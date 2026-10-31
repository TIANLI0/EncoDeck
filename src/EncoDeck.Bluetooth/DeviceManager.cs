// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using EncoDeck.Bluetooth.Control;
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Connection;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;
using EncoDeck.Bluetooth.Usb;
using EncoDeck.Protocol.Brands.Usb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Bluetooth;

/// <summary>A paired device EncoDeck has a profile for.</summary>
public sealed record KnownDevice(DeviceIdentity Identity, IDeviceProfile Profile, bool IsConnected, bool IsActive);

public interface IDeviceManager : IDeviceCommandService, IAdvancedDeviceCommands, IAsyncDisposable
{
    IReadOnlyList<KnownDevice> KnownDevices { get; }

    /// <summary>Raised when the list of known devices or their link state changes.</summary>
    event EventHandler? KnownDevicesChanged;

    /// <summary>Raised when the active device's Bluetooth link comes up (drives the connection popup).</summary>
    event EventHandler<DeviceIdentity>? ActiveDeviceLinkConnected;

    /// <summary>Raised when the active device's case lid opens while connected.</summary>
    event EventHandler<DeviceIdentity>? ActiveDeviceCaseLidOpened;

    void Start();

    Task SelectDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Call after the system resumes from sleep: re-validates the control channel.</summary>
    void NotifySystemResumed();
}

/// <summary>
/// Composition root of the Bluetooth runtime: watches paired devices, picks the active supported
/// device, runs its <see cref="ControlChannelSupervisor"/>, and routes commands to it.
/// </summary>
public sealed class DeviceManager : IDeviceManager
{
    private readonly PairedDeviceWatcher _watcher;
    private readonly IDeviceProfileRegistry _profiles;
    private readonly IDeviceStateStore _store;
    private readonly IChannelConnector _connector;
    private readonly Func<string?> _preferredDeviceId;
    private readonly Func<bool> _allowExperimental;
    private readonly ReconnectPolicy _policy;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly IProtocolTrace? _trace;
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ActiveDevice? _active;
    private readonly UsbHeadsetWatcher _usbWatcher = new();
    private DeviceIdentity? _usbIdentity;
    private CancellationTokenSource? _usbStop;
    private Task? _usbPoll;
    private readonly SemaphoreSlim _usbReadGate = new(1, 1);
    private UsbHeadsetSettingsController? _usbSettings;
    private string? _manualSelection;
    // The most recently connected device wins automatic selection over earlier choices.
    private readonly object _connectionGate = new();
    private readonly Dictionary<string, long> _connectedAt = [];
    private long _sequence;
    private long _selectedAt;
    private bool _startupTracked;
    private long _startedAt;
    private IReadOnlyList<PairedDevice> _bridgeDevices = [];

    public DeviceManager(
        IDeviceProfileRegistry profiles,
        IDeviceStateStore store,
        IChannelConnector connector,
        Func<string?> preferredDeviceId,
        Func<bool> allowExperimental,
        ILoggerFactory? loggers = null,
        ReconnectPolicy? policy = null,
        IProtocolTrace? trace = null)
    {
        _profiles = profiles;
        _store = store;
        _connector = connector;
        _preferredDeviceId = preferredDeviceId;
        _allowExperimental = allowExperimental;
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<DeviceManager>();
        _policy = policy ?? ReconnectPolicy.Default;
        _trace = trace;
        _watcher = new PairedDeviceWatcher(_loggers.CreateLogger<PairedDeviceWatcher>());
        _watcher.Changed += OnPairedDevicesChanged;
        _usbWatcher.Changed += OnPairedDevicesChanged;
    }

    public IReadOnlyList<KnownDevice> KnownDevices
    {
        get
        {
            string? activeId = _usbIdentity?.DeviceId ?? _active?.Identity.DeviceId;
            var paired = _watcher.Devices;
            var bridgeOnly = _bridgeDevices.Where(b => !paired.Any(p => p.Identity.Address == b.Identity.Address)).ToList();
            return paired.Concat(bridgeOnly).Concat(_usbWatcher.Devices)
                .Select(d => (Device: d, Profile: _profiles.Match(d.Identity)))
                .Where(x => x.Profile is not null)
                // Only the bridge links earbuds Windows never paired: their control channel must be one it can open.
                .Where(x => !bridgeOnly.Contains(x.Device) || IsBridgeable(x.Profile!))
                // A USB endpoint name can also contain a Bluetooth model name; never open Bluetooth control for it.
                .Where(x => x.Device.Identity.Transport != DeviceTransport.Usb || x.Profile is UsbHeadsetProfile)
                // The read-only generic profile matches by name ("OPPO …"), which also hits phones and
                // watches; only accept it for audio devices. Exact model profiles are trusted as-is.
                .Where(x => !IsGeneric(x.Profile!) || x.Device.IsAudioDevice)
                .Select(x => new KnownDevice(x.Device.Identity, x.Profile!, x.Device.IsConnected, x.Device.Identity.DeviceId == activeId))
                .ToList();
        }
    }

    public event EventHandler? KnownDevicesChanged;

    /// <summary>
    /// Opens control channels through A2DP Windows Bridge, for earbuds only the bridge is linked to; null without
    /// one. Read when a device is selected.
    /// </summary>
    public IChannelConnector? BridgeConnector { get; set; }

    /// <summary>
    /// Earbuds A2DP Windows Bridge has connection profiles for, connected while it streams to them. Those Windows has
    /// not paired (paired with the bridge's own adapter alone) are listed as devices of their own, with ids that are
    /// not Windows': nothing opens them through Windows.
    /// </summary>
    public void SetBridgeDevices(IReadOnlyList<PairedDevice> devices)
    {
        if (devices.SequenceEqual(_bridgeDevices)) return;
        _bridgeDevices = devices;
        OnPairedDevicesChanged(this, EventArgs.Empty);
    }

    public event EventHandler<DeviceIdentity>? ActiveDeviceLinkConnected;

    public event EventHandler<DeviceIdentity>? ActiveDeviceCaseLidOpened;

    public void Start() { _startedAt = Environment.TickCount64; _watcher.Start(); _usbWatcher.Start(); }

    public async Task SelectDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        _manualSelection = deviceId;
        lock (_connectionGate) _selectedAt = ++_sequence;
        await ReselectAsync(cancellationToken).ConfigureAwait(false);
    }

    public void NotifySystemResumed()
    {
        // Sockets opened before sleep are often dead without an error; force a clean reconnect.
        _active?.Supervisor.RequestReconnect();
    }

    private void OnPairedDevicesChanged(object? sender, EventArgs e)
    {
        TrackConnections();
        KnownDevicesChanged?.Invoke(this, EventArgs.Empty);
        // Wait for Bluetooth enumeration so a USB receiver found first does not briefly take over.
        if (!_watcher.IsEnumerated) return;
        _ = ReselectAsync(_lifetime.Token);
    }

    private void TrackConnections()
    {
        lock (_connectionGate)
        {
            // Snapshot under the gate: the Bluetooth and USB watchers raise Changed on different
            // threads, and an older snapshot applied after a newer one would corrupt _connectedAt.
            var connected = KnownDevices.Where(d => d.IsConnected).Select(d => d.Identity.DeviceId).ToHashSet();
            // Devices already connected at startup are not newer than the saved preference.
            bool startup = !_startupTracked;
            // A USB watcher that never completes must not keep newer connections from winning.
            _startupTracked |= _watcher.IsEnumerated && (_usbWatcher.IsEnumerated || Environment.TickCount64 - _startedAt > 10_000);
            foreach (var id in _connectedAt.Keys.Where(id => !connected.Contains(id)).ToList()) _connectedAt.Remove(id);
            foreach (var id in connected) _connectedAt.TryAdd(id, startup ? 0 : ++_sequence);
        }
    }

    private KnownDevice? NewestConnection(IReadOnlyList<KnownDevice> candidates)
    {
        lock (_connectionGate)
        {
            return candidates
                .Select(c => (Device: c, At: c.IsConnected && _connectedAt.TryGetValue(c.Identity.DeviceId, out long at) ? at : 0))
                .Where(x => x.At > _selectedAt)
                .OrderByDescending(x => x.At).Select(x => x.Device).FirstOrDefault();
        }
    }

    private async Task ReselectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _selectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var candidates = KnownDevices;
            string? preferred = _manualSelection ?? _preferredDeviceId();
            var choice = NewestConnection(candidates)
                ?? candidates.FirstOrDefault(c => c.Identity.DeviceId == preferred)
                // Without a newer connection, a USB receiver (always "connected" even with the
                // headset off) yields to connected Bluetooth devices; the user can still pick it manually.
                ?? candidates.Where(c => c.IsConnected).OrderBy(c => c.Identity.Transport == DeviceTransport.Usb)
                    .ThenByDescending(c => c.Profile.MatchScore(c.Identity)).FirstOrDefault()
                ?? candidates.FirstOrDefault(c => c.Identity.DeviceId == _active?.Identity.DeviceId)
                ?? candidates.OrderByDescending(c => c.Profile.MatchScore(c.Identity)).FirstOrDefault();

            if (choice?.Identity.DeviceId == (_usbIdentity?.DeviceId ?? _active?.Identity.DeviceId)
                // Windows had no radio for it: open it again (the radio may be back) unless the bridge carries it now.
                && _active is not { WindowsUnavailable: true, BridgeOnly: false, Link.IsConnected: false }) return;

            await StopUsbAsync().ConfigureAwait(false);

            if (_active is { } old)
            {
                _active = null;
                await old.DisposeAsync().ConfigureAwait(false);
            }

            if (choice is null)
            {
                _store.Reset(DeviceState.Empty);
                return;
            }

            if (choice.Identity.Transport == DeviceTransport.Usb && choice.Profile is UsbHeadsetProfile usb)
            {
                _logger.LogInformation("Active USB device: {Model}", usb.ModelId);
                _usbIdentity = choice.Identity;
                _usbStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _store.Reset(DeviceState.Empty with { Device = choice.Identity, Profile = usb, Connection = DeviceConnectionState.UsbReceiverConnected });
                _store.TryUpdate(choice.Identity.DeviceId, s => s with { Battery = BatteryStatus.Unknown });
                await RefreshUsbAsync(_usbStop.Token).ConfigureAwait(false);
                _usbPoll = PollUsbAsync(_usbStop.Token);
                KnownDevicesChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            _logger.LogInformation("Active device: {Device} ({Model})", choice.Identity, choice.Profile.ModelId);
            _store.Reset(DeviceState.Empty with
            {
                Device = choice.Identity,
                Profile = choice.Profile,
                Connection = choice.IsConnected ? DeviceConnectionState.AudioConnected : DeviceConnectionState.PairedNotConnected,
            });
            // The device cache has now restored what these earbuds reported last time: show the
            // same model and settings as then, before the control channel even opens.
            var profile = choice.Profile;
            if (profile is IControllableProfile matched)
            {
                var remembered = matched.Remember(_store.Current);
                if (!ReferenceEquals(remembered, matched))
                {
                    profile = remembered;
                    _store.TryUpdate(choice.Identity.DeviceId, s => s with { Profile = remembered });
                }
            }

            ILinkStateSource? link;
            bool bridgeable = BridgeConnector is not null && IsBridgeable(profile);
            // Paired with A2DP Windows Bridge alone: Windows does not know the device.
            bool bridgeOnly = _bridgeDevices.Any(d => d.Identity.DeviceId == choice.Identity.DeviceId);
            try
            {
                link = bridgeOnly ? null : await BluetoothLinkMonitor.CreateAsync(
                    choice.Identity.DeviceId,
                    profile.Form == DeviceForm.Headphones,
                    profile.ManagesLeAudioLink,
                    cancellationToken).ConfigureAwait(false);
            }
            // 0x800710DF (device not ready): Windows has no Bluetooth radio left, typically because A2DP Windows
            // Bridge took the only adapter over (WinUSB). The bridge's own link is then the only way to the earbuds.
            catch (COMException ex) when (bridgeable)
            {
                _logger.LogWarning("Windows cannot open {Device} (0x{HResult:X8}): control only through A2DP Windows Bridge", choice.Identity, ex.HResult);
                link = null;
            }
            bool windowsUnavailable = link is null && bridgeable;
            if (windowsUnavailable) link = new NoWindowsLink();
            if (link is null)
            {
                _logger.LogWarning("Could not open {Device} for link monitoring", choice.Identity);
                _store.TryUpdate(choice.Identity.DeviceId, s => s with { Connection = DeviceConnectionState.ControlUnavailable, LastError = new DeviceError("Windows could not open the Bluetooth device.", CommandStatus.TransportError, DateTimeOffset.UtcNow) });
                return;
            }

            if (profile is not IControllableProfile controllable)
            {
                link.Dispose();
                return;
            }

            // While A2DP Windows Bridge alone streams to the earbuds, their control channel runs through it.
            ILinkStateSource supervised = link;
            var connector = _connector;
            if (BridgeConnector is { } bridge && controllable.ControlServiceUuids.Any(RfcommChannelConnector.IsRfcommService))
            {
                var bridged = new BridgedLinkState(link, _store, choice.Identity.DeviceId);
                supervised = bridged;
                connector = new BridgeRoutingConnector(_connector, bridge, bridged);
            }
            var supervisor = new ControlChannelSupervisor(choice.Identity, controllable, supervised, connector, _store, _policy, _allowExperimental, _loggers, trace: _trace);
            var identity = choice.Identity;
            supervisor.LinkConnected += (_, _) => ActiveDeviceLinkConnected?.Invoke(this, identity);
            supervisor.CaseLidOpened += (_, _) => ActiveDeviceCaseLidOpened?.Invoke(this, identity);
            _active = new ActiveDevice(identity, supervised, supervisor, windowsUnavailable, bridgeOnly);
            supervisor.Start();
            KnownDevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device selection failed");
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private static bool IsGeneric(IDeviceProfile profile) => profile is IControllableProfile { IsGeneric: true };

    private static bool IsBridgeable(IDeviceProfile profile) =>
        profile is IControllableProfile { ControlServiceUuids: var uuids } && uuids.Any(RfcommChannelConnector.IsRfcommService);

    private IDeviceController? Controller => _active?.Supervisor.Controller;

    public Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken = default) =>
        Controller is { } c ? c.SetNoiseControlAsync(target, cancellationToken) : Task.FromResult(CommandResult.NotConnected);

    public Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken = default) =>
        _usbSettings is { } usb ? usb.SetEqualizerPresetAsync(presetId, cancellationToken) :
        Controller is { } c ? c.SetEqualizerPresetAsync(presetId, cancellationToken) : Task.FromResult(CommandResult.NotConnected);

    public Task<CommandResult> SetEqualizerBandsAsync(IReadOnlyList<int> bands, CancellationToken cancellationToken = default) =>
        Controller is { } c ? c.SetEqualizerBandsAsync(bands, cancellationToken) : Task.FromResult(CommandResult.NotConnected);

    public Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken = default) =>
        _usbSettings is { } usb ? usb.SetToggleAsync(toggle, enabled, cancellationToken) :
        Controller is { } c ? c.SetToggleAsync(toggle, enabled, cancellationToken) : Task.FromResult(CommandResult.NotConnected);

    public Task<CommandResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        _usbIdentity is not null ? RefreshUsbAsync(cancellationToken, forceSettings: true) : Controller is { } c ? c.RefreshAsync(cancellationToken) : Task.FromResult(CommandResult.NotConnected);

    private Task<CommandResult> WithController(Func<IDeviceController, Task<CommandResult>> action) =>
        Controller is { } c ? action(c) : Task.FromResult(CommandResult.NotConnected);

    public Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default) => WithController(c => c.SetSpatialAudioAsync(mode, cancellationToken));

    public Task<CommandResult> SetLeBrSwitchAsync(bool enabled, CancellationToken cancellationToken = default) => WithController(c => c.SetLeBrSwitchAsync(enabled, cancellationToken));

    public Task<CommandResult> ReportLeAudioTransportAsync(CancellationToken cancellationToken = default) => WithController(c => c.ReportLeAudioTransportAsync(cancellationToken));

    public Task<CommandResult> SetGameSoundAsync(int type, CancellationToken cancellationToken = default) => WithController(c => c.SetGameSoundAsync(type, cancellationToken));

    public Task<CommandResult> RefreshGameModeAsync(CancellationToken cancellationToken = default) => WithController(c => c.RefreshGameModeAsync(cancellationToken));

    public Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default) => WithController(c => c.SetPromptVolumeAsync(level, cancellationToken));

    public Task<CommandResult> SetGainAsync(int level, CancellationToken cancellationToken = default) => WithController(c => c.SetGainAsync(level, cancellationToken));

    public Task<CommandResult> SetBassEngineLevelAsync(int level, CancellationToken cancellationToken = default) => WithController(c => c.SetBassEngineLevelAsync(level, cancellationToken));

    public Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default) => WithController(c => c.SetKeyFunctionAsync(assignment, cancellationToken));

    public Task<CommandResult> SetNoiseCycleAsync(EarSide side, uint modeMask, CancellationToken cancellationToken = default) => WithController(c => c.SetNoiseCycleAsync(side, modeMask, cancellationToken));

    public Task<CommandResult> SetHeadMotionTypeAsync(int type, CancellationToken cancellationToken = default) => WithController(c => c.SetHeadMotionTypeAsync(type, cancellationToken));

    public Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default) => WithController(c => c.FindEarbudsAsync(ring, cancellationToken));

    public Task<CommandResult> RunFitTestAsync(CancellationToken cancellationToken = default) => WithController(c => c.RunFitTestAsync(cancellationToken));

    public Task<CommandResult> MultiConnectAsync(MultiConnectOperation operation, byte[] address, CancellationToken cancellationToken = default) => WithController(c => c.MultiConnectAsync(operation, address, cancellationToken));

    public Task<CommandResult> SetPreferredDeviceAsync(byte[]? address, CancellationToken cancellationToken = default) => WithController(c => c.SetPreferredDeviceAsync(address, cancellationToken));

    public Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) =>
        _usbSettings is { } usb ? usb.SelectCustomEqualizerAsync(id, cancellationToken) : WithController(c => c.SelectCustomEqualizerAsync(id, cancellationToken));

    public Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken cancellationToken = default) =>
        _usbSettings is { } usb ? usb.SaveCustomEqualizerAsync(preset, cancellationToken) : WithController(c => c.SaveCustomEqualizerAsync(preset, cancellationToken));


    public Task<CommandResult> AddCustomEqualizerAsync(CancellationToken cancellationToken = default) => WithController(c => c.AddCustomEqualizerAsync(cancellationToken));

    public Task<CommandResult> DeleteCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) => WithController(c => c.DeleteCustomEqualizerAsync(id, cancellationToken));

    public Task<CommandResult> SetOptionAsync(string id, int value, CancellationToken cancellationToken = default) =>
        _usbSettings is { } usb ? usb.SetOptionAsync(id, value, cancellationToken) : WithController(c => c.SetOptionAsync(id, value, cancellationToken));

    public Task<CommandResult> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_usbIdentity is not null) return RefreshUsbAsync(cancellationToken, forceSettings: true);
        if (_active is null) return Task.FromResult(CommandResult.NotConnected);
        _active.Supervisor.RequestReconnect();
        return Task.FromResult(CommandResult.Ok);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _watcher.Changed -= OnPairedDevicesChanged;
        _watcher.Dispose();
        _usbWatcher.Changed -= OnPairedDevicesChanged;
        _usbWatcher.Dispose();
        await _selectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopUsbAsync().ConfigureAwait(false);
            if (_active is { } active)
            {
                _active = null;
                await active.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private async Task StopUsbAsync()
    {
        _usbIdentity = null;
        // Queued settings writes see the headset as gone and send nothing.
        _usbSettings?.Clear();
        _usbSettings = null;
        if (_usbStop is null) return;
        await _usbStop.CancelAsync().ConfigureAwait(false);
        if (_usbPoll is not null) await _usbPoll.ConfigureAwait(false);
        _usbStop.Dispose(); _usbStop = null; _usbPoll = null;
    }

    private async Task<CommandResult> RefreshUsbAsync(CancellationToken ct, bool forceSettings = false)
    {
        var identity = _usbIdentity;
        if (identity is null || _usbWatcher.Container(identity.DeviceId) is not { } container) return CommandResult.NotConnected;
        void ClearBattery()
        {
            _usbSettings?.Clear();
            _store.TryUpdate(identity.DeviceId, s => s with
            {
                Connection = DeviceConnectionState.UsbReceiverConnected,
                Profile = s.Profile is UsbHeadsetProfile profile ? profile.WithBattery(false) : s.Profile,
                Battery = BatteryStatus.Unknown,
                LastResponseAt = null,
            });
        }
        await _usbReadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            bounded.CancelAfter(TimeSpan.FromSeconds(8));
            var status = await UsbHidBattery.ReadStatusAsync(container, bounded.Token, _logger).ConfigureAwait(false);
            _logger.LogInformation("USB headset status: battery={Battery}, cable={Cable}, radio={Radio}", status.Level, status.IsCharging, status.IsConnected);
            int? level = status.Level;
            _store.TryUpdate(identity.DeviceId, s => s with
            {
                Connection = status.IsConnected == true ? DeviceConnectionState.UsbHeadsetConnected : DeviceConnectionState.UsbReceiverConnected,
                Profile = s.Profile is UsbHeadsetProfile profile ? profile.WithBattery(level.HasValue) : s.Profile,
                Battery = level.HasValue || status.IsCharging.HasValue ? new(Observed.From(new BatteryReading(level, status.IsCharging), DateTimeOffset.UtcNow, StateSource.QueryResponse), null, null) : BatteryStatus.Unknown,
                LastResponseAt = level.HasValue || status.IsConnected.HasValue ? DateTimeOffset.UtcNow : null,
            });
            // Barracuda X 1532:0550 only: its settings are read in their own session, still under the battery
            // gate, once the headset has just answered over the radio link. Other receivers never get here.
            if (status.Product == RazerPaSettings.BarracudaX && _usbStop is { } stop) _usbSettings ??= CreateUsbSettings(identity.DeviceId, container, stop.Token);
            if (_usbSettings is { } settings)
                await settings.SyncAsync(status.Product == RazerPaSettings.BarracudaX && status.IsConnected == true && level.HasValue, forceSettings, ct).ConfigureAwait(false);
            return level.HasValue ? CommandResult.Ok : CommandResult.Unsupported("No valid battery reply from a documented receiver query or standard HID percentage report.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { ClearBattery(); return new(CommandStatus.Timeout, "USB battery read timed out."); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            ClearBattery();
            _logger.LogWarning("USB battery read failed: 0x{HResult:X8}", ex.HResult);
            return new(CommandStatus.TransportError, "USB battery access is unavailable.");
        }
        finally { _usbReadGate.Release(); }
    }

    private UsbHeadsetSettingsController CreateUsbSettings(string deviceId, Guid container, CancellationToken stop)
    {
        // Each session opens and closes its own HID handle, bounded like the battery read.
        async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, stop, _lifetime.Token);
            bounded.CancelAfter(TimeSpan.FromSeconds(12));
            return await action(bounded.Token).ConfigureAwait(false);
        }
        return new UsbHeadsetSettingsController(deviceId, _store, _usbReadGate,
            (settings, token) => Bounded(t => UsbHeadsetSettings.ReadAsync(container, RazerPaSettings.BarracudaX, settings, t), token),
            (setting, values, token) => Bounded(t => UsbHeadsetSettings.WriteAsync(container, RazerPaSettings.BarracudaX, setting, values, t), token),
            _logger);
    }

    private async Task PollUsbAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) await RefreshUsbAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "USB monitoring stopped"); }
    }

    /// <param name="WindowsUnavailable">Windows could not open the device (no radio): only the bridge can link it.</param>
    /// <param name="BridgeOnly">Paired with A2DP Windows Bridge alone: Windows does not know it, opening it again changes nothing.</param>
    private sealed record ActiveDevice(DeviceIdentity Identity, ILinkStateSource Link, ControlChannelSupervisor Supervisor, bool WindowsUnavailable = false, bool BridgeOnly = false) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync().ConfigureAwait(false);
            Link.Dispose();
        }
    }
}
