// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EncoDeck.Bluetooth.Control;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Core.State;
using Windows.Devices.Bluetooth;

namespace EncoDeck.Bluetooth.Discovery;

/// <summary>
/// Event-driven link state of one paired device via <see cref="BluetoothDevice.ConnectionStatusChanged"/>.
/// Earbuds switched to LE Audio keep only the links of their bonded LE identities (the one at the same
/// public address and the other earbud), so those count as well. Holds the devices for its lifetime
/// and releases them on dispose.
/// </summary>
public sealed class BluetoothLinkMonitor : ILinkStateSource
{
    private static readonly TimeSpan SecondaryRetry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OutputPoll = TimeSpan.FromSeconds(5);
    private const int SecondaryAttempts = 8;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private BluetoothDevice? _device;
    private ImmutableArray<BluetoothLEDevice> _leDevices;
    private ImmutableArray<Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession> _sessions = [];
    private volatile bool _connected;
    private volatile bool _leAudio;
    private volatile bool _leAudioOutput;
    private LeAudioLinkState _leLinks = LeAudioLinkState.Empty;
    private int _searching;
    private bool _disposed;

    private readonly bool _singleUnit;
    private readonly bool _managesLe;

    /// <param name="device">The Classic device; null for earbuds kept on LE Audio alone (<see cref="LeAudioOnlyDevices"/>).</param>
    private BluetoothLinkMonitor(BluetoothDevice? device, ImmutableArray<BluetoothLEDevice> leDevices, bool singleUnit, bool managesLe)
    {
        _device = device;
        _leDevices = leDevices;
        _singleUnit = singleUnit;
        _managesLe = managesLe;
        _connected = Evaluate();
        _leAudio = EvaluateLe();
        _leLinks = EvaluateLinks();
        if (_device is not null) _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        foreach (var le in leDevices) le.ConnectionStatusChanged += OnConnectionStatusChanged;
        if (managesLe) _ = MaintainAsync(leDevices);
        else if (!leDevices.IsDefaultOrEmpty) _ = WatchLeAudioOutputAsync();
        // Only the primary bonded so far (e.g. the LE Audio switch is still pairing the other earbud).
        if (leDevices.Length == 1 && !singleUnit) _ = FindSecondaryAsync(SecondaryAttempts);
    }

    public bool IsConnected => _connected;

    public event EventHandler<bool>? ConnectionChanged;

    public bool IsLeAudio => _leAudio;

    public LeAudioLinkState LeAudioLinks => Volatile.Read(ref _leLinks);

    public event EventHandler? LinkDetailsChanged;

    /// <summary>
    /// The earbuds were switched to LE Audio on this PC (an LE bond is watched). An LE bond of earbuds whose LE Audio
    /// Windows decides says nothing about the audio path, so it never makes them wait for LE Audio.
    /// </summary>
    public bool IsLeAudioBonded => _managesLe && !_leDevices.IsDefaultOrEmpty;

    public event EventHandler? TransportChanged;

    /// <param name="singleUnit">
    /// Headphones expose exactly one LE identity, so the "other earbud" of a true pair never appears.
    /// Treating that as "only one side is connected" made the UI claim one earbud was missing.
    /// </param>
    /// <param name="managesLe">See <see cref="Core.Profiles.IDeviceProfile.ManagesLeAudioLink"/>.</param>
    public static async Task<BluetoothLinkMonitor?> CreateAsync(string deviceId, bool singleUnit, bool managesLe, CancellationToken cancellationToken)
    {
        // Never open the Classic device of LE-only earbuds: only their LE bonds carry links.
        if (LeAudioOnlyDevices.Find(deviceId) is { } leOnly)
        {
            var bonds = await BondedLeIdentitiesAsync(leOnly.Address, cancellationToken).ConfigureAwait(false);
            if (!bonds.IsDefaultOrEmpty) return new BluetoothLinkMonitor(null, bonds, singleUnit, managesLe);
        }

        BluetoothDevice? device;
        try
        {
            device = await BluetoothDevice.FromIdAsync(deviceId).AsTask(cancellationToken);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (device is null) return null;
        try
        {
            return new BluetoothLinkMonitor(device, await BondedLeIdentitiesAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false), singleUnit, managesLe);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The bonded LE identity at the public address plus the other bonded earbud of the pair (either
    /// may be the only one out of the case). An unpaired GATT link (e.g. GAIA control) is not audio.
    /// </summary>
    private static async Task<ImmutableArray<BluetoothLEDevice>> BondedLeIdentitiesAsync(ulong address, CancellationToken cancellationToken)
    {
        BluetoothLEDevice? le;
        try
        {
            le = await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or COMException)
        {
            return [];
        }
        if (le is null) return [];
        bool bonded;
        try
        {
            bonded = await LeAudioLink.IsBondedAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            le.Dispose();
            throw;
        }
        if (!bonded)
        {
            le.Dispose();
            return [];
        }
        try
        {
            return [le, .. await LeAudioPairing.FindSecondaryBondsAsync(address, le.Name, cancellationToken).ConfigureAwait(false)];
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or COMException)
        {
            return [le];
        }
    }

    /// <summary>Looks for the other earbud's bond until it appears, the attempts run out or the monitor is disposed.</summary>
    private async Task FindSecondaryAsync(int attempts)
    {
        if (Interlocked.Exchange(ref _searching, 1) != 0) return;
        try
        {
            var token = _lifetime.Token;
            for (int i = 0; i < attempts && !token.IsCancellationRequested; i++)
            {
                if (i > 0) await Task.Delay(SecondaryRetry, token).ConfigureAwait(false);
                var current = _leDevices;
                if (current.Length != 1) return;
                List<BluetoothLEDevice> found;
                try
                {
                    found = await LeAudioPairing.FindSecondaryBondsAsync(current[0].BluetoothAddress, current[0].Name, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or COMException or ObjectDisposedException)
                {
                    continue;
                }
                if (found.Count == 0) continue;
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _leDevices = _leDevices.AddRange(found);
                        foreach (var le in found) le.ConnectionStatusChanged += OnConnectionStatusChanged;
                        found.Clear();
                    }
                }
                foreach (var le in found) le.Dispose();
                if (_managesLe) _ = MaintainAsync(_leDevices.Skip(1));
                OnConnectionStatusChanged(this, EventArgs.Empty);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Volatile.Write(ref _searching, 0);
        }
    }

    /// <summary>
    /// Asks Windows to keep (and re-establish) the LE links of the bonded earbuds. After the case
    /// opens the X4 does not reconnect LE by itself, but once it advertises connectably (holding the
    /// case button) Windows reconnects the existing bonds without pairing again (verified on the X4).
    /// </summary>
    private async Task MaintainAsync(IEnumerable<BluetoothLEDevice> devices)
    {
        foreach (var le in devices.ToList())
        {
            Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession? session = null;
            try
            {
                session = await Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession.FromDeviceIdAsync(le.BluetoothDeviceId).AsTask(_lifetime.Token).ConfigureAwait(false);
                if (session is null) continue;
                session.MaintainConnection = true;
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _sessions = _sessions.Add(session);
                        session = null;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or ArgumentException or UnauthorizedAccessException or COMException)
            {
                // Best effort: without it the earbuds still reconnect whenever they choose to.
            }
            finally { session?.Dispose(); }
        }
    }

    private bool Evaluate() => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected || IsLeLinkUp();

    private bool IsLeLinkUp() => _leDevices.Any(le => le.ConnectionStatus == BluetoothConnectionStatus.Connected);

    private bool EvaluateLe() => IsLeLinkUp() && (_managesLe || _leAudioOutput);

    /// <summary>
    /// For earbuds whose LE Audio Windows decides: whether Windows exposes an active LE Audio output for them while
    /// an LE link is up. Polled, since the output appears seconds after the link and turning LE Audio off in Windows
    /// removes it without dropping the link. Read-only.
    /// </summary>
    private async Task WatchLeAudioOutputAsync()
    {
        var token = _lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool output = false;
                var devices = _leDevices;
                if (!devices.IsDefaultOrEmpty && IsLeLinkUp())
                {
                    try
                    {
                        var inspection = await LeAudioClassicAudioEndpoint.InspectAsync(devices[0].BluetoothAddress, token).ConfigureAwait(false);
                        output = inspection.LeAudioRender is { IsStateKnown: true, IsActive: true, IsDisabled: false };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
                    {
                        output = _leAudioOutput;
                    }
                }
                if (output != _leAudioOutput)
                {
                    _leAudioOutput = output;
                    OnConnectionStatusChanged(this, EventArgs.Empty);
                }
                await Task.Delay(OutputPoll, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private LeAudioLinkState EvaluateLinks()
    {
        var devices = _leDevices;
        var primaryConnected = devices.Length > 0 && devices[0].ConnectionStatus == BluetoothConnectionStatus.Connected;
        if (_singleUnit)
        {
            // One identity, one unit: a connected headset is complete, never "only one side connected".
            return new(devices.Length > 0, primaryConnected, false, false);
        }
        return new(devices.Length > 0,
            primaryConnected,
            devices.Length > 1,
            devices.Skip(1).Any(le => le.ConnectionStatus == BluetoothConnectionStatus.Connected));
    }

    private void OnConnectionStatusChanged(object sender, object args)
    {
        bool connected, leAudio;
        LeAudioLinkState links;
        LeAudioLinkState previousLinks;
        bool transportChanged, connectionChanged;
        // Publish one consistent snapshot before invoking listeners: callbacks can synchronously
        // read IsConnected/IsLeAudio as well as the per-ear details.
        lock (_gate)
        {
            if (_disposed) return;
            try { connected = Evaluate(); leAudio = EvaluateLe(); links = EvaluateLinks(); }
            catch (ObjectDisposedException) { return; }
            previousLinks = Interlocked.Exchange(ref _leLinks, links);
            transportChanged = leAudio != _leAudio;
            connectionChanged = connected != _connected;
            _leAudio = leAudio;
            _connected = connected;
        }
        if (links != previousLinks) LinkDetailsChanged?.Invoke(this, EventArgs.Empty);
        if (sender is BluetoothLEDevice && _leDevices.Length == 1 && !_singleUnit) _ = FindSecondaryAsync(1);
        if (transportChanged) TransportChanged?.Invoke(this, EventArgs.Empty);
        if (connectionChanged) ConnectionChanged?.Invoke(this, connected);
    }

    public void Dispose()
    {
        ImmutableArray<BluetoothLEDevice> leDevices;
        ImmutableArray<Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession> sessions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            leDevices = _leDevices;
            _leDevices = [];
            sessions = _sessions;
            _sessions = [];
        }
        _lifetime.Cancel();
        foreach (var session in sessions) session.Dispose();
        foreach (var le in leDevices)
        {
            le.ConnectionStatusChanged -= OnConnectionStatusChanged;
            le.Dispose();
        }
        var device = Interlocked.Exchange(ref _device, null);
        if (device is not null)
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            device.Dispose();
        }
        // _lifetime is left undisposed (no timer): a late FindSecondaryAsync may still read its token.
    }
}
