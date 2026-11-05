// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Bluetooth.Control;

/// <summary>
/// Windows' link to the device, or A2DP Windows Bridge's while the bridge streams to it and can carry the control
/// channel (<see cref="BridgeAudioLink.CanCarryControl"/>): the earbuds count as connected either way. Windows' link
/// is preferred while it is up; a change of the route alone raises nothing (the open channel keeps working).
/// </summary>
internal sealed class BridgedLinkState : ILinkStateSource
{
    private readonly ILinkStateSource _windows;
    private readonly IDeviceStateStore _store;
    private readonly string _deviceId;
    private readonly Lock _gate = new();
    private bool _bridge;
    private bool _connected;

    public void ReleaseMaintainedLinks() => _windows.ReleaseMaintainedLinks();

    public BridgedLinkState(ILinkStateSource windows, IDeviceStateStore store, string deviceId)
    {
        _windows = windows;
        _store = store;
        _deviceId = deviceId;
        _bridge = Carries(store.Current);
        _connected = windows.IsConnected || _bridge;
        _windows.ConnectionChanged += OnWindowsChanged;
        _store.StateChanged += OnStateChanged;
    }

    public bool IsConnected
    {
        get { lock (_gate) return _connected; }
    }

    public bool IsViaBridge
    {
        get { lock (_gate) return _bridge && !_windows.IsConnected; }
    }

    public event EventHandler<bool>? ConnectionChanged;

    public bool IsLeAudio => _windows.IsLeAudio;

    public LeAudioLinkState LeAudioLinks => _windows.LeAudioLinks;

    public bool IsLeAudioBonded => _windows.IsLeAudioBonded;

    public event EventHandler? LinkDetailsChanged
    {
        add => _windows.LinkDetailsChanged += value;
        remove => _windows.LinkDetailsChanged -= value;
    }

    public event EventHandler? TransportChanged
    {
        add => _windows.TransportChanged += value;
        remove => _windows.TransportChanged -= value;
    }

    private bool Carries(DeviceState state) =>
        state.Device?.DeviceId == _deviceId && state.Bridge is { IsStreaming: true, CanCarryControl: true };

    private void OnWindowsChanged(object? sender, bool connected) => Update();

    // The store's events can arrive out of order across threads: read its current state, not the event's.
    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs e) => Update();

    private void Update()
    {
        bool connected;
        lock (_gate)
        {
            _bridge = Carries(_store.Current);
            connected = _windows.IsConnected || _bridge;
            if (connected == _connected) return;
            _connected = connected;
        }
        ConnectionChanged?.Invoke(this, connected);
    }

    public void Dispose()
    {
        _store.StateChanged -= OnStateChanged;
        _windows.ConnectionChanged -= OnWindowsChanged;
        _windows.Dispose();
    }
}

/// <summary>
/// Windows' link while Windows cannot open the device at all (no Bluetooth radio: A2DP Windows Bridge took the only
/// adapter over): never connected, so <see cref="BridgedLinkState"/> follows the bridge alone.
/// </summary>
internal sealed class NoWindowsLink : ILinkStateSource
{
    public bool IsConnected => false;

    public event EventHandler<bool>? ConnectionChanged { add { } remove { } }

    public void Dispose() { }
}

/// <summary>Opens the control channel over Windows Bluetooth, or through A2DP Windows Bridge while only the bridge is linked.</summary>
internal sealed class BridgeRoutingConnector(IChannelConnector windows, IChannelConnector bridge, ILinkStateSource link) : IChannelConnector
{
    public Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken) =>
        link.IsViaBridge
            ? bridge.OpenAsync(deviceId, serviceUuids.Where(RfcommChannelConnector.IsRfcommService).ToArray(), cancellationToken)
            : windows.OpenAsync(deviceId, serviceUuids, cancellationToken);
}
