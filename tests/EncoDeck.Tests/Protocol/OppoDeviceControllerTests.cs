// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Tests.Fakes;

namespace EncoDeck.Tests.Protocol;

public class OppoDeviceControllerTests
{
    private static readonly DeviceIdentity X4 = new("dev-x4", "OPPO Enco X4", new BluetoothAddress(0x112233445566));

    private sealed class Harness : IAsyncDisposable
    {
        public FakeOppoDevice Device { get; } = new();
        public DeviceStateStore Store { get; } = new();
        public OppoSession Session { get; }
        public OppoDeviceController Controller { get; }
        public bool AllowExperimental { get; set; }

        public Harness(OppoModelDefinition? model = null, bool leAudioTransport = false)
        {
            Store.Reset(DeviceState.Empty with { Device = X4, Connection = DeviceConnectionState.ControlConnecting });
            Session = new OppoSession(Device.Channel);
            Session.Start();
            Controller = new OppoDeviceController(Session, new OppoDeviceProfile(model ?? OppoModels.EncoX4), X4, Store, () => AllowExperimental)
            {
                IsLeAudioTransport = leAudioTransport,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            await Session.DisposeAsync();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The X4 bitmap without bit 64 (0x0136/0x0433), as the Enco X3 reports it.</summary>
    private static readonly byte[] CapabilitiesWithoutLeBrSwitch = Convert.FromHexString("FF775AEEEE8FA65F1600000000000000");

    private static bool SentLeAudioTransport(FakeOppoDevice device)
    {
        lock (device.Received)
            return device.Received.Any(f => f.Command == OppoCommand.SetFeature && f.Payload.AsSpan().SequenceEqual(new byte[] { 0x1F, 0x01 }));
    }

    [Fact]
    public async Task Initialize_OverLeAudio_WithoutLeBrSwitch_ReportsLeTransport()
    {
        await using var h = new Harness(leAudioTransport: true);
        h.Device.Capabilities = CapabilitiesWithoutLeBrSwitch;
        Assert.True(await h.Controller.InitializeAsync(Ct));
        Assert.True(SentLeAudioTransport(h.Device));
    }

    [Theory]
    [InlineData(true, false)]   // X4: has the LE/BR switch, verified without feature 0x1F
    [InlineData(false, true)]   // Classic link: the transport is not LE
    public async Task Initialize_DoesNotReportLeTransport_OnClassicOrWithLeBrSwitch(bool leAudioTransport, bool withoutLeBrSwitch)
    {
        await using var h = new Harness(leAudioTransport: leAudioTransport);
        if (withoutLeBrSwitch) h.Device.Capabilities = CapabilitiesWithoutLeBrSwitch;
        Assert.True(await h.Controller.InitializeAsync(Ct));
        Assert.False(SentLeAudioTransport(h.Device));
    }

    [Theory]
    [InlineData(true, true, CommandStatus.Success)]
    [InlineData(true, false, CommandStatus.Unsupported)]  // X4: the LE/BR switch replaces 0x1F
    [InlineData(false, true, CommandStatus.NotConnected)] // control still on Classic
    public async Task ReportLeAudioTransport_AnswersByTransportAndFirmware(bool leAudioTransport, bool withoutLeBrSwitch, CommandStatus expected)
    {
        await using var h = new Harness(leAudioTransport: leAudioTransport);
        if (withoutLeBrSwitch) h.Device.Capabilities = CapabilitiesWithoutLeBrSwitch;
        Assert.True(await h.Controller.InitializeAsync(Ct));
        Assert.Equal(expected, (await h.Controller.ReportLeAudioTransportAsync(Ct)).Status);
    }

    [Fact]
    public async Task Initialize_PopulatesStateFromDevice()
    {
        await using var h = new Harness();
        h.Device.Battery = [0x01, 0xC6, 0x02, 0x50, 0x03, 0x46];
        Assert.True(await h.Controller.InitializeAsync(Ct));

        var s = h.Store.Current;
        Assert.Equal(new BatteryReading(70, true), s.Battery.Left!.Value);
        Assert.Equal(new BatteryReading(80, false), s.Battery.Right!.Value);
        Assert.Equal(new BatteryReading(70, false), s.Battery.Case!.Value);
        Assert.Equal(StateSource.QueryResponse, s.Battery.Left.Source);
        Assert.Equal(new NoiseControlState(NoiseControlMode.Off), s.NoiseControl!.Value);
        Assert.Equal("116", s.Firmware!.Value.Version);
        Assert.Equal("06EC10", s.Firmware.Value.ModelCode);
        Assert.True(s.Wear!.Value.LeftInEar);
        Assert.Equal(0x06EC10u, h.Controller.ProductId);
        Assert.Equal("oppo-enco-x4", h.Controller.Profile.ModelId);

        // Like the official client: every offered event, ordered as signed bytes.
        var subscribe = Assert.Single(h.Device.Received, f => f.Command == OppoCommand.SubscribeEvents);
        Assert.Equal(Convert.FromHexString("09F1F2F301020304080B"), subscribe.Payload);
    }

    [Fact]
    public async Task Initialize_FailsWithoutHandshake()
    {
        await using var h = new Harness();
        h.Device.AnswerHandshake = false;
        Assert.False(await h.Controller.InitializeAsync(Ct));
    }

    [Fact]
    public async Task SetNoiseControl_Success_IsConfirmedByReadBack()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);

        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation, "deep"), Ct);

        Assert.Equal(CommandStatus.Success, result.Status);
        var set = Assert.Single(h.Device.Received, f => f.Command == OppoCommand.SetNoiseControl);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x10 }, set.Payload);
        Assert.Equal(OppoSequenceAllocator.FixedSet, set.Sequence);
        var state = h.Store.Current;
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "deep"), state.NoiseControl!.Value);
        Assert.True(state.NoiseControl.IsConfirmed);
        Assert.Empty(state.PendingCommands);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task SetNoiseControl_ParentMode_AcceptsAnyLevel()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation), Ct);
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x02 }, h.Device.Received.Last(f => f.Command == OppoCommand.SetNoiseControl).Payload);
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "smart"), h.Store.Current.NoiseControl!.Value);
    }

    [Fact]
    public async Task SetNoiseControl_Rejected_RollsBack()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        h.Device.SetStatus = 0x02;

        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        var state = h.Store.Current;
        Assert.Equal(new NoiseControlState(NoiseControlMode.Off), state.NoiseControl!.Value);
        Assert.NotEqual(StateSource.Optimistic, state.NoiseControl.Source);
        Assert.Empty(state.PendingCommands);
        Assert.Equal(CommandStatus.Rejected, state.LastError!.Status);
    }

    [Fact]
    public async Task SetNoiseControl_AckedButNotApplied_ReportsDeviceTruth()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        h.Device.ApplySets = false;

        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Equal(NoiseControlMode.Off, h.Store.Current.NoiseControl!.Value.Mode);
    }

    [Fact]
    public async Task SetToggle_AppliedAMomentAfterTheAck_Succeeds()
    {
        // Enco Free4 game mode: acknowledged, but the first read-back still shows the old value.
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        h.Device.FeatureSettleQueries = 2;

        var result = await h.Controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(h.Store.Current.Toggles[DeviceToggle.GamingMode].Value);
        Assert.Null(h.Store.Current.LastError);
    }

    [Fact]
    public async Task SetNoiseControl_NoAck_TimesOutAndRollsBack()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        h.Device.DropSets = true;

        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct);

        Assert.Equal(CommandStatus.Timeout, result.Status);
        Assert.Equal(NoiseControlMode.Off, h.Store.Current.NoiseControl!.Value.Mode);
        Assert.Empty(h.Store.Current.PendingCommands);
    }

    [Fact]
    public async Task AdaptiveLess_UsesTwoByteMask()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Adaptive, "less"), Ct);
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00, 0x10 }, h.Device.Received.Last(f => f.Command == OppoCommand.SetNoiseControl).Payload);
    }

    [Fact]
    public async Task AdaptiveButton_SendsTopLevelIndex11_LikeOfficialClient()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Adaptive), Ct);
        Assert.True(result.IsSuccess);
        // Index 11 needs a two-byte mask; the device then reports its child level.
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00, 0x08 }, h.Device.Received.Last(f => f.Command == OppoCommand.SetNoiseControl).Payload);
        Assert.Equal(new NoiseControlState(NoiseControlMode.Adaptive, "standard"), h.Store.Current.NoiseControl!.Value);
    }

    [Fact]
    public async Task ReportedIndices_MapToX4Modes()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        var expected = new Dictionary<int, NoiseControlState>
        {
            [3] = new(NoiseControlMode.Off),
            [4] = new(NoiseControlMode.NoiseCancellation, "deep"),
            [5] = new(NoiseControlMode.NoiseCancellation, "medium"),
            [6] = new(NoiseControlMode.NoiseCancellation, "light"),
            [7] = new(NoiseControlMode.NoiseCancellation, "smart"),
            [8] = new(NoiseControlMode.Transparency),
            [12] = new(NoiseControlMode.Adaptive, "less"),
            [13] = new(NoiseControlMode.Adaptive, "standard"),
            [14] = new(NoiseControlMode.Adaptive, "more"),
        };
        foreach (var (index, state) in expected)
        {
            h.Device.PushNoiseEvent(index);
            await WaitUntil(() => h.Store.Current.NoiseControl?.Value == state);
        }
    }

    [Fact]
    public async Task UnknownOption_IsUnsupported()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation, "made-up"), Ct);
        Assert.Equal(CommandStatus.Unsupported, result.Status);
    }

    [Fact]
    public async Task MismatchedProductId_DemotesToReadOnlyGenericProfile()
    {
        await using var h = new Harness();
        h.Device.ProductId = 0x123456;
        await h.Controller.InitializeAsync(Ct);

        Assert.True(h.Controller.Profile.Definition.IsGeneric);
        Assert.Equal("oppo-generic", h.Store.Current.Profile!.ModelId);
        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct);
        Assert.Equal(CommandStatus.Unsupported, result.Status);
        Assert.DoesNotContain(h.Device.Received, f => f.Command == OppoCommand.SetNoiseControl);
    }

    [Fact]
    public async Task CapabilityBitmap_WithoutAncBit_BlocksNoiseCommands()
    {
        await using var h = new Harness();
        h.Device.Capabilities = [0x00, 0x00, 0x00];
        await h.Controller.InitializeAsync(Ct);
        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct);
        Assert.Equal(CommandStatus.Unsupported, result.Status);
        Assert.DoesNotContain(h.Device.Received, f => f.Command == OppoCommand.QueryNoiseControl || f.Command == OppoCommand.SetNoiseControl);
    }

    [Fact]
    public async Task DeviceEvents_UpdateState()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);

        h.Device.PushNoiseEvent(8);
        h.Device.PushBatteryEvent(0x01, 0x5A); // left 90 %, right not reported
        await WaitUntil(() => h.Store.Current.NoiseControl?.Value.Mode == NoiseControlMode.Transparency && h.Store.Current.Battery.Left?.Value.Level == 90);

        var s = h.Store.Current;
        Assert.Equal(StateSource.DeviceNotification, s.NoiseControl!.Source);
        Assert.Equal(100, s.Battery.Right!.Value.Level); // kept from the earlier query
    }

    [Fact]
    public async Task SetNoiseControl_StaleNotWorn_RereadsWearBeforeRefusing()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        // Missed wear events (a silent GATT earbud) left the earbuds "in the case".
        h.Store.TryUpdate(X4.DeviceId, s => s with { Wear = Observed.From(new WearStatus(false, false, true, true, true), DateTimeOffset.UtcNow, StateSource.QueryResponse) });

        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct);

        Assert.True(result.IsSuccess);
        Assert.True(h.Store.Current.Wear!.Value.LeftInEar);
        Assert.Single(h.Device.Received, f => f.Command == OppoCommand.SetNoiseControl);
    }

    [Fact]
    public async Task SetNoiseControl_NotWorn_IsRefusedWithoutSending()
    {
        await using var h = new Harness();
        h.Device.Wear = [0x04, 0x04, 0x04];
        await h.Controller.InitializeAsync(Ct);

        var result = await h.Controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Equal(OppoDeviceController.WearRequiredDetail, result.Detail);
        Assert.DoesNotContain(h.Device.Received, f => f.Command == OppoCommand.SetNoiseControl);
    }

    [Fact]
    public async Task KeepAlive_RereadsBatteryWhileIdle()
    {
        var device = new FakeOppoDevice();
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = X4 });
        await using var session = new OppoSession(device.Channel);
        session.Start();
        await using var controller = new OppoDeviceController(session, new OppoDeviceProfile(OppoModels.EncoX4), X4, store)
        {
            KeepAliveInterval = TimeSpan.FromMilliseconds(50),
        };
        await controller.InitializeAsync(Ct);

        device.Battery = [0x01, 0x32, 0x02, 0x32];
        await WaitUntil(() => store.Current.Battery.Left?.Value.Level == 50);
    }

    [Fact]
    public async Task MissingCaseEntry_KeepsLastReadingWithOriginalTimestamp()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-20T10:00:00Z"));
        var device = new FakeOppoDevice { Battery = [0x01, 0x64, 0x02, 0x64, 0x03, 0x46] };
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = X4 });
        await using var session = new OppoSession(device.Channel);
        session.Start();
        await using var controller = new OppoDeviceController(session, new OppoDeviceProfile(OppoModels.EncoX4), X4, store, time: time);
        await controller.InitializeAsync(Ct);
        var caseReadAt = store.Current.Battery.Case!.Timestamp;

        time.Advance(TimeSpan.FromMinutes(30));
        device.PushBatteryEvent(0x01, 0x5A, 0x02, 0x5A);
        await WaitUntil(() => store.Current.Battery.Left?.Value.Level == 90);

        var battery = store.Current.Battery;
        Assert.Equal(70, battery.Case!.Value.Level);
        Assert.Equal(caseReadAt, battery.Case.Timestamp);
        Assert.True(battery.Case.IsStale(time.GetUtcNow(), TimeSpan.FromMinutes(5)));
        Assert.False(battery.Left!.IsStale(time.GetUtcNow(), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task UpdatesForPreviousDevice_AreDroppedAfterSwitch()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);

        var other = new DeviceIdentity("dev-other", "OPPO Enco X4", new BluetoothAddress(1));
        h.Store.Reset(DeviceState.Empty with { Device = other });
        h.Device.PushNoiseEvent(8);
        h.Device.PushBatteryEvent(0x01, 0x10);
        await Task.Delay(100, Ct);

        Assert.Equal("dev-other", h.Store.Current.Device!.DeviceId);
        Assert.Null(h.Store.Current.NoiseControl);
        Assert.Null(h.Store.Current.Battery.Left);
    }

    [Fact]
    public async Task LeAudioBond_RejectsEnablingMultipointWithoutSendingIt()
    {
        await using var h = new Harness();
        Assert.True(await h.Controller.InitializeAsync(Ct));
        h.Store.TryUpdate(X4.DeviceId, s => s with { IsLeAudioBonded = true });
        int writes = h.Device.Received.Count(f => f.Command == OppoCommand.SetFeature);
        var result = await h.Controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, true, Ct);
        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Equal(writes, h.Device.Received.Count(f => f.Command == OppoCommand.SetFeature));
    }

    [Fact]
    public async Task DisableMultipoint_ConfirmsActualDeviceEvenWhenCachedStateSaysOff()
    {
        await using var h = new Harness();
        Assert.True(await h.Controller.InitializeAsync(Ct));
        h.Device.Features[0x11] = 1;
        h.Store.TryUpdate(X4.DeviceId, s => s with
        {
            IsLeAudioBonded = true,
            Toggles = s.Toggles.SetItem(DeviceToggle.DualDeviceConnection, Observed.From(false, DateTimeOffset.UtcNow, StateSource.CommandConfirmation)),
        });
        var result = await h.Controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, false, Ct);
        Assert.True(result.IsSuccess);
        Assert.Equal(0, h.Device.Features[0x11]);
        Assert.Equal(new byte[] { 0x11, 0x00 }, h.Device.Received.Last(f => f.Command == OppoCommand.SetFeature).Payload);
        Assert.False(h.Store.Current.Toggles[DeviceToggle.DualDeviceConnection].Value);
        Assert.Equal(StateSource.CommandConfirmation, h.Store.Current.Toggles[DeviceToggle.DualDeviceConnection].Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("0100")]
    public async Task LeBrSwitch_InvalidReadBackCannotConfirmOff(string hex)
    {
        await using var h = new Harness();
        Assert.True(await h.Controller.InitializeAsync(Ct));
        h.Device.LeBrSwitchResponse = Convert.FromHexString(hex);
        var result = await h.Controller.SetLeBrSwitchAsync(false, Ct);
        Assert.Equal(CommandStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task LeBrSwitch_UpdatesStateOnlyFromValidReadBack()
    {
        await using var h = new Harness();
        Assert.True(await h.Controller.InitializeAsync(Ct));
        Assert.True((await h.Controller.SetLeBrSwitchAsync(true, Ct)).IsSuccess);
        Assert.True(h.Store.Current.LeAudioSwitch!.Value.Enabled);
        Assert.Equal(StateSource.CommandConfirmation, h.Store.Current.LeAudioSwitch.Source);
    }

    [Fact]
    public async Task SetToggle_WriteAndReadBack()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        var result = await h.Controller.SetToggleAsync(DeviceToggle.InEarDetection, false, Ct);
        Assert.True(result.IsSuccess);
        Assert.False(h.Store.Current.Toggles[DeviceToggle.InEarDetection].Value);
        Assert.Equal(new byte[] { 0x04, 0x00 }, h.Device.Received.Last(f => f.Command == OppoCommand.SetFeature).Payload);
    }

    [Fact]
    public async Task SetEqualizer_WritesAndReadsBack()
    {
        await using var h = new Harness();
        await h.Controller.InitializeAsync(Ct);
        var result = await h.Controller.SetEqualizerPresetAsync("pure-vocals", Ct);
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x02 }, h.Device.Received.Last(f => f.Command == OppoCommand.SetEqualizer).Payload);
        Assert.Equal((byte)2, h.Store.Current.Equalizer!.Value.RawPresetValue);
    }

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(10);
        }
    }
}

/// <summary>Clock that only moves when told to (delays still use real time via the base provider).</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}
