// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace EncoDeck.Bluetooth.Gatt;

/// <summary>
/// Ordinary byte stream over a write characteristic and a notifying characteristic (Sony MDR over
/// GATT, AKG BES, Airoha RACE, Xiaomi MMA). The brand codecs re-frame the stream, so notifications
/// may split or join frames. The LE endpoint is bound to the selected headset's address (or, for
/// <see cref="GattStreamEndpoint.BondedLeIdentities"/>, to the earbuds' own LE Audio bonds); only
/// endpoints marked <see cref="GattStreamEndpoint.NameMatchedLeFallback"/> may fall back to one
/// name-matched LE advertiser.
/// </summary>
internal sealed class StreamGattByteChannel : IDuplexByteChannel
{
    /// <summary>Minimum ATT payload; unacknowledged writes fit it when the MTU is unknown.</summary>
    private const int MinimumAttPayload = 20;

    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _notify;
    private readonly GattCharacteristic _write;
    private readonly GattWriteOption _writeOption;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly ulong? _stallKey;
    private readonly LeTarget _target;
    private readonly ILogger _logger;
    private byte[]? _pending;
    private int _offset;
    private int _disposed;
    private int _answered;

    private StreamGattByteChannel(BluetoothLEDevice device, GattDeviceService service, GattCharacteristic notify, GattCharacteristic write, GattStreamEndpoint endpoint,
        LeTarget target, ulong? stallKey, ILogger logger)
    {
        _device = device;
        _service = service;
        _notify = notify;
        _write = write;
        _target = target;
        _stallKey = stallKey;
        _logger = logger;
        Description = endpoint.Name;
        _writeOption = ChooseWriteOption(write.CharacteristicProperties, endpoint.PreferUnacknowledgedWrites);
        notify.ValueChanged += OnValueChanged;
        device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public string Description { get; }

    /// <summary>
    /// Acknowledged writes when the characteristic declares them, unless the endpoint prefers
    /// unacknowledged writes and the characteristic also allows those.
    /// </summary>
    internal static GattWriteOption ChooseWriteOption(GattCharacteristicProperties properties, bool preferUnacknowledged)
    {
        bool acknowledged = properties.HasFlag(GattCharacteristicProperties.Write);
        bool unacknowledged = properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        return acknowledged && !(preferUnacknowledged && unacknowledged) ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
    }

    /// <summary>
    /// Opens the endpoint at the selected headset's address; endpoints that allow it then fall back to
    /// one name-matched LE advertiser (see <see cref="LeCompanionResolver"/>).
    /// </summary>
    internal static Task<ChannelOpenResult> OpenAsync(ulong address, string? name, GattStreamEndpoint endpoint, ILogger logger, CancellationToken cancellationToken) =>
        endpoint.BondedLeIdentities
            ? OpenOnBondedLeAsync(address, name, endpoint, logger, cancellationToken)
            : LeCompanionResolver.OpenAsync(address, name, endpoint, LeCompanionCache.Shared,
                (target, expectedName, ct) => OpenAtAsync(address, target, expectedName, endpoint, null, logger, ct), ScanAsync, logger, cancellationToken);

    /// <summary>
    /// Opens <paramref name="endpoint"/> on a connected LE Audio identity of these earbuds: the one at the
    /// public address or the other earbud's bond, so one earbud worn alone answers whichever it is. Only
    /// bonds of the selected earbuds are tried, and none when they are not on LE Audio.
    /// </summary>
    private static async Task<ChannelOpenResult> OpenOnBondedLeAsync(ulong address, string? name, GattStreamEndpoint endpoint, ILogger logger, CancellationToken cancellationToken)
    {
        var connected = new List<LeTarget>();
        try
        {
            // Retryable, like the Classic outcome it follows: a Classic-only pairing still answers over SPP once its link settles.
            if (!await LeAudioLink.IsBondedAsync(address, cancellationToken).ConfigureAwait(false))
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, $"{endpoint.Name}: the earbuds are not bonded over LE.");
            using (var primary = await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public).AsTask(cancellationToken).ConfigureAwait(false))
            {
                if (primary?.ConnectionStatus == BluetoothConnectionStatus.Connected) connected.Add(new(address, false));
            }
            var secondaries = await LeAudioPairing.FindSecondaryBondsAsync(address, name, cancellationToken).ConfigureAwait(false);
            foreach (var le in secondaries)
            {
                using (le)
                {
                    if (le.ConnectionStatus == BluetoothConnectionStatus.Connected)
                        connected.Add(new(le.BluetoothAddress, le.BluetoothAddressType == BluetoothAddressType.Random));
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or COMException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"{endpoint.Name}: 0x{ex.HResult:X8}");
        }
        ChannelOpenResult? failure = null;
        // An identity whose last write stalled keeps its ATT bearer busy until the ATT timeout: try it last.
        var stalled = GattWriteStallRegistry.Shared.RecentlyStalled(address, Environment.TickCount64);
        var ordered = GattWriteStallRegistry.Order(connected, stalled);
        logger.LogInformation("{Endpoint}: {Count} connected LE Audio identities{Stalled}", endpoint.Name, ordered.Count,
            stalled is not null && ordered.Contains(stalled) ? $", {Describe(stalled)} last (write stalled recently)" : "");
        foreach (var target in ordered)
        {
            var result = await OpenAtAsync(address, target, null, endpoint, address, logger, cancellationToken).ConfigureAwait(false);
            if (result.Status == ChannelOpenStatus.Opened) return result;
            logger.LogInformation("{Endpoint} on LE identity {Identity} failed: {Detail}", endpoint.Name, Describe(target), result.Detail);
            failure = result;
        }
        return failure ?? ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, $"{endpoint.Name}: no LE Audio link is up.");
    }

    /// <summary>
    /// Opens <paramref name="endpoint"/> at the classic address (<paramref name="target"/> null) or at an LE companion.
    /// With <paramref name="stallKey"/>, a stalled write is remembered for that earbuds' next attempt.
    /// </summary>
    private static async Task<ChannelOpenResult> OpenAtAsync(ulong address, LeTarget? target, string? expectedName, GattStreamEndpoint endpoint, ulong? stallKey,
        ILogger logger, CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        StreamGattByteChannel? channel = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (target is not null)
            {
                // A companion chosen by LeCompanionResolver: exactly one advertiser matched the paired name.
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(target.Address,
                    target.IsRandom ? BluetoothAddressType.Random : BluetoothAddressType.Public).AsTask(timeout.Token).ConfigureAwait(false);
                if (device is null)
                    return ChannelOpenResult.Failed(ChannelOpenStatus.ControlEndpointNotFound, $"{endpoint.Name}: LE companion not found.");
                if (expectedName is not null && !string.IsNullOrEmpty(device.Name) && !LeCompanionResolver.NameMatches(expectedName, device.Name))
                    return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, $"{endpoint.Name}: LE companion reports a different name.");
            }
            else
            {
                // Never attach to a different nearby headset because its name or service matches.
                string? id = await DiscoverEndpointAsync(address, timeout.Token).ConfigureAwait(false);
                device = id is not null
                    ? await BluetoothLEDevice.FromIdAsync(id).AsTask(timeout.Token).ConfigureAwait(false)
                    : await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(timeout.Token).ConfigureAwait(false);
                if (device is null)
                    return ChannelOpenResult.Failed(ChannelOpenStatus.ControlEndpointNotFound, $"{endpoint.Name}: LE endpoint not found at the selected address.");
            }
            if (await device.RequestAccessAsync().AsTask(timeout.Token).ConfigureAwait(false) != DeviceAccessStatus.Allowed)
                return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, $"{endpoint.Name}: access denied.");
            var services = await device.GetGattServicesForUuidAsync(endpoint.Service, BluetoothCacheMode.Uncached).AsTask(timeout.Token).ConfigureAwait(false);
            foreach (var candidate in services.Services)
            {
                if (service is null) service = candidate;
                else candidate.Dispose();
            }
            if (services.Status != GattCommunicationStatus.Success || service is null)
                return ChannelOpenResult.Failed(services.Status == GattCommunicationStatus.Success ? ChannelOpenStatus.ServiceNotFound : ChannelOpenStatus.ConnectFailed,
                    $"{endpoint.Name} service: {services.Status}");
            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token).ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"{endpoint.Name} characteristics: {characteristics.Status}");
            const GattCharacteristicProperties writable = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse;
            const GattCharacteristicProperties notifying = GattCharacteristicProperties.Notify | GattCharacteristicProperties.Indicate;
            // The documented characteristic first; otherwise the first one with the right property.
            var write = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == endpoint.Write && (c.CharacteristicProperties & writable) != 0)
                ?? characteristics.Characteristics.FirstOrDefault(c => (c.CharacteristicProperties & writable) != 0);
            var notify = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == endpoint.Notify && (c.CharacteristicProperties & notifying) != 0)
                ?? characteristics.Characteristics.FirstOrDefault(c => (c.CharacteristicProperties & notifying) != 0);
            if (write is null || notify is null || (write.CharacteristicProperties & writable) == 0 || (notify.CharacteristicProperties & notifying) == 0)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, $"{endpoint.Name}: write or notify characteristic missing.");
            var subscription = notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
            var identity = target ?? new LeTarget(address, false);
            channel = new StreamGattByteChannel(device, service, notify, write, endpoint, identity, stallKey, logger);
            device = null;
            service = null;
            var status = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(subscription).AsTask(timeout.Token).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"{endpoint.Name} subscription: {status}");
            if (channel._device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, $"{endpoint.Name}: disconnected during subscription.");
            logger.LogInformation("{Endpoint} opened on LE identity {Identity}: write {WriteOption} (properties {WriteProperties}), notify {NotifyProperties}, max PDU {MaxPdu}",
                endpoint.Name, Describe(identity), channel._writeOption, write.CharacteristicProperties, notify.CharacteristicProperties, channel.MaxPduSize());
            var result = new ChannelOpenResult(ChannelOpenStatus.Opened, channel, endpoint.Service, null);
            channel = null;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"{endpoint.Name}: connection timed out.");
        }
        catch (UnauthorizedAccessException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, $"{endpoint.Name}: access denied.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"{endpoint.Name}: 0x{ex.HResult:X8}");
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            service?.Dispose();
            device?.Dispose();
        }
    }

    /// <summary>Active LE scan for <see cref="LeCompanionResolver.ScanWindow"/>; name and service UUIDs are merged per advertiser.</summary>
    private static async Task<IReadOnlyList<LeAdvertisement>> ScanAsync(CancellationToken cancellationToken)
    {
        var seen = new Dictionary<(ulong, bool), (string? Name, HashSet<Guid> Services)>();
        var gate = new object();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var key = (args.BluetoothAddress, args.BluetoothAddressType == BluetoothAddressType.Random);
            lock (gate)
            {
                var entry = seen.TryGetValue(key, out var known) ? known : (null, []);
                if (!string.IsNullOrEmpty(args.Advertisement.LocalName)) entry.Name = args.Advertisement.LocalName;
                foreach (var uuid in args.Advertisement.ServiceUuids) entry.Services.Add(uuid);
                seen[key] = entry;
            }
        }
        watcher.Received += Received;
        try
        {
            watcher.Start();
            await Task.Delay(LeCompanionResolver.ScanWindow, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            watcher.Received -= Received;
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
        }
        lock (gate)
            return seen.Select(e => new LeAdvertisement(new(e.Key.Item1, e.Key.Item2), e.Value.Name, e.Value.Services.ToArray())).ToArray();
    }

    private static async Task<string?> DiscoverEndpointAsync(ulong address, CancellationToken cancellationToken)
    {
        var found = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = DeviceInformation.CreateWatcher(
            BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(address, BluetoothAddressType.Public),
            [], DeviceInformationKind.AssociationEndpoint);
        void Added(DeviceWatcher sender, DeviceInformation info) => found.TrySetResult(info.Id);
        void Stopped(DeviceWatcher sender, object args) => found.TrySetResult(null);
        watcher.Added += Added;
        watcher.Stopped += Stopped;
        try
        {
            watcher.Start();
            return await found.Task.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
        finally
        {
            watcher.Added -= Added;
            watcher.Stopped -= Stopped;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        }
    }

    private static string Describe(LeTarget target) =>
        $"{new EncoDeck.Core.Privacy.BluetoothAddress(target.Address)} ({(target.IsRandom ? "random" : "public")})";

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (args.CharacteristicValue.Length == 0) return;
        if (_stallKey is { } key && Interlocked.Exchange(ref _answered, 1) == 0) GattWriteStallRegistry.Shared.MarkHealthy(key, _target);
        _incoming.Writer.TryWrite(args.CharacteristicValue.ToArray());
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) _incoming.Writer.TryComplete();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty) return 0;
        while (_pending is null)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            if (!_incoming.Reader.TryRead(out _pending) || _pending.Length == 0) _pending = null;
            _offset = 0;
        }
        int count = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        if (_offset == _pending.Length) _pending = null;
        return count;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // Acknowledged writes may use long writes; unacknowledged ones must fit the minimum ATT payload.
        int chunk = _writeOption == GattWriteOption.WriteWithResponse ? Math.Max(data.Length, 1) : UnacknowledgedPayload();
        for (int offset = 0; offset < data.Length; offset += chunk)
        {
            var part = data.Slice(offset, Math.Min(chunk, data.Length - offset));
            var buffer = MemoryMarshal.TryGetArray(part, out var segment)
                ? segment.Array!.AsBuffer(segment.Offset, segment.Count)
                : part.ToArray().AsBuffer();
            GattWriteResult result;
            try
            {
                result = await _write.WriteValueWithResultAsync(buffer, _writeOption).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The abandoned request may still occupy this identity's ATT bearer: the next attempt tries another identity first.
                if (_stallKey is { } key) GattWriteStallRegistry.Shared.MarkStalled(key, _target, Environment.TickCount64);
                _logger.LogWarning("{Endpoint} write ({WriteOption}, {Length} bytes) on LE identity {Identity} did not complete",
                    Description, _writeOption, part.Length, Describe(_target));
                throw;
            }
            if (result.Status != GattCommunicationStatus.Success)
                throw new IOException(result.ProtocolError is { } error
                    ? $"{Description} write: {result.Status}, ATT error 0x{error:X2}"
                    : $"{Description} write: {result.Status}");
        }
    }

    /// <summary>Negotiated ATT MTU minus the 3-byte header, so a frame stays in one write where the MTU allows.</summary>
    private int UnacknowledgedPayload() => MaxPduSize() is { } pdu ? Math.Max(MinimumAttPayload, pdu - 3) : MinimumAttPayload;

    private int? MaxPduSize()
    {
        try
        {
            return _service.Session.MaxPduSize;
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _incoming.Writer.TryComplete();
        _notify.ValueChanged -= OnValueChanged;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                .AsTask(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* A disconnected device may no longer accept CCCD writes. */ }
        finally
        {
            try { _service.Dispose(); }
            finally { _device.Dispose(); }
        }
    }
}
