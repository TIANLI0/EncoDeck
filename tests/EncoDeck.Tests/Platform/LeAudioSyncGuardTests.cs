// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.LeAudioSync;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Platform;

public sealed class LeAudioSyncGuardTests
{
    private const string Render = "{0.0.0.00000000}.{7900dd66-6f67-49c5-aa6e-2f71567cce16}";
    private const string Capture = "{0.0.1.00000000}.{c3c25e91-651b-47d6-a41e-51f4bb05dbba}";
    private static readonly LeAudioEndpoints Endpoints = new(Render, Capture);
    private static readonly LeAudioSyncTimings Fast = new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task UnsupportedPcHoldsNothing()
    {
        var platform = new FakePlatform { Support = LeAudioSyncSupport.NotIntel };
        await using var guard = new LeAudioSyncGuard(platform, NullLogger.Instance, Fast);
        guard.SetEndpoints(Endpoints);
        guard.SetEnabled(true);
        var status = await WaitForAsync(guard, LeAudioSyncState.Unsupported);
        Assert.Equal(LeAudioSyncSupport.NotIntel, status.Support);
        Assert.Equal(0, platform.Opens);
    }

    [Fact]
    public async Task WaitsForTheEarbuds()
    {
        var platform = new FakePlatform();
        await using var guard = new LeAudioSyncGuard(platform, NullLogger.Instance, Fast);
        guard.SetEnabled(true);
        await WaitForAsync(guard, LeAudioSyncState.WaitingForDevice);
        Assert.Equal(0, platform.Opens);
        guard.SetEndpoints(Endpoints);
        await WaitForAsync(guard, LeAudioSyncState.NeedsCheck);
        Assert.Equal(1, platform.Opens);
    }

    [Fact]
    public async Task CheckThenConfirmKeepsTheLink()
    {
        var platform = new FakePlatform();
        await using var guard = await HeldAsync(platform);
        guard.StartCheck();
        await WaitForAsync(guard, LeAudioSyncState.Checking);
        Assert.True(platform.Last!.CheckPlaying);
        guard.ConfirmAligned();
        await WaitForAsync(guard, LeAudioSyncState.Aligned);
        Assert.False(platform.Last.CheckPlaying);
        Assert.Equal(1, platform.Opens);
        Assert.Equal(0, platform.Closes);
    }

    [Fact]
    public async Task OffsetRebuildsAndChecksAgain()
    {
        var platform = new FakePlatform();
        await using var guard = await HeldAsync(platform);
        guard.StartCheck();
        await WaitForAsync(guard, LeAudioSyncState.Checking);
        var first = platform.Last!;
        guard.ReportOffset();
        await WaitUntilAsync(() => platform.Opens == 2);
        var status = await WaitForAsync(guard, LeAudioSyncState.Checking);
        Assert.Equal(1, status.Rebuilds);
        Assert.True(first.Disposed);
        Assert.False(first.CheckPlaying);
        // The check plays on the new link.
        Assert.True(platform.Last!.CheckPlaying);
    }

    [Fact]
    public async Task RebuildWaitsForOtherPrograms()
    {
        var platform = new FakePlatform { OtherSessions = true };
        await using var guard = await HeldAsync(platform);
        guard.StartCheck();
        await WaitForAsync(guard, LeAudioSyncState.Checking);
        guard.ReportOffset();
        await WaitForAsync(guard, LeAudioSyncState.WaitingForIdle);
        await Task.Delay(100);
        Assert.Equal(0, platform.Closes);
        platform.OtherSessions = false;
        await WaitUntilAsync(() => platform.Opens == 2);
        await WaitForAsync(guard, LeAudioSyncState.Checking);
    }

    [Fact]
    public async Task UnansweredCheckFallsBackToNeedsCheck()
    {
        var platform = new FakePlatform();
        await using var guard = await HeldAsync(platform);
        guard.StartCheck();
        await WaitForAsync(guard, LeAudioSyncState.Checking);
        await WaitForAsync(guard, LeAudioSyncState.NeedsCheck, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task MicrophoneUseAsksForANewCheck()
    {
        var platform = new FakePlatform();
        await using var guard = await HeldAsync(platform);
        guard.StartCheck();
        guard.ConfirmAligned();
        await WaitForAsync(guard, LeAudioSyncState.Aligned);
        platform.Capturing = true;
        await WaitForAsync(guard, LeAudioSyncState.MicrophoneInUse);
        platform.Capturing = false;
        await WaitForAsync(guard, LeAudioSyncState.NeedsCheck);
        // The link itself was never closed by the guard.
        Assert.Equal(0, platform.Closes);
    }

    [Fact]
    public async Task LostEndpointWaitsAndReopens()
    {
        var platform = new FakePlatform();
        await using var guard = await HeldAsync(platform);
        platform.Last!.Lose();
        await WaitForAsync(guard, LeAudioSyncState.WaitingForDevice);
        guard.SetEndpoints(Endpoints);
        await WaitForAsync(guard, LeAudioSyncState.NeedsCheck);
        Assert.Equal(2, platform.Opens);
    }

    [Fact]
    public async Task FailedOpenWaitsForTheDevice()
    {
        var platform = new FakePlatform { FailOpen = true };
        await using var guard = new LeAudioSyncGuard(platform, NullLogger.Instance, Fast);
        guard.SetEnabled(true);
        guard.SetEndpoints(Endpoints);
        var status = await WaitForAsync(guard, LeAudioSyncState.WaitingForDevice);
        Assert.Equal("endpoint gone", status.Detail);
        platform.FailOpen = false;
        // The same endpoints again open the stream (the guard forgot them).
        guard.SetEndpoints(Endpoints);
        await WaitForAsync(guard, LeAudioSyncState.NeedsCheck);
    }

    [Fact]
    public async Task DisablingReleasesTheLink()
    {
        var platform = new FakePlatform();
        await using var guard = await HeldAsync(platform);
        guard.SetEnabled(false);
        await WaitForAsync(guard, LeAudioSyncState.Off);
        Assert.Equal(1, platform.Closes);
    }

    [Theory]
    [InlineData(null, true, LeAudioSyncSupport.NoRadio)]
    [InlineData((ushort)29, true, LeAudioSyncSupport.NotIntel)]
    [InlineData((ushort)93, true, LeAudioSyncSupport.NotIntel)]
    [InlineData((ushort)2, false, LeAudioSyncSupport.NoIntelOffload)]
    [InlineData((ushort)2, true, LeAudioSyncSupport.Supported)]
    public void NeedsIntelRadioAndOffload(ushort? manufacturer, bool offload, LeAudioSyncSupport expected) =>
        Assert.Equal(expected, LeAudioSyncEligibility.Evaluate(() => manufacturer, () => offload));

    [Fact]
    public void OffloadIsOnlyCheckedForIntel() =>
        Assert.Equal(LeAudioSyncSupport.NotIntel, LeAudioSyncEligibility.Evaluate(() => 29, () => throw new InvalidOperationException()));

    private static async Task<LeAudioSyncGuard> HeldAsync(FakePlatform platform)
    {
        var guard = new LeAudioSyncGuard(platform, NullLogger.Instance, Fast);
        guard.SetEnabled(true);
        guard.SetEndpoints(Endpoints);
        await WaitForAsync(guard, LeAudioSyncState.NeedsCheck);
        return guard;
    }

    private static async Task<LeAudioSyncStatus> WaitForAsync(LeAudioSyncGuard guard, LeAudioSyncState state, TimeSpan? timeout = null)
    {
        await WaitUntilAsync(() => guard.Status.State == state, timeout);
        return guard.Status;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached");
            await Task.Delay(5);
        }
    }

    private sealed class FakePlatform : ILeAudioSyncPlatform
    {
        public LeAudioSyncSupport Support { get; set; } = LeAudioSyncSupport.Supported;
        public volatile bool OtherSessions;
        public volatile bool Capturing;
        public volatile bool FailOpen;
        public int Opens;
        public int Closes;
        public FakeStream? Last { get; private set; }

        public LeAudioSyncSupport CheckSupport() => Support;

        public IKeepAliveStream OpenKeepAlive(string renderEndpointId)
        {
            Assert.Equal(Render, renderEndpointId);
            if (FailOpen) throw new InvalidOperationException("endpoint gone");
            Interlocked.Increment(ref Opens);
            Last = new FakeStream(() => Interlocked.Increment(ref Closes));
            return Last;
        }

        public bool HasOtherActiveSessions(string renderEndpointId) => OtherSessions;

        public bool HasActiveCapture(string captureEndpointId)
        {
            Assert.Equal(Capture, captureEndpointId);
            return Capturing;
        }
    }

    private sealed class FakeStream(Action onDispose) : IKeepAliveStream
    {
        public volatile bool CheckPlaying;
        public bool Disposed;

        public event EventHandler? Lost;

        public void Lose() => Lost?.Invoke(this, EventArgs.Empty);

        public void PlayCheck(TimeSpan duration) => CheckPlaying = true;

        public void StopCheck() => CheckPlaying = false;

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            CheckPlaying = false;
            onDispose();
        }
    }
}
