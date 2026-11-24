// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Bluetooth.Discovery;

/// <summary>
/// Keeps a long-lived watcher running across stops nobody asked for. Windows aborts a <c>DeviceWatcher</c> when the
/// Bluetooth stack or driver resets (adapter re-plugged, another program taking the adapter over WinUSB, a driver
/// update); without this the device list and connection flags froze until EncoDeck restarted. On such a stop the
/// owner's state is cleared (its consumers see the devices go away) and a new watcher is started after a bounded
/// backoff: 1 s, 2 s, 5 s, then every 30 s, never after <see cref="Dispose"/>.
/// <para>
/// WinRT-free so the decisions can be tested: <c>start</c> creates, subscribes and starts one watcher, wiring its
/// <c>Stopped</c> event to the callback it is given, and returns what unsubscribes and stops it again. The callback is
/// bound to that watcher's generation, so a late <c>Stopped</c> from a replaced watcher is ignored.
/// </para>
/// </summary>
internal sealed class WatcherRecovery : IDisposable
{
    internal static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    /// <summary>A watcher that ran this long counts as healthy: its loss starts the backoff from the beginning again.</summary>
    internal static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(60);

    private readonly string _name;
    private readonly Func<Action<string>, IDisposable> _start;
    private readonly Action _clear;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private IDisposable? _current;
    private ITimer? _restart;
    private int _generation;
    private int _failures;
    private long _startedAt;
    private bool _disposed;

    /// <param name="name">For logs.</param>
    /// <param name="start">Creates, subscribes and starts a watcher; the argument is its "stopped" callback (pass the status).</param>
    /// <param name="clear">Drops everything the lost watcher reported and raises the owner's change notifications.</param>
    public WatcherRecovery(string name, Func<Action<string>, IDisposable> start, Action clear, ILogger? logger = null, TimeProvider? time = null)
    {
        _name = name;
        _start = start;
        _clear = clear;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether a watcher is currently running (not stopped, not waiting to restart).</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _current is not null; }
    }

    /// <summary>Starts the first watcher. Exceptions propagate, as before recovery existed.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _current is not null || _restart is not null) return;
            StartLocked();
        }
    }

    // Runs under _gate: a Stopped raised on another thread while start is still running waits in OnStopped for the
    // gate, so it sees the new watcher as current and is not lost. start must release a half-made watcher if it throws.
    private void StartLocked()
    {
        int generation = ++_generation;
        _current = _start(status => OnStopped(generation, status));
        _startedAt = _time.GetTimestamp();
    }

    private void OnStopped(int generation, string status)
    {
        IDisposable? lost;
        bool healthy;
        lock (_gate)
        {
            // Ours (Dispose) or a watcher already replaced: nothing to recover.
            if (_disposed || generation != _generation || _current is null) return;
            lost = _current;
            _current = null;
            healthy = _time.GetElapsedTime(_startedAt) >= HealthyAfter;
            if (healthy) _failures = 0;
        }
        _logger.LogWarning("{Watcher} watcher stopped unexpectedly ({Status}); clearing its devices and restarting", _name, status);
        // Unsubscribes the old watcher's handlers before the owner's state is cleared, so it cannot refill it.
        Release(lost);
        _clear();
        lock (_gate)
        {
            if (!_disposed) ScheduleRestartLocked();
        }
    }

    private void ScheduleRestartLocked()
    {
        var delay = Backoff[Math.Min(_failures, Backoff.Length - 1)];
        _failures++;
        _restart?.Dispose();
        _restart = _time.CreateTimer(_ => Restart(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void Restart()
    {
        lock (_gate)
        {
            _restart?.Dispose();
            _restart = null;
            if (_disposed || _current is not null) return;
            try
            {
                StartLocked();
                _logger.LogInformation("{Watcher} watcher restarted", _name);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The stack may still be resetting (no adapter yet): try again later, never in a tight loop.
                _current = null;
                _logger.LogInformation("{Watcher} watcher could not restart (0x{Result:X8}); retrying", _name, ex.HResult);
                ScheduleRestartLocked();
            }
        }
    }

    private void Release(IDisposable? watcher)
    {
        try
        {
            watcher?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Stopping an aborted watcher can throw; it is already gone.
            _logger.LogDebug(ex, "{Watcher} watcher teardown failed", _name);
        }
    }

    public void Dispose()
    {
        IDisposable? current;
        ITimer? restart;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            current = _current;
            _current = null;
            restart = _restart;
            _restart = null;
        }
        restart?.Dispose();
        Release(current);
    }
}
