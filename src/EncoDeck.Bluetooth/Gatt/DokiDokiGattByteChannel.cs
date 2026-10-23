// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Protocol.Brands.DokiDoki;
using EncoDeck.Protocol.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace EncoDeck.Bluetooth.Gatt;

/// <summary>
/// DokiDoki FFAC service over GATT. Heartbeat frames go to FFB5 (fallback FFB7), every other
/// frame to FFB7 (fallback FFB5); FFB8 notifications are surfaced as reads.
/// </summary>
internal sealed class DokiDokiGattByteChannel : IDuplexByteChannel
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);

    private readonly BluetoothLEDevice _device;
    private readonly GattSession _session;
    private readonly GattDeviceService _service;
    private readonly Route _action;
    private readonly Route _heartbeat;
    private readonly GattCharacteristic _notify;
    private readonly Channel<byte[]> _notifications = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private bool _notifySubscribed;
    private int _stopped;
    private int _disposed;

    private readonly record struct Route(GattCharacteristic Characteristic, GattWriteOption Option);

    private DokiDokiGattByteChannel(BluetoothLEDevice device, GattSession session, GattDeviceService service,
        Route action, Route heartbeat, GattCharacteristic notify)
    {
        _device = device;
        _session = session;
        _service = service;
        _action = action;
        _heartbeat = heartbeat;
        _notify = notify;
        _shutdownToken = _shutdown.Token;
        _notify.ValueChanged += OnValueChanged;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public string Description => "DokiDoki GATT";

    internal static async Task<ChannelOpenResult> OpenAsync(ulong address, BluetoothAddressType addressType, CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattSession? session = null;
        GattDeviceService? service = null;
        DokiDokiGattByteChannel? channel = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OpenTimeout);
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType).AsTask(timeout.Token).ConfigureAwait(false);
            if (device is null)
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "Windows returned no Bluetooth LE device for the advertised address.");
            var access = await device.RequestAccessAsync().AsTask(timeout.Token).ConfigureAwait(false);
            if (access != DeviceAccessStatus.Allowed)
                return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, $"BLE device access: {access}");

            // Unpaired LE peripherals drop the link as soon as nothing holds it.
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(timeout.Token).ConfigureAwait(false);
            if (session is null) return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "Windows could not create a GATT session.");
            session.MaintainConnection = true;

            var services = await device.GetGattServicesForUuidAsync(DokiDokiConstants.ServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(timeout.Token).ConfigureAwait(false);
            for (int i = 0; i < services.Services.Count; i++)
            {
                if (i == 0) service = services.Services[i];
                else services.Services[i].Dispose();
            }
            if (services.Status != GattCommunicationStatus.Success) return Failed(services.Status, "FFAC service discovery");
            if (service is null)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound,
                    "DokiDoki service FFAC not found. If only standard services are visible, another app (e.g. the official client) may hold the device.");

            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token).ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success) return Failed(characteristics.Status, "FFAC characteristic discovery");
            var all = characteristics.Characteristics;
            var withResponse = Writable(all, DokiDokiConstants.WriteWithResponseCharacteristicUuid, GattCharacteristicProperties.Write, GattWriteOption.WriteWithResponse);
            var withoutResponse = Writable(all, DokiDokiConstants.WriteWithoutResponseCharacteristicUuid, GattCharacteristicProperties.WriteWithoutResponse, GattWriteOption.WriteWithoutResponse);
            var notify = all.FirstOrDefault(c => c.Uuid == DokiDokiConstants.NotifyCharacteristicUuid);
            if ((withoutResponse ?? withResponse) is not { } action || (withResponse ?? withoutResponse) is not { } heartbeat)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "Neither FFB5 nor FFB7 is writable.");
            if (notify is null) return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "Notify characteristic FFB8 is missing.");

            channel = new DokiDokiGattByteChannel(device, session, service, action, heartbeat, notify);
            device = null;
            session = null;
            service = null;
            if (notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                using var connected = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, channel._shutdownToken);
                // Yilia treats a failed subscription as non-fatal; so do we (notifications carry no known meaning).
                var status = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    .AsTask(connected.Token).ConfigureAwait(false);
                channel._notifySubscribed = status == GattCommunicationStatus.Success;
            }
            if (channel._device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "The device disconnected during setup.");
            var result = new ChannelOpenResult(ChannelOpenStatus.Opened, channel, DokiDokiConstants.ServiceUuid, null);
            channel = null;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "DokiDoki GATT connection timed out or disconnected.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"DokiDoki GATT: 0x{ex.HResult:X8}");
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            service?.Dispose();
            session?.Dispose();
            device?.Dispose();
        }
    }

    private static Route? Writable(IReadOnlyList<GattCharacteristic> all, Guid uuid, GattCharacteristicProperties property, GattWriteOption option) =>
        all.FirstOrDefault(c => c.Uuid == uuid) is { } c && c.CharacteristicProperties.HasFlag(property) ? new Route(c, option) : null;

    private static ChannelOpenResult Failed(GattCommunicationStatus status, string operation) =>
        ChannelOpenResult.Failed(status switch
        {
            GattCommunicationStatus.AccessDenied => ChannelOpenStatus.AccessDenied,
            GattCommunicationStatus.Unreachable => ChannelOpenStatus.DeviceUnreachable,
            _ => ChannelOpenStatus.ConnectFailed,
        }, $"{operation}: {status}");

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (Volatile.Read(ref _stopped) == 0 && args.CharacteristicValue.Length > 0)
            _notifications.Writer.TryWrite(args.CharacteristicValue.ToArray());
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) Stop();
    }

    private void Stop()
    {
        lock (_shutdown)
        {
            if (_stopped != 0) return;
            Volatile.Write(ref _stopped, 1);
            _shutdown.Cancel();
            _notifications.Writer.TryComplete();
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty) throw new ArgumentException("A notification requires a nonempty read buffer.", nameof(buffer));
        while (await _notifications.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_shutdownToken.IsCancellationRequested) return 0;
            if (!_notifications.Reader.TryRead(out var message)) continue;
            int length = Math.Min(message.Length, buffer.Length);
            message.AsSpan(0, length).CopyTo(buffer.Span);
            return length;
        }
        return 0;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_shutdownToken.IsCancellationRequested) throw new IOException("DokiDoki GATT device is disconnected.");
        cancellationToken.ThrowIfCancellationRequested();
        if (data.IsEmpty) return;
        var route = DokiDokiCommands.IsHeartbeatFrame(data.Span) ? _heartbeat : _action;
        IBuffer buffer = MemoryMarshal.TryGetArray(data, out var segment)
            ? segment.Array!.AsBuffer(segment.Offset, segment.Count)
            : data.ToArray().AsBuffer();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        linked.CancelAfter(WriteTimeout);
        GattCommunicationStatus status;
        try
        {
            status = await route.Characteristic.WriteValueAsync(buffer, route.Option).AsTask(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A disconnect or write timeout is a transport failure, not a caller cancellation.
            throw new IOException("DokiDoki GATT write timed out or the device disconnected.");
        }
        if (status != GattCommunicationStatus.Success)
        {
            if (status == GattCommunicationStatus.Unreachable) Stop();
            throw new IOException($"DokiDoki GATT write failed: {status}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _notify.ValueChanged -= OnValueChanged;
        try
        {
            if (_notifySubscribed)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                    .AsTask(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or COMException)
        {
            // Disconnect/cancel may make the remote CCCD unreachable; local handles still must close.
        }
        finally
        {
            try { _session.MaintainConnection = false; }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or COMException) { }
            try { _service.Dispose(); }
            finally
            {
                try { _session.Dispose(); }
                finally
                {
                    try { _device.Dispose(); }
                    finally { _shutdown.Dispose(); }
                }
            }
        }
    }
}
