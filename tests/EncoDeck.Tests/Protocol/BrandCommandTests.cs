// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

/// <summary>
/// The shared brand write flow (<c>BrandControllerBase.RunCommandAsync</c>): pending marker, optimistic value,
/// rollback of still-optimistic fields, last error, serialization; and the throttled liveness stamp.
/// </summary>
public class BrandCommandTests
{
    private static readonly DeviceIdentity Identity = new("dev-brand", "Test Buds", new BluetoothAddress(0x112233445566));
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly NoiseControlState Off = new(NoiseControlMode.Off);
    private static readonly NoiseControlState Anc = new(NoiseControlMode.NoiseCancellation);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class TestController(FakeByteChannel channel, ControllerContext context) : BrandControllerBase(channel, context)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        protected override Task<bool> StartSessionAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        protected override void OnReceived(ReadOnlySpan<byte> data) => Touch();

        public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(CommandResult.Ok);

        public void Stamp() => Touch();

        public void End() => EndSession();

        public Task<CommandResult> Run(string key, Func<CancellationToken, Task<CommandResult>> action, CancellationToken ct, NoiseControlState? optimistic = null) =>
            RunCommandAsync(key, Gate, action, ct, optimistic is null ? null : s => s with { NoiseControl = Observed.From(optimistic, Now, StateSource.Optimistic) });

        public Task<CommandResult> Exclusive(Func<CancellationToken, Task<CommandResult>> action, CancellationToken ct) => RunExclusiveAsync(Gate, action, ct);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public FakeByteChannel Channel { get; } = new();
        public DeviceStateStore Store { get; } = new();
        public ManualTimeProvider Time { get; } = new(Start);
        public TestController Controller { get; }

        public Harness()
        {
            Store.Reset(DeviceState.Empty with
            {
                Device = Identity,
                Connection = DeviceConnectionState.ControlReady,
                NoiseControl = Observed.From(Off, Start, StateSource.QueryResponse),
            });
            Controller = new TestController(Channel, new ControllerContext(Identity, Store, () => false, NullLoggerFactory.Instance, Time));
        }

        public DeviceState State => Store.Current;

        public ValueTask DisposeAsync() => Controller.DisposeAsync();
    }

    [Fact]
    public async Task Success_MarksPendingWithTheOptimisticValueThenClearsTheMarker()
    {
        await using var h = new Harness();
        DeviceState? during = null;

        var result = await h.Controller.Run(PendingKeys.NoiseControl, _ =>
        {
            during = h.State;
            return Task.FromResult(CommandResult.Ok);
        }, Ct, Anc);

        Assert.True(result.IsSuccess);
        Assert.True(during!.IsPending(PendingKeys.NoiseControl));
        Assert.Equal(Anc, during.NoiseControl!.Value);
        Assert.Equal(StateSource.Optimistic, during.NoiseControl.Source);
        Assert.Empty(h.State.PendingCommands);
        Assert.Null(h.State.LastError);
    }

    [Fact]
    public async Task Failure_RollsBackTheOptimisticValueAndRecordsTheError()
    {
        await using var h = new Harness();
        bool pending = false;

        var result = await h.Controller.Run(PendingKeys.NoiseControl, _ =>
        {
            pending = h.State.IsPending(PendingKeys.NoiseControl);
            return Task.FromResult(new CommandResult(CommandStatus.Rejected, "no"));
        }, Ct, Anc);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.True(pending);
        Assert.Empty(h.State.PendingCommands);
        Assert.Equal(Off, h.State.NoiseControl!.Value);
        Assert.Equal(StateSource.QueryResponse, h.State.NoiseControl.Source);
        Assert.Equal(CommandStatus.Rejected, h.State.LastError!.Status);
        Assert.Equal("no", h.State.LastError.Message);
    }

    [Fact]
    public async Task Exception_BecomesTransportErrorAndNeverLeaksTheMarker()
    {
        await using var h = new Harness();
        bool pending = false;

        var result = await h.Controller.Run(PendingKeys.Toggle(DeviceToggle.GamingMode), _ =>
        {
            pending = h.State.IsPending("toggle:GamingMode");
            throw new InvalidOperationException("boom");
        }, Ct, Anc);

        Assert.Equal(CommandStatus.TransportError, result.Status);
        Assert.True(pending);
        Assert.Empty(h.State.PendingCommands);
        Assert.Equal(Off, h.State.NoiseControl!.Value);
        Assert.Equal(CommandStatus.TransportError, h.State.LastError!.Status);
    }

    [Fact]
    public async Task Cancellation_ClearsTheMarkerRollsBackAndRecordsTheError()
    {
        await using var h = new Harness();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var command = h.Controller.Run(PendingKeys.Equalizer, async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return CommandResult.Ok;
        }, cts.Token, Anc);
        await started.Task.WaitAsync(Ct);
        Assert.True(h.State.IsPending(PendingKeys.Equalizer));
        await cts.CancelAsync();
        var result = await command;

        Assert.Equal(CommandStatus.Cancelled, result.Status);
        Assert.Empty(h.State.PendingCommands);
        Assert.Equal(Off, h.State.NoiseControl!.Value);
        Assert.Equal(CommandStatus.Cancelled, h.State.LastError!.Status);
    }

    [Fact]
    public async Task Failure_KeepsAValueTheDeviceConfirmedMeanwhile()
    {
        await using var h = new Harness();
        var transparency = new NoiseControlState(NoiseControlMode.Transparency);

        await h.Controller.Run(PendingKeys.NoiseControl, _ =>
        {
            h.Store.TryUpdate(Identity.DeviceId, s => s with { NoiseControl = Observed.From(transparency, Start, StateSource.DeviceNotification) });
            return Task.FromResult(new CommandResult(CommandStatus.Timeout));
        }, Ct, Anc);

        Assert.Equal(transparency, h.State.NoiseControl!.Value);
        Assert.Equal(StateSource.DeviceNotification, h.State.NoiseControl.Source);
    }

    [Fact]
    public async Task Failure_LeavesFieldsItDidNotMakeOptimistic()
    {
        await using var h = new Harness();
        var other = Observed.From(new EqualizerState("eq-1", 1), Start, StateSource.Optimistic);

        await h.Controller.Run(PendingKeys.NoiseControl, _ =>
        {
            h.Store.TryUpdate(Identity.DeviceId, s => s with { Equalizer = other });
            return Task.FromResult(new CommandResult(CommandStatus.Rejected));
        }, Ct, Anc);

        Assert.Equal(Off, h.State.NoiseControl!.Value);
        Assert.Same(other, h.State.Equalizer);
    }

    [Fact]
    public async Task UnsupportedAnswerIsNotRecordedAsAnError()
    {
        await using var h = new Harness();
        var result = await h.Controller.Run(PendingKeys.Gain, _ => Task.FromResult(CommandResult.Unsupported("not here")), Ct);
        Assert.Equal(CommandStatus.Unsupported, result.Status);
        Assert.Empty(h.State.PendingCommands);
        Assert.Null(h.State.LastError);
    }

    [Fact]
    public async Task ClosedSession_AnswersNotConnectedWithoutMarking()
    {
        await using var h = new Harness();
        h.Controller.End();
        bool ran = false;
        int changes = 0;
        h.Store.StateChanged += (_, _) => changes++;

        var result = await h.Controller.Run(PendingKeys.NoiseControl, _ => { ran = true; return Task.FromResult(CommandResult.Ok); }, Ct, Anc);

        Assert.Equal(CommandStatus.NotConnected, result.Status);
        Assert.False(ran);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task CommandsRunOneAtATimeAndAQueuedOneIsNotMarkedYet()
    {
        await using var h = new Harness();
        var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = h.Controller.Run(PendingKeys.NoiseControl, _ => release.Task, Ct, Anc);
        bool secondRan = false;
        var second = h.Controller.Run(PendingKeys.Equalizer, _ => { secondRan = true; return Task.FromResult(CommandResult.Ok); }, Ct);

        Assert.False(secondRan);
        Assert.False(h.State.IsPending(PendingKeys.Equalizer));
        release.SetResult(CommandResult.Ok);
        Assert.True((await first).IsSuccess);
        Assert.True((await second).IsSuccess);
        Assert.True(secondRan);
        Assert.Empty(h.State.PendingCommands);
    }

    [Fact]
    public async Task CancelledWhileQueued_NeverMarksOrRecords()
    {
        await using var h = new Harness();
        var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = h.Controller.Run(PendingKeys.NoiseControl, _ => release.Task, Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var queued = h.Controller.Run(PendingKeys.Equalizer, _ => Task.FromResult(CommandResult.Ok), cts.Token);
        await cts.CancelAsync();

        Assert.Equal(CommandStatus.Cancelled, (await queued).Status);
        Assert.False(h.State.IsPending(PendingKeys.Equalizer));
        Assert.Null(h.State.LastError);
        release.SetResult(CommandResult.Ok);
        await first;
        Assert.Empty(h.State.PendingCommands);
    }

    [Fact]
    public async Task Exclusive_SerializesWithoutMarkerOrError()
    {
        await using var h = new Harness();
        DeviceState? during = null;
        var result = await h.Controller.Exclusive(_ =>
        {
            during = h.State;
            return Task.FromResult(new CommandResult(CommandStatus.Timeout));
        }, Ct);

        Assert.Equal(CommandStatus.Timeout, result.Status);
        Assert.Empty(during!.PendingCommands);
        Assert.Null(h.State.LastError);
        Assert.Equal(CommandStatus.TransportError, (await h.Controller.Exclusive(_ => throw new IOException("gone"), Ct)).Status);
        Assert.Equal(1, h.Controller.Gate.CurrentCount);
    }

    [Fact]
    public void PendingKeysMatchTheStringsTheUiReads()
    {
        Assert.Equal("noise-control", PendingKeys.NoiseControl);
        Assert.Equal("equalizer", PendingKeys.Equalizer);
        Assert.Equal("custom-eq", PendingKeys.CustomEqualizer);
        Assert.Equal("toggle:DualDeviceConnection", PendingKeys.Toggle(DeviceToggle.DualDeviceConnection));
        Assert.StartsWith(PendingKeys.AnyToggle, PendingKeys.Toggle(DeviceToggle.PromptTone));
        Assert.Equal("prompt-volume", PendingKeys.PromptVolume);
        Assert.Equal("prompt-tone", PendingKeys.PromptTone);
        Assert.Equal("gain", PendingKeys.Gain);
        Assert.Equal("game-sound", PendingKeys.GameSound);
        Assert.Equal("bass-engine", PendingKeys.BassEngine);
        Assert.Equal("spatial", PendingKeys.Spatial);
        Assert.Equal("key-function", PendingKeys.KeyFunction);
        Assert.Equal("gesture", PendingKeys.Gesture);
        Assert.Equal("noise-cycle", PendingKeys.NoiseCycle);
        Assert.Equal("head-motion", PendingKeys.HeadMotion);
        Assert.Equal("multi-connect", PendingKeys.MultiConnect);
        Assert.Equal("preferred-device", PendingKeys.PreferredDevice);
    }

    [Fact]
    public async Task Touch_StampsLivenessAtMostEvery30Seconds()
    {
        await using var h = new Harness();
        int changes = 0;
        h.Store.StateChanged += (_, _) => changes++;

        h.Controller.Stamp();
        Assert.Equal(Start, h.State.LastResponseAt);
        Assert.Equal(1, changes);

        // Periodic chatter inside the window does not wake the UI.
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.Controller.Stamp();
        h.Time.Advance(TimeSpan.FromSeconds(20));
        h.Controller.Stamp();
        Assert.Equal(Start, h.State.LastResponseAt);
        Assert.Equal(1, changes);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Controller.Stamp();
        Assert.Equal(Start + TimeSpan.FromSeconds(31), h.State.LastResponseAt);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Touch_ReplacesAStampFromTheFuture()
    {
        await using var h = new Harness();
        // E.g. a cached state written by a clock that has since been set back.
        h.Store.TryUpdate(null, s => s with { LastResponseAt = Start + TimeSpan.FromMinutes(5) });
        h.Controller.Stamp();
        Assert.Equal(Start, h.State.LastResponseAt);
    }
}
