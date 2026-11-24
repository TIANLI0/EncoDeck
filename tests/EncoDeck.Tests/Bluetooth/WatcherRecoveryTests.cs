// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Discovery;

namespace EncoDeck.Tests.Bluetooth;

public class WatcherRecoveryTests
{
    /// <summary>Clock and timers that only move when the test fires them.</summary>
    private sealed class StepTime : TimeProvider
    {
        private long _ticks;
        public List<StepTimer> Timers { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new StepTimer(() => callback(state), dueTime);
            Timers.Add(timer);
            return timer;
        }

        /// <summary>The only timer still pending (asserts there is exactly one).</summary>
        public StepTimer Pending => Assert.Single(Timers, t => !t.Disposed && !t.Fired);
    }

    private sealed class StepTimer(Action fire, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; } = due;
        public bool Disposed { get; private set; }
        public bool Fired { get; private set; }
        public void Fire() { Fired = true; fire(); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    /// <summary>Stands in for one DeviceWatcher: records its "stopped" callback and whether it was torn down.</summary>
    private sealed class FakeWatcher(Action<string> stopped) : IDisposable
    {
        public bool Released { get; private set; }
        public void Abort() => stopped("Aborted");
        public void Dispose() => Released = true;
    }

    private sealed class Harness
    {
        public StepTime Time { get; } = new();
        public List<FakeWatcher> Watchers { get; } = [];
        public int Clears;
        public int FailStarts;
        public WatcherRecovery Recovery { get; }

        public Harness()
        {
            Recovery = new WatcherRecovery("test", stopped =>
            {
                if (FailStarts > 0) { FailStarts--; throw new InvalidOperationException("no adapter"); }
                var watcher = new FakeWatcher(stopped);
                Watchers.Add(watcher);
                return watcher;
            }, () => Clears++, time: Time);
        }
    }

    [Fact]
    public void Abort_ClearsState_ReleasesTheOldWatcher_AndRestartsAfterBackoff()
    {
        var h = new Harness();
        h.Recovery.Start();
        var first = Assert.Single(h.Watchers);

        first.Abort();
        Assert.Equal(1, h.Clears);
        Assert.True(first.Released);
        Assert.False(h.Recovery.IsRunning);
        Assert.Single(h.Watchers); // not restarted synchronously
        Assert.Equal(TimeSpan.FromSeconds(1), h.Time.Pending.Due);

        h.Time.Pending.Fire();
        Assert.Equal(2, h.Watchers.Count);
        Assert.True(h.Recovery.IsRunning);
    }

    [Fact]
    public void RepeatedAborts_BackOff_1_2_5_ThenEvery30Seconds()
    {
        var h = new Harness();
        h.Recovery.Start();
        var delays = new List<TimeSpan>();
        for (int i = 0; i < 6; i++)
        {
            h.Watchers[^1].Abort();
            delays.Add(h.Time.Pending.Due);
            h.Time.Pending.Fire();
        }
        Assert.Equal([1, 2, 5, 30, 30, 30], delays.Select(d => (int)d.TotalSeconds));
    }

    [Fact]
    public void WatcherThatRanLongEnough_StartsTheBackoffOver()
    {
        var h = new Harness();
        h.Recovery.Start();
        h.Watchers[^1].Abort();
        h.Time.Pending.Fire();
        h.Watchers[^1].Abort();
        Assert.Equal(TimeSpan.FromSeconds(2), h.Time.Pending.Due);
        h.Time.Pending.Fire();

        h.Time.Advance(WatcherRecovery.HealthyAfter);
        h.Watchers[^1].Abort();
        Assert.Equal(TimeSpan.FromSeconds(1), h.Time.Pending.Due);
    }

    [Fact]
    public void FailedRestart_KeepsBackingOff_WithoutClearingAgain()
    {
        var h = new Harness();
        h.Recovery.Start();
        h.Watchers[^1].Abort();
        h.FailStarts = 2;

        h.Time.Pending.Fire();
        Assert.Equal(TimeSpan.FromSeconds(2), h.Time.Pending.Due);
        h.Time.Pending.Fire();
        Assert.Equal(TimeSpan.FromSeconds(5), h.Time.Pending.Due);
        h.Time.Pending.Fire();

        Assert.Equal(2, h.Watchers.Count);
        Assert.True(h.Recovery.IsRunning);
        Assert.Equal(1, h.Clears);
    }

    [Fact]
    public void LateStopFromAReplacedWatcher_IsIgnored()
    {
        var h = new Harness();
        h.Recovery.Start();
        var first = h.Watchers[0];
        first.Abort();
        h.Time.Pending.Fire();

        first.Abort();
        Assert.Equal(1, h.Clears);
        Assert.True(h.Recovery.IsRunning);
        Assert.DoesNotContain(h.Time.Timers, t => !t.Disposed && !t.Fired);
    }

    [Fact]
    public void Dispose_ReleasesTheWatcher_AndNeverRestarts()
    {
        var h = new Harness();
        h.Recovery.Start();
        h.Watchers[^1].Abort();
        var pending = h.Time.Pending;

        h.Recovery.Dispose();
        Assert.True(pending.Disposed);
        // Even a timer callback already on its way does not start a watcher after Dispose.
        pending.Fire();
        Assert.Single(h.Watchers);

        var other = new Harness();
        other.Recovery.Start();
        other.Recovery.Dispose();
        Assert.True(other.Watchers[0].Released);
        // The Stopped our own teardown causes is not taken for an abort.
        other.Watchers[0].Abort();
        Assert.Equal(0, other.Clears);
        Assert.Empty(other.Time.Timers);
    }
}
