// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.DokiDoki;
using EncoDeck.Tests.Fakes;
using EncoDeck.Tests.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.DokiDoki;

public sealed class DokiDokiDeviceControllerTests
{
    private static readonly DeviceIdentity Meta2 = new("BluetoothLE#dokidoki-test", "DK-META2", new BluetoothAddress(0xC0FFEE123456));
    private static readonly TimeSpan Never = TimeSpan.FromHours(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness : IAsyncDisposable
    {
        public FakeByteChannel Channel { get; } = new();
        public DeviceStateStore Store { get; } = new();
        public bool AllowExperimental { get; set; } = true;
        public DokiDokiDeviceController Controller { get; }

        public Harness(DokiDokiFamily family, DokiDokiTiming? timing = null)
        {
            Store.Reset(DeviceState.Empty with { Device = Meta2 });
            var context = new ControllerContext(Meta2, Store, () => AllowExperimental, NullLoggerFactory.Instance, TimeProvider.System);
            Controller = new DokiDokiDeviceController(Channel, context, family,
                timing ?? new DokiDokiTiming(Never, TimeSpan.Zero, TimeSpan.FromSeconds(2)));
        }

        /// <summary>Frames other than heartbeats, as hex.</summary>
        public List<string> Actions() => Channel.Written
            .Where(f => !DokiDokiCommands.IsHeartbeatFrame(f))
            .Select(Convert.ToHexString)
            .ToList();

        public ValueTask DisposeAsync() => Controller.DisposeAsync();
    }

    private static string Hex(byte[] frame) => Convert.ToHexString(frame);

    private static List<string> StopFrames(int firstId, DokiDokiFamily family) =>
        DokiDokiCommands.EncodeStopAll(firstId, family).Select(Hex).ToList();

    [Fact]
    public async Task Initialize_Meta2_StopsEveryChannelThenSendsHeartbeat()
    {
        await using var h = new Harness(DokiDokiFamily.Meta2);
        Assert.True(await h.Controller.InitializeAsync(Ct));

        var written = h.Channel.Written.Select(Hex).ToList();
        Assert.Equal([.. StopFrames(1, DokiDokiFamily.Meta2), Hex(DokiDokiCommands.EncodeHeartbeat(4))], written);
    }

    [Fact]
    public async Task Initialize_Bh2Pro_SendsSingleStopCommand()
    {
        await using var h = new Harness(DokiDokiFamily.Bh2Pro);
        Assert.True(await h.Controller.InitializeAsync(Ct));

        Assert.Equal([.. StopFrames(1, DokiDokiFamily.Bh2Pro), Hex(DokiDokiCommands.EncodeHeartbeat(2))],
            h.Channel.Written.Select(Hex).ToList());
        Assert.Equal([ActuatorChannel.Linear, ActuatorChannel.Vibration], h.Controller.Channels);
    }

    [Fact]
    public async Task Initialize_FailsWhenTheChannelCannotBeWritten()
    {
        await using var h = new Harness(DokiDokiFamily.Meta2);
        h.Channel.FailWritesWith = new IOException("gatt down");
        Assert.False(await h.Controller.InitializeAsync(Ct));
        Assert.True(h.Controller.Closed.IsCompleted);
    }

    [Fact]
    public async Task SetIntensity_ClampsAndWritesActionFrame_WithoutClaimingAcknowledgement()
    {
        await using var h = new Harness(DokiDokiFamily.Meta2);
        await h.Controller.InitializeAsync(Ct);

        var result = await h.Controller.SetIntensityAsync(ActuatorChannel.Linear, 250, Ct);

        Assert.True(result.IsSuccess);
        Assert.Contains("does not acknowledge", result.Detail);
        Assert.Equal(Hex(DokiDokiCommands.EncodeAction(5, DokiDokiFamily.Meta2, DokiDokiChannel.Linear, 100)), h.Actions().Last());
    }

    [Fact]
    public async Task SetIntensity_NonzeroRequiresExperimental_ButZeroIsAlwaysAllowed()
    {
        await using var h = new Harness(DokiDokiFamily.Meta2) { AllowExperimental = false };
        await h.Controller.InitializeAsync(Ct);
        int before = h.Channel.Written.Count;

        var blocked = await h.Controller.SetIntensityAsync(ActuatorChannel.Vibration, 10, Ct);
        Assert.Equal(CommandStatus.Unsupported, blocked.Status);
        Assert.Equal(before, h.Channel.Written.Count);

        var zero = await h.Controller.SetIntensityAsync(ActuatorChannel.Vibration, -5, Ct);
        Assert.True(zero.IsSuccess);
        Assert.Equal(Hex(DokiDokiCommands.EncodeAction(5, DokiDokiFamily.Meta2, DokiDokiChannel.Vibration, 0)), h.Actions().Last());
    }

    [Fact]
    public async Task SetIntensity_UnsupportedChannelIsRejectedWithoutWriting()
    {
        await using var h = new Harness(DokiDokiFamily.Bh2Pro);
        await h.Controller.InitializeAsync(Ct);
        int before = h.Channel.Written.Count;

        var result = await h.Controller.SetIntensityAsync(ActuatorChannel.Rotary, 10, Ct);

        Assert.Equal(CommandStatus.Unsupported, result.Status);
        Assert.Equal(before, h.Channel.Written.Count);
    }

    [Fact]
    public async Task SetIntensity_RapidChangesAreRateLimitedAndOnlyTheLatestIsSent()
    {
        await using var h = new Harness(DokiDokiFamily.Meta2, new DokiDokiTiming(Never, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2)));
        await h.Controller.InitializeAsync(Ct);

        Assert.True((await h.Controller.SetIntensityAsync(ActuatorChannel.Vibration, 10, Ct)).IsSuccess);
        var burst = Enumerable.Range(1, 5).Select(i => h.Controller.SetIntensityAsync(ActuatorChannel.Vibration, 10 + i, Ct)).ToArray();
        var results = await Task.WhenAll(burst);

        Assert.All(results[..^1], r => Assert.Equal(CommandStatus.Cancelled, r.Status));
        Assert.True(results[^1].IsSuccess);
        var actions = h.Actions().Skip(3).ToList();
        Assert.Equal(2, actions.Count);
        Assert.EndsWith(Convert.ToHexString([15, unchecked((byte)-(0x08 + 15))]), actions[1]);
    }

    [Fact]
    public async Task StopAll_IsNotDelayedByRateLimiting_AndSupersedesPendingActions()
    {
        await using var h = new Harness(DokiDokiFamily.Meta2, new DokiDokiTiming(Never, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        await h.Controller.InitializeAsync(Ct);
        await h.Controller.SetIntensityAsync(ActuatorChannel.Vibration, 10, Ct);

        var pending = h.Controller.SetIntensityAsync(ActuatorChannel.Vibration, 50, Ct);
        var started = DateTime.UtcNow;
        var stop = await h.Controller.StopAllAsync();

        Assert.True(stop.IsSuccess);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(500));
        Assert.False(pending.IsCompleted);
        Assert.Equal(CommandStatus.Cancelled, (await pending).Status);
        // Nothing nonzero may follow the stop.
        Assert.Equal(StopFrames(6, DokiDokiFamily.Meta2), h.Actions()[^3..]);
    }

    [Fact]
    public async Task Dispose_WritesStopBeforeTheChannelCloses()
    {
        var h = new Harness(DokiDokiFamily.Meta2);
        await h.Controller.InitializeAsync(Ct);
        await h.Controller.SetIntensityAsync(ActuatorChannel.Rotary, 30, Ct);

        await h.Controller.DisposeAsync();

        Assert.Equal(StopFrames(6, DokiDokiFamily.Meta2), h.Actions()[^3..]);
        Assert.True(h.Channel.IsDisposed);
        Assert.Equal(CommandStatus.NotConnected, (await h.Controller.SetIntensityAsync(ActuatorChannel.Rotary, 30, Ct)).Status);
    }

    [Fact]
    public async Task HeartbeatIsSentPeriodically()
    {
        await using var h = new Harness(DokiDokiFamily.Bh2Pro, new DokiDokiTiming(TimeSpan.FromMilliseconds(30), TimeSpan.Zero, TimeSpan.FromSeconds(2)));
        await h.Controller.InitializeAsync(Ct);

        await OppoDeviceControllerTests.WaitUntil(() => h.Channel.Written.Count(f => DokiDokiCommands.IsHeartbeatFrame(f)) >= 3);
    }

    [Fact]
    public async Task HeartbeatFailure_ClosesTheSession_AndDisposeStillAttemptsStop()
    {
        var h = new Harness(DokiDokiFamily.Meta2, new DokiDokiTiming(TimeSpan.FromMilliseconds(30), TimeSpan.Zero, TimeSpan.FromSeconds(2)));
        await h.Controller.InitializeAsync(Ct);
        h.Channel.FailWritesWith = new IOException("link lost");

        await h.Controller.Closed.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        h.Channel.FailWritesWith = null;
        int before = h.Channel.Written.Count;
        await h.Controller.DisposeAsync();

        Assert.Equal(3, h.Channel.Written.Count - before);
        Assert.All(h.Channel.Written.Skip(before), f => Assert.Equal(0, f[5]));
    }

    [Fact]
    public async Task WriteFailure_ReportsTransportError()
    {
        await using var h = new Harness(DokiDokiFamily.Meta2);
        await h.Controller.InitializeAsync(Ct);
        h.Channel.FailWritesWith = new IOException("gatt down");

        var result = await h.Controller.SetIntensityAsync(ActuatorChannel.Vibration, 10, Ct);

        Assert.Equal(CommandStatus.TransportError, result.Status);
        Assert.True(h.Controller.Closed.IsCompleted);
    }

    [Fact]
    public async Task HeadsetCommandsAreUnsupported()
    {
        await using var h = new Harness(DokiDokiFamily.Meta2);
        await h.Controller.InitializeAsync(Ct);

        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Off), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetEqualizerPresetAsync("x", Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.RefreshAsync(Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.FindEarbudsAsync(true, Ct)).Status);
    }

    [Fact]
    public async Task MessageIdsWrapFrom15To1()
    {
        await using var h = new Harness(DokiDokiFamily.Meta2);
        await h.Controller.InitializeAsync(Ct);

        for (int i = 0; i < 12; i++) await h.Controller.SetIntensityAsync(ActuatorChannel.Vibration, 0, Ct);

        var ids = h.Channel.Written.Select(f => (int)f[0]).ToList();
        Assert.Equal([.. Enumerable.Range(1, 15), 1], ids);
    }
}
