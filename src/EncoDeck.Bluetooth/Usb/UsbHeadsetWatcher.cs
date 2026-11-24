// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Protocol.Brands.Usb;
using Windows.Devices.Enumeration;

namespace EncoDeck.Bluetooth.Usb;

/// <summary>Watches audio-render endpoints and confirms USB ancestry in the same device container.</summary>
public sealed class UsbHeadsetWatcher : IDisposable
{
    internal const string ContainerKey = "System.Devices.ContainerId";
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceInformation> _endpoints = [];
    private readonly Dictionary<string, PairedDevice> _devices = [];
    private readonly Dictionary<string, Guid> _containers = [];
    private readonly Dictionary<string, long> _revisions = [];
    private long _revision;
    // Startup receivers count as enumerated only after their USB ancestry check finishes.
    private int _pending;
    private bool _completed;
    private readonly CancellationTokenSource _stop = new();
    private WatcherRecovery? _recovery;
    // One receiver often exposes several render endpoints (game/chat); list it once per container.
    public IReadOnlyList<PairedDevice> Devices
    {
        get
        {
            lock (_gate) return _devices.GroupBy(d => _containers[d.Key])
                .Select(g => g.OrderBy(d => d.Key, StringComparer.Ordinal).First().Value).ToList();
        }
    }
    public bool IsEnumerated { get; private set; }
    public event EventHandler? Changed;
    public Guid? Container(string id) { lock (_gate) return _containers.TryGetValue(id, out var value) ? value : null; }
    public void Start()
    {
        if (_recovery is not null) return;
        _recovery = new WatcherRecovery("USB audio", StartWatcher, Clear);
        _recovery.Start();
    }
    private IDisposable StartWatcher(Action<string> stopped)
    {
        // Audio render interface class; input-only microphones and arbitrary HID devices are excluded.
        var watcher = DeviceInformation.CreateWatcher("System.Devices.InterfaceClassGuid:=\"{e6327cad-dcec-4949-ae8a-991e976a79d2}\"",
            [ContainerKey], DeviceInformationKind.DeviceInterface);
        void Stopped(DeviceWatcher sender, object args) => stopped(sender.Status.ToString());
        watcher.Added += Added;
        watcher.Updated += Updated;
        watcher.Removed += Removed;
        watcher.EnumerationCompleted += Completed;
        watcher.Stopped += Stopped;
        var subscription = new WatcherSubscription(watcher, () =>
        {
            watcher.Added -= Added; watcher.Updated -= Updated; watcher.Removed -= Removed; watcher.EnumerationCompleted -= Completed;
            watcher.Stopped -= Stopped;
        });
        subscription.Start();
        return subscription;
    }
    /// <summary>
    /// The watcher was aborted (audio/USB stack reset): forget every endpoint so receivers go away until the new
    /// watcher lists them again. Dropping the revisions also discards ancestry checks still running for them.
    /// </summary>
    private void Clear()
    {
        lock (_gate) { _endpoints.Clear(); _devices.Clear(); _containers.Clear(); _revisions.Clear(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Added(DeviceWatcher sender, DeviceInformation info)
    {
        long revision;
        lock (_gate) { _endpoints[info.Id] = info; revision = _revisions[info.Id] = ++_revision; _pending++; }
        _ = VerifyAsync(info, revision);
    }
    private void Updated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        DeviceInformation? info;
        long revision;
        lock (_gate) { _endpoints.TryGetValue(update.Id, out info); info?.Update(update); revision = _revisions[update.Id] = ++_revision; if (info is not null) _pending++; }
        if (info is not null) _ = VerifyAsync(info, revision);
    }
    private void Removed(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_gate) { _endpoints.Remove(update.Id); _devices.Remove(update.Id); _containers.Remove(update.Id); _revisions.Remove(update.Id); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Completed(DeviceWatcher sender, object args)
    {
        lock (_gate) { _completed = true; if (_pending == 0) IsEnumerated = true; }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void VerifyFinished()
    {
        bool latched;
        lock (_gate) { _pending--; latched = _completed && _pending == 0 && !IsEnumerated; if (latched) IsEnumerated = true; }
        if (latched) Changed?.Invoke(this, EventArgs.Empty);
    }
    private async Task VerifyAsync(DeviceInformation info, long revision)
    {
        try
        {
            var identity = new DeviceIdentity(info.Id, info.Name, new BluetoothAddress(0)) { Transport = DeviceTransport.Usb };
            bool named = UsbHeadsetProfiles.Create().Any(p => p.MatchScore(identity) > 0);
            bool valid = false;
            Guid container = default;
            if (info.IsEnabled && info.Properties.TryGetValue(ContainerKey, out var value) && value is Guid id && id != Guid.Empty)
            {
                container = id;
                var members = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"",
                    ["System.Devices.HardwareIds"], DeviceInformationKind.Device).AsTask(_stop.Token).ConfigureAwait(false);
                string[] hardware = members.SelectMany(m => m.Properties.TryGetValue("System.Devices.HardwareIds", out var ids) && ids is string[] list ? list : []).ToArray();
                if (UsbHeadsetProfiles.MatchHardware(hardware) is { } exact)
                { identity = identity with { Name = exact.DisplayName }; named = true; }
                valid = named && hardware.Any(h => h.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase));
            }
            bool changed;
            lock (_gate)
            {
                if (_stop.IsCancellationRequested || !_revisions.TryGetValue(info.Id, out var current) || current != revision) return;
                if (valid)
                {
                    changed = !_devices.TryGetValue(info.Id, out var old) || old.Identity != identity || _containers[info.Id] != container;
                    _devices[info.Id] = new(identity, true); _containers[info.Id] = container;
                }
                else { changed = _devices.Remove(info.Id); _containers.Remove(info.Id); }
            }
            // Every audio endpoint is verified; only supported receivers may trigger device reselection.
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // USB unplug/access denial races are normal; they must not stop Bluetooth discovery.
        }
        finally { VerifyFinished(); }
    }
    public void Dispose()
    {
        _stop.Cancel();
        _recovery?.Dispose();
    }
}
