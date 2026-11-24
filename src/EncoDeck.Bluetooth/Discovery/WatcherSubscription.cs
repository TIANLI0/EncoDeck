// SPDX-License-Identifier: GPL-3.0-only
using Windows.Devices.Enumeration;

namespace EncoDeck.Bluetooth.Discovery;

/// <summary>
/// One started <see cref="DeviceWatcher"/> with its handlers, as handed to <see cref="WatcherRecovery"/>: disposing it
/// unsubscribes the handlers first (so a stopping or aborted watcher cannot refill state that was just cleared, and
/// our own Stop is not taken for an abort), then stops the watcher if it is still running.
/// </summary>
internal sealed class WatcherSubscription(DeviceWatcher watcher, Action unsubscribe) : IDisposable
{
    private int _disposed;

    /// <summary>Starts the watcher; on failure releases it, so a failed restart leaves no handlers behind.</summary>
    public void Start()
    {
        try
        {
            watcher.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        unsubscribe();
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
    }
}
