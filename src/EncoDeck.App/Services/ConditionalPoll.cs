// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// A background check that runs every <c>interval</c> only while there is something to watch, and otherwise waits,
/// with no timer, until <see cref="Wake"/> (a state change that may give it something to watch again). A failing
/// check is logged and the loop goes on; only <see cref="Dispose"/> ends it.
/// </summary>
/// <remarks>
/// The first check runs at <see cref="Start"/>, on the caller's thread; after a wake the next check comes one interval
/// later, as it would have on the old fixed timer. One more check always runs after the condition turned false, so a
/// check can clear what it projected.
/// </remarks>
internal sealed class ConditionalPoll : IDisposable
{
    private readonly string _name;
    private readonly TimeSpan _interval;
    private readonly Func<bool> _active;
    private readonly Action _check;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private string? _lastFailure;
    private int _started;

    /// <param name="active">Whether to keep checking; evaluated on the poll's own thread after each check.</param>
    public ConditionalPoll(string name, TimeSpan interval, Func<bool> active, Action check, ILogger logger)
    {
        _name = name;
        _interval = interval;
        _active = active;
        _check = check;
        _logger = logger;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _ = RunAsync(_cts.Token);
    }

    /// <summary>Something to watch may have appeared: resume checking. Cheap, any thread, any number of times.</summary>
    public void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another caller woke it at the same moment.
        }
    }

    /// <summary>Runs the check once now (outside the loop's rhythm), with the loop's error handling.</summary>
    public void CheckNow() => SafeCheck();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                SafeCheck();
                if (!IsActive())
                {
                    _logger.LogDebug("{Poll}: idle", _name);
                    await _wake.WaitAsync(ct).ConfigureAwait(false);
                    _logger.LogDebug("{Poll}: woken", _name);
                }
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private bool IsActive()
    {
        try
        {
            return _active();
        }
        catch (Exception ex)
        {
            // Keep checking rather than go to sleep on a condition that could not be evaluated.
            Report(ex);
            return true;
        }
    }

    private void SafeCheck()
    {
        try
        {
            _check();
            _lastFailure = null;
        }
        catch (Exception ex)
        {
            // Includes exceptions thrown by state subscribers the check notifies synchronously: one bad
            // event must not end the poll for the rest of the session.
            Report(ex);
        }
    }

    // A failure that repeats on every tick is logged once as a warning, then only at debug level.
    private void Report(Exception ex)
    {
        string signature = $"{ex.GetType().FullName}: {ex.Message}";
        if (signature == _lastFailure)
        {
            _logger.LogDebug(ex, "{Poll} failed again", _name);
            return;
        }
        _lastFailure = signature;
        _logger.LogWarning(ex, "{Poll} failed; it keeps running", _name);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
