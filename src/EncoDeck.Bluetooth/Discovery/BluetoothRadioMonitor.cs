// SPDX-License-Identifier: GPL-3.0-only
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace EncoDeck.Bluetooth.Discovery;

/// <summary>
/// Whether the local Bluetooth radio is switched off (Quick Settings toggle, airplane mode), via
/// <see cref="Radio.StateChanged"/>. Unknown (no adapter, or the radio could not be read) counts as on.
/// The <see cref="Radio"/> object belongs to one adapter instance: when the adapter is re-plugged, its driver
/// restarts or another program takes it over, a watcher on Bluetooth adapters re-resolves the radio and moves the
/// subscription to the new one, instead of following the dead object forever.
/// </summary>
public sealed class BluetoothRadioMonitor : IDisposable
{
    private readonly Lock _gate = new();
    private Radio? _radio;
    private WatcherRecovery? _adapters;
    private int _resolveVersion;
    private bool _adaptersEnumerated;
    private volatile bool _disposed;
    private volatile bool _off;

    public bool IsOff => _off;

    public async Task StartAsync()
    {
        await ResolveAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed || _adapters is not null) return;
            _adapters = new WatcherRecovery("Bluetooth adapter", StartAdapterWatcher, OnAdapterWatcherLost);
        }
        try
        {
            _adapters.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No adapter notifications on this system: keep the radio found above, as before.
        }
    }

    private IDisposable StartAdapterWatcher(Action<string> stopped)
    {
        lock (_gate) _adaptersEnumerated = false;
        var watcher = DeviceInformation.CreateWatcher(BluetoothAdapter.GetDeviceSelector());
        void Added(DeviceWatcher sender, DeviceInformation info) => OnAdapterChanged();
        void Removed(DeviceWatcher sender, DeviceInformationUpdate update) => OnAdapterChanged();
        void Completed(DeviceWatcher sender, object args)
        {
            lock (_gate) _adaptersEnumerated = true;
        }
        void Stopped(DeviceWatcher sender, object args) => stopped(sender.Status.ToString());
        watcher.Added += Added;
        watcher.Removed += Removed;
        watcher.EnumerationCompleted += Completed;
        watcher.Stopped += Stopped;
        var subscription = new WatcherSubscription(watcher, () =>
        {
            watcher.Added -= Added;
            watcher.Removed -= Removed;
            watcher.EnumerationCompleted -= Completed;
            watcher.Stopped -= Stopped;
        });
        subscription.Start();
        return subscription;
    }

    private void OnAdapterChanged()
    {
        // The initial enumeration lists the adapter already resolved in StartAsync (or by a restart).
        lock (_gate)
            if (!_adaptersEnumerated) return;
        _ = ResolveAsync();
    }

    /// <summary>The adapter watcher was aborted (stack reset): the radio object is likely stale too.</summary>
    private void OnAdapterWatcherLost() => _ = ResolveAsync();

    private async Task ResolveAsync()
    {
        int version = Interlocked.Increment(ref _resolveVersion);
        Radio? radio = null;
        try
        {
            var radios = await Radio.GetRadiosAsync();
            radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Radio state is unavailable on this system (or mid-reset); behave as if Bluetooth is on.
        }
        lock (_gate)
        {
            // A newer resolve (a later adapter change) wins; never resubscribe after Dispose.
            if (_disposed || version != Volatile.Read(ref _resolveVersion)) return;
            if (!ReferenceEquals(_radio, radio))
            {
                // Old one off before the new one on: when the adapter did not actually change, both wrappers can share
                // one native event source, and adding before removing would leave no subscription at all.
                if (_radio is not null) _radio.StateChanged -= OnStateChanged;
                _radio = radio;
                if (radio is not null) radio.StateChanged += OnStateChanged;
            }
        }
        Refresh();
    }

    // Reads the current radio rather than the sender, so a late event from a replaced radio cannot set a stale state.
    private void OnStateChanged(Radio sender, object? args) => Refresh();

    private void Refresh()
    {
        Radio? radio;
        lock (_gate) radio = _radio;
        try
        {
            _off = radio is not null && radio.State is RadioState.Off or RadioState.Disabled;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The adapter went away under us; the adapter watcher resolves the new radio.
            _off = false;
        }
    }

    public void Dispose()
    {
        Radio? radio;
        WatcherRecovery? adapters;
        lock (_gate)
        {
            _disposed = true;
            radio = _radio;
            _radio = null;
            adapters = _adapters;
            _adapters = null;
        }
        adapters?.Dispose();
        if (radio is not null) radio.StateChanged -= OnStateChanged;
    }
}
