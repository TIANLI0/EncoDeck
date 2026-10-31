// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace EncoDeck.Bluetooth.Discovery;

/// <summary>A paired classic Bluetooth device as currently known to Windows.</summary>
/// <param name="MajorClass">Bluetooth Class-of-Device major class (4 = audio/video, 2 = phone), or null when Windows did not report it.</param>
public sealed record PairedDevice(DeviceIdentity Identity, bool IsConnected, int? MajorClass = null)
{
    public const int AudioVideoMajorClass = 4;

    public bool IsAudioDevice => MajorClass == AudioVideoMajorClass;
}

/// <summary>
/// Keeps an up-to-date list of paired Bluetooth Classic devices using a <see cref="DeviceWatcher"/>
/// (push notifications from Windows — no polling). Earbuds on LE Audio keep their Classic link
/// down, so a second watcher follows bonded LE identities: a Classic device counts as connected
/// when its LE identity (same public address) or the other earbud of the pair is connected.
/// Earbuds whose Classic pairing EncoDeck removed for LE Audio (<see cref="LeAudioOnlyDevices"/>) are listed
/// under their former Classic identity for as long as the primary's LE bond exists.
/// </summary>
public sealed partial class PairedDeviceWatcher : IDisposable
{
    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private const string AddressKey = "System.Devices.Aep.DeviceAddress";
    private const string CodMajorKey = "System.Devices.Aep.Bluetooth.Cod.Major";

    private sealed record LeBond(ulong Address, string Name, bool IsConnected);

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PairedDevice> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LeBond> _leBonds = new(StringComparer.Ordinal);
    private DeviceWatcher? _watcher;
    private DeviceWatcher? _leWatcher;
    private bool _leEnumerated;

    public PairedDeviceWatcher(ILogger<PairedDeviceWatcher>? logger = null)
    {
        _logger = logger ?? NullLogger<PairedDeviceWatcher>.Instance;
    }

    public IReadOnlyList<PairedDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices.Values.Concat(LeOnlyDevices())
                    .Select(d => d.IsConnected || !LeConnected(d.Identity) ? d : d with { IsConnected = true })
                    .OrderBy(d => d.Identity.Name, StringComparer.CurrentCulture).ToList();
            }
        }
    }

    /// <summary>Remembered LE-only earbuds whose primary LE bond exists and that have no Classic pairing again.</summary>
    private IEnumerable<PairedDevice> LeOnlyDevices() =>
        LeAudioOnlyDevices.All
            .Where(d => _leBonds.Values.Any(b => b.Address == d.Address)
                && !_devices.Values.Any(c => c.Identity.Address.Value == d.Address
                    || string.Equals(c.Identity.DeviceId, d.DeviceId, StringComparison.OrdinalIgnoreCase)))
            .Select(d => new PairedDevice(new DeviceIdentity(d.DeviceId, d.Name, new BluetoothAddress(d.Address)), false, PairedDevice.AudioVideoMajorClass));

    /// <summary>The device's own LE identity or, for earbuds, the other bonded earbud (same name, same first four address bytes).</summary>
    private bool LeConnected(DeviceIdentity identity)
    {
        ulong address = identity.Address.Value;
        return address != 0 && _leBonds.Values.Any(b => b.IsConnected
            && (b.Address == address || (b.Address >> 16 == address >> 16 && b.Name == identity.Name)));
    }

    /// <summary>True once the initial enumeration finished.</summary>
    public bool IsEnumerated { get; private set; }

    /// <summary>Raised (on a thread-pool thread) whenever the list or a connection flag changes.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (_watcher is not null) return;
        string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        try
        {
            _watcher = DeviceInformation.CreateWatcher(selector, [IsConnectedKey, AddressKey, CodMajorKey], DeviceInformationKind.AssociationEndpoint);
        }
        catch (ArgumentException)
        {
            // Older builds may not know the class-of-device property; fall back without it.
            _watcher = DeviceInformation.CreateWatcher(selector, [IsConnectedKey, AddressKey], DeviceInformationKind.AssociationEndpoint);
        }
        _watcher.Added += OnAdded;
        _watcher.Updated += OnUpdated;
        _watcher.Removed += OnRemoved;
        _watcher.EnumerationCompleted += OnEnumerationCompleted;
        _watcher.Start();
        LeAudioOnlyDevices.Changed += OnLeOnlyChanged;

        try
        {
            _leWatcher = DeviceInformation.CreateWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                [IsConnectedKey, AddressKey], DeviceInformationKind.AssociationEndpoint);
            _leWatcher.Added += OnLeAdded;
            _leWatcher.Updated += OnLeUpdated;
            _leWatcher.Removed += OnLeRemoved;
            _leWatcher.EnumerationCompleted += OnLeEnumerationCompleted;
            _leWatcher.Start();
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // LE Audio connection state is optional; Classic devices still work without it.
            _logger.LogInformation("Bonded LE watcher unavailable (0x{Result:X8})", ex.HResult);
            _leWatcher = null;
        }
    }

    private void OnLeOnlyChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnLeEnumerationCompleted(DeviceWatcher sender, object args)
    {
        lock (_gate) _leEnumerated = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLeAdded(DeviceWatcher sender, DeviceInformation info)
    {
        var address = ParseAddress(info.Properties.TryGetValue(AddressKey, out var a) ? a as string : null) ?? ParseAddressFromId(info.Id);
        if (address is null) return;
        bool connected = info.Properties.TryGetValue(IsConnectedKey, out var c) && c is true;
        lock (_gate) _leBonds[info.Id] = new LeBond(address.Value, info.Name, connected);
        // A remembered LE-only primary appears or goes with its bond.
        if (connected || LeAudioOnlyDevices.Find(address.Value) is not null) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLeUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_leBonds.TryGetValue(update.Id, out var existing) && update.Properties.TryGetValue(IsConnectedKey, out var c) && c is bool connected && connected != existing.IsConnected)
            {
                _leBonds[update.Id] = existing with { IsConnected = connected };
                changed = true;
            }
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLeRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        bool changed;
        lock (_gate) changed = _leBonds.Remove(update.Id, out var removed)
            && (removed.IsConnected || LeAudioOnlyDevices.Find(removed.Address) is not null);
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        var address = ParseAddress(info.Properties.TryGetValue(AddressKey, out var a) ? a as string : null) ?? ParseAddressFromId(info.Id);
        bool connected = info.Properties.TryGetValue(IsConnectedKey, out var c) && c is true;
        int? major = info.Properties.TryGetValue(CodMajorKey, out var m) && m is not null ? Convert.ToInt32(m, System.Globalization.CultureInfo.InvariantCulture) : null;
        var device = new PairedDevice(new DeviceIdentity(info.Id, info.Name, new BluetoothAddress(address ?? 0)), connected, major);
        bool backOnClassic;
        lock (_gate)
        {
            _devices[info.Id] = device;
            // Paired over Classic again after its LE bonds were removed outside EncoDeck: no longer LE-only.
            backOnClassic = _leEnumerated && address is { } value && LeAudioOnlyDevices.Find(value) is not null
                && !_leBonds.Values.Any(b => b.Address == value || (b.Address >> 16 == value >> 16 && b.Name == info.Name));
        }
        if (backOnClassic) LeAudioOnlyDevices.Remove(address!.Value);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_devices.TryGetValue(update.Id, out var existing) && update.Properties.TryGetValue(IsConnectedKey, out var c) && c is bool connected && connected != existing.IsConnected)
            {
                _devices[update.Id] = existing with { IsConnected = connected };
                changed = true;
            }
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        bool removed;
        lock (_gate) removed = _devices.Remove(update.Id);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        IsEnumerated = true;
        _logger.LogInformation("Paired device enumeration completed ({Count} devices)", _devices.Count);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static ulong? ParseAddress(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        string hex = text.Replace(":", string.Empty).Replace("-", string.Empty);
        return hex.Length == 12 && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong value) ? value : null;
    }

    /// <summary>AEP ids look like <c>Bluetooth#Bluetooth{adapter}-{device}</c>.</summary>
    internal static ulong? ParseAddressFromId(string id)
    {
        var match = DeviceAddressInId().Match(id);
        return match.Success ? ParseAddress(match.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"-((?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2})$")]
    private static partial Regex DeviceAddressInId();

    public void Dispose()
    {
        LeAudioOnlyDevices.Changed -= OnLeOnlyChanged;
        var leWatcher = Interlocked.Exchange(ref _leWatcher, null);
        if (leWatcher is not null)
        {
            leWatcher.Added -= OnLeAdded;
            leWatcher.Updated -= OnLeUpdated;
            leWatcher.Removed -= OnLeRemoved;
            leWatcher.EnumerationCompleted -= OnLeEnumerationCompleted;
            if (leWatcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) leWatcher.Stop();
        }
        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher is null) return;
        watcher.Added -= OnAdded;
        watcher.Updated -= OnUpdated;
        watcher.Removed -= OnRemoved;
        watcher.EnumerationCompleted -= OnEnumerationCompleted;
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            watcher.Stop();
        }
    }
}
