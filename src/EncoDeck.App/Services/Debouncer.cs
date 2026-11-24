// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Dispatching;

namespace EncoDeck.App.Services;

/// <summary>
/// A write that waits until the user stops changing a value (a slider being dragged), then runs once on the UI thread.
/// The control already shows the new value, so a pending write must not be dropped when its page goes away: the owner
/// calls <see cref="Flush"/> when it unloads, and <see cref="FlushAll"/> covers the paths where WinUI raises no Unloaded
/// (a window closing, the app exiting). The write gets <c>refresh</c> = false when flushed: its page may be gone, so it
/// must then not touch its controls after reading the value. UI thread only.
/// </summary>
internal sealed class Debouncer
{
    // Debouncers whose timer runs, and writes started by them that have not finished (UI thread only).
    private static readonly HashSet<Debouncer> Armed = [];
    private static readonly HashSet<Task> Running = [];

    private readonly DispatcherQueueTimer _timer;
    private readonly Func<bool, Task> _write;

    /// <param name="write">The write; its argument is false when it runs early (<see cref="Flush"/>), so it must not refresh the UI.</param>
    public Debouncer(DispatcherQueue queue, TimeSpan delay, Func<bool, Task> write)
    {
        _write = write;
        _timer = queue.CreateTimer();
        _timer.Interval = delay;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) =>
        {
            Disarm();
            Run(refresh: true);
        };
    }

    /// <summary>A write is waiting (the user is still changing the value).</summary>
    public bool IsPending => Armed.Contains(this);

    /// <summary>(Re)starts the wait: the write runs once the value stays unchanged for the delay.</summary>
    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
        Armed.Add(this);
    }

    /// <summary>Drops the waiting write (it no longer applies, e.g. another device or a preset replaced the value).</summary>
    public void Cancel() => Disarm();

    /// <summary>Runs the waiting write now, if there is one.</summary>
    public void Flush()
    {
        if (!IsPending) return;
        Disarm();
        Run(refresh: false);
    }

    /// <summary>Runs every waiting write now (a window closes, the app exits).</summary>
    public static void FlushAll()
    {
        foreach (var debouncer in Armed.ToArray()) debouncer.Flush();
    }

    /// <summary>
    /// Flushes every waiting write and waits up to <paramref name="timeout"/> for all started writes to finish, so the
    /// app does not close the device connection under them on exit.
    /// </summary>
    public static async Task FlushAndWaitAsync(TimeSpan timeout)
    {
        FlushAll();
        if (Running.Count == 0) return;
        await Task.WhenAny(Task.WhenAll(Running.ToArray()), Task.Delay(timeout));
    }

    private void Disarm()
    {
        _timer.Stop();
        Armed.Remove(this);
    }

    // async void like the timer handlers this replaces: a failing write reaches App.UnhandledException (logged, handled).
    private async void Run(bool refresh)
    {
        var write = _write(refresh);
        Running.Add(write);
        try
        {
            await write;
        }
        finally
        {
            Running.Remove(write);
        }
    }
}
