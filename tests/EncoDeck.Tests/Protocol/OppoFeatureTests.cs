// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Tests.Fakes;

namespace EncoDeck.Tests.Protocol;

/// <summary>
/// Feature commands against the emulated X4: exact wire bytes from the official client, the
/// official re-read after every write, and the official preconditions and mutexes.
/// </summary>
public class OppoFeatureTests
{
    private static readonly DeviceIdentity X4 = new("dev-x4", "OPPO Enco X4", new BluetoothAddress(0x112233445566));
    private static readonly byte[] PcAddress = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
    private static readonly byte[] PhoneAddress = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];

    private sealed class Harness : IAsyncDisposable
    {
        public FakeOppoDevice Device { get; } = new();
        public DeviceStateStore Store { get; } = new();
        public OppoSession Session { get; }
        public OppoDeviceController Controller { get; }

        public Harness(OppoModelDefinition? model = null, TimeProvider? time = null)
        {
            Store.Reset(DeviceState.Empty with { Device = X4, Connection = DeviceConnectionState.ControlConnecting });
            Session = new OppoSession(Device.Channel);
            Session.Start();
            Controller = new OppoDeviceController(Session, new OppoDeviceProfile(model ?? OppoModels.EncoX4), X4, Store, time: time);
        }

        public DeviceState State => Store.Current;

        public byte[] LastPayload(ushort command) => Device.Received.Last(f => f.Command == command).Payload;

        public int Count(ushort command) => Device.Received.Count(f => f.Command == command);

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            await Session.DisposeAsync();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Harness> StartAsync(Action<FakeOppoDevice>? configure = null, TimeProvider? time = null)
    {
        var h = new Harness(time: time);
        configure?.Invoke(h.Device);
        Assert.True(await h.Controller.InitializeAsync(Ct));
        return h;
    }

    [Fact]
    public async Task Initialize_ReadsEveryFeature()
    {
        await using var h = await StartAsync();
        var s = h.State;
        Assert.Equal(SpatialAudioMode.Off, s.Spatial!.Value);
        Assert.Equal(0, s.GameSound!.Value.SelectedType);
        Assert.Equal([3, 0], s.GameSound.Value.SupportedTypes);
        Assert.Equal(6, s.PromptVolume!.Value);
        Assert.Equal(new BassEngineState(-3, 3, 0), s.BassEngine!.Value);
        Assert.Equal(16, s.KeyFunctions!.Value.Count);
        Assert.Equal(new NoiseCycleState(0x0006, 0x0006), s.NoiseCycle!.Value);
        Assert.Equal("自定义1", Assert.Single(s.CustomEqualizers!.Value).Name);
        Assert.Equal(0, s.HeadMotionType!.Value);
        Assert.Null(s.PreferredDevice!.Value.Address);
    }

    [Fact]
    public async Task SourceDevices_AreParsedInDisplayOrderWithFlags()
    {
        await using var h = await StartAsync();
        var devices = h.State.SourceDevices!.Value;
        var pc = devices.Single(d => d.Name == "PC");
        Assert.Equal(PcAddress, pc.Address);
        Assert.Equal("11:22:33:44:55:66", pc.AddressText);
        Assert.True(pc.IsThisDevice);
        Assert.True(pc.IsAudioActive);
        Assert.True(pc.IsConnected);
        Assert.Equal(2, pc.DeviceType);
        var phone = devices.Single(d => d.Name == "Phone");
        Assert.False(phone.IsThisDevice);
        Assert.False(phone.IsConnected);
        Assert.Equal(1, phone.DeviceType);
    }

    // ---- spatial audio -------------------------------------------------------------------------

    [Fact]
    public async Task SetSpatialAudio_WritesTypeAndReadsBack()
    {
        await using var h = await StartAsync();
        var result = await h.Controller.SetSpatialAudioAsync(SpatialAudioMode.HeadTracking, Ct);
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x02 }, h.LastPayload(OppoCommand.SetSpatialAudio));
        Assert.Equal(SpatialAudioMode.HeadTracking, h.State.Spatial!.Value);
        Assert.True(h.Count(OppoCommand.QuerySpatialType) >= 2);
    }

    [Fact]
    public async Task SetSpatialAudio_On_TurnsHiResOff()
    {
        await using var h = await StartAsync(d => d.Features[0x18] = 1);
        Assert.True(h.State.Toggles[DeviceToggle.HiRes].Value);
        var result = await h.Controller.SetSpatialAudioAsync(SpatialAudioMode.Fixed, Ct);
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x18, 0x00 }, h.LastPayload(OppoCommand.SetFeature));
        Assert.False(h.State.Toggles[DeviceToggle.HiRes].Value);
    }

    [Fact]
    public async Task SpatialMutexFailure_DoesNotSendTheSpatialWrite()
    {
        await using var h = await StartAsync();
        h.Device.SetStatus = 1;
        Assert.Equal(CommandStatus.Rejected, (await h.Controller.SetSpatialAudioAsync(SpatialAudioMode.Fixed, Ct)).Status);
        Assert.Equal(0, h.Count(OppoCommand.SetSpatialAudio));
    }

    [Fact]
    public async Task SetSpatialAudio_Off_LeavesHiResAlone()
    {
        await using var h = await StartAsync(d => { d.Features[0x18] = 1; d.SpatialType = 1; });
        Assert.True((await h.Controller.SetSpatialAudioAsync(SpatialAudioMode.Off, Ct)).IsSuccess);
        Assert.Equal(0, h.Count(OppoCommand.SetFeature));
    }

    // ---- game sound, prompt volume, bass, head motion -------------------------------------------

    [Fact]
    public async Task SetGameSound_SendsTypeWithEnableFlag()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.SetGameSoundAsync(3, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x03, 0x01 }, h.LastPayload(OppoCommand.SetGameSound));
        Assert.Equal(3, h.State.GameSound!.Value.SelectedType);

        Assert.True((await h.Controller.SetGameSoundAsync(0, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x00, 0x01 }, h.LastPayload(OppoCommand.SetGameSound));
    }

    [Fact]
    public async Task RefreshGameMode_ReadsChangesMadeElsewhere()
    {
        await using var h = await StartAsync();
        Assert.False(h.State.Toggles[DeviceToggle.GamingMode].Value);
        // Game mode on and enhanced game sound chosen on the phone, without events reaching us.
        h.Device.Features[0x28] = 1;
        h.Device.GameSoundType = 3;
        Assert.True((await h.Controller.RefreshGameModeAsync(Ct)).IsSuccess);
        Assert.True(h.State.Toggles[DeviceToggle.GamingMode].Value);
        Assert.Equal(3, h.State.GameSound!.Value.SelectedType);
        Assert.Equal(0, h.Count(OppoCommand.SetFeature) + h.Count(OppoCommand.SetGameSound));
    }

    [Fact]
    public async Task SetGameSound_RejectsTypeNotOffered()
    {
        await using var h = await StartAsync();
        var result = await h.Controller.SetGameSoundAsync(1, Ct);
        Assert.Equal(CommandStatus.Unsupported, result.Status);
        Assert.Equal(0, h.Count(OppoCommand.SetGameSound));
    }

    [Fact]
    public async Task SetPromptVolume_WritesAndReadsBack()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.SetPromptVolumeAsync(8, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x08 }, h.LastPayload(OppoCommand.SetPromptVolume));
        Assert.Equal(8, h.State.PromptVolume!.Value);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetPromptVolumeAsync(11, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetPromptVolumeAsync(0, Ct)).Status);
    }

    [Fact]
    public async Task SetBassEngine_EchoesRangeAndClamps()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.SetBassEngineLevelAsync(2, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0xFD, 0x03, 0x02 }, h.LastPayload(OppoCommand.SetBassEngine));
        Assert.Equal(2, h.State.BassEngine!.Value.Current);

        Assert.True((await h.Controller.SetBassEngineLevelAsync(9, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0xFD, 0x03, 0x03 }, h.LastPayload(OppoCommand.SetBassEngine));
    }

    [Fact]
    public async Task SetHeadMotionType_WritesAndReadsBack()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.SetHeadMotionTypeAsync(1, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x01 }, h.LastPayload(OppoCommand.SetHeadMotionType));
        Assert.Equal(1, h.State.HeadMotionType!.Value);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetHeadMotionTypeAsync(2, Ct)).Status);
    }

    [Fact]
    public async Task WearRequiredStatus_IsReportedAsSuch()
    {
        await using var h = await StartAsync();
        h.Device.SetStatus = 0x0E;
        var result = await h.Controller.SetPromptVolumeAsync(3, Ct);
        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Equal(OppoDeviceController.WearRequiredDetail, result.Detail);
        Assert.Equal(6, h.State.PromptVolume!.Value);
    }

    // ---- gestures and long-press cycle ---------------------------------------------------------

    [Fact]
    public async Task SetKeyFunction_PerEarbudRecord()
    {
        await using var h = await StartAsync();
        var result = await h.Controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Left, 1, GestureAction.DoubleSqueeze, 5), Ct);
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x01, 0x02, 0x05 }, h.LastPayload(OppoCommand.SetKeyFunction));
        Assert.Contains(h.State.KeyFunctions!.Value, k => k is { Side: EarSide.Left, Action: GestureAction.DoubleSqueeze, Function: 5 });
        // The right earbud is untouched.
        Assert.Contains(h.State.KeyFunctions.Value, k => k is { Side: EarSide.Right, Action: GestureAction.DoubleSqueeze, Function: 6 });
    }

    [Fact]
    public async Task SetKeyFunction_CallRowUsesBothEarbudsAndButton6()
    {
        await using var h = await StartAsync();
        var result = await h.Controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Left, KeyFunction.CallButton, GestureAction.DoubleSqueeze, 0), Ct);
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x01, 0x04, 0x06, 0x02, 0x00 }, h.LastPayload(OppoCommand.SetKeyFunction));
        // The X4 reports call rows per earbud; both now show the new function.
        Assert.All(h.State.KeyFunctions!.Value.Where(k => k is { Button: KeyFunction.CallButton, Action: GestureAction.DoubleSqueeze }), k => Assert.Equal(0, k.Function));
    }

    [Fact]
    public async Task SetKeyFunction_RejectsFunctionNotOffered()
    {
        await using var h = await StartAsync();
        var result = await h.Controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Left, 1, GestureAction.Slide, 1), Ct);
        Assert.Equal(CommandStatus.Unsupported, result.Status);
        Assert.Equal(0, h.Count(OppoCommand.SetKeyFunction));
    }

    [Fact]
    public async Task SetNoiseCycle_PerEarbudMinimalMask()
    {
        await using var h = await StartAsync();
        // Left: noise cancelling + adaptive + transparency (official example 02 03 06 08).
        var result = await h.Controller.SetNoiseCycleAsync(EarSide.Left, 0x0806, Ct);
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x02, 0x03, 0x06, 0x08 }, h.LastPayload(OppoCommand.SetNoiseControl));
        Assert.Equal(new NoiseCycleState(0x0806, 0x0006), h.State.NoiseCycle!.Value);
    }

    [Fact]
    public async Task SetNoiseCycle_NeedsAtLeastTwoModes()
    {
        await using var h = await StartAsync();
        int before = h.Count(OppoCommand.SetNoiseControl);
        var result = await h.Controller.SetNoiseCycleAsync(EarSide.Right, 0x0002, Ct);
        Assert.Equal(new CommandResult(CommandStatus.Rejected, "at-least-two"), result);
        Assert.Equal(before, h.Count(OppoCommand.SetNoiseControl));
    }

    // ---- find earbuds and fit test -------------------------------------------------------------

    [Fact]
    public async Task FindEarbuds_StartAndStop()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.FindEarbudsAsync(true, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x01 }, h.LastPayload(OppoCommand.FindEarbuds));
        Assert.True(h.State.IsFindingEarbuds);

        Assert.True((await h.Controller.FindEarbudsAsync(false, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x00 }, h.LastPayload(OppoCommand.FindEarbuds));
        Assert.False(h.State.IsFindingEarbuds);
    }

    [Fact]
    public async Task FindEarbuds_StopsWhenBothGoBackIntoTheCase()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.FindEarbudsAsync(true, Ct)).IsSuccess);
        h.Device.PushWearEvent(0x04, 0x04, 0x04);
        await OppoDeviceControllerTests.WaitUntil(() => !h.Store.Current.IsFindingEarbuds);
        Assert.Equal(new byte[] { 0x00 }, h.LastPayload(OppoCommand.FindEarbuds));
    }

    [Fact]
    public async Task FitTest_ReportsBothSides()
    {
        await using var h = await StartAsync(d => d.FitResult = (6, 0));
        Assert.True((await h.Controller.RunFitTestAsync(Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x01 }, h.LastPayload(OppoCommand.FitTest));
        Assert.Equal(new FitTestResult(FitQuality.Poor, FitQuality.Average), h.State.FitTest);
        Assert.False(h.State.IsFitTestRunning);
    }

    [Fact]
    public async Task FitTest_NeedsBothEarbudsWorn()
    {
        await using var h = await StartAsync(d => d.Wear = [0x07, 0x04, 0x04]);
        Assert.NotEqual(true, h.State.Wear!.Value.RightInEar);
        var result = await h.Controller.RunFitTestAsync(Ct);
        Assert.Equal(new CommandResult(CommandStatus.Rejected, OppoDeviceController.WearRequiredDetail), result);
        Assert.Equal(0, h.Count(OppoCommand.FitTest));
    }

    // ---- dual connection -----------------------------------------------------------------------

    [Fact]
    public async Task MultiConnect_ConnectUsesDisplayOrderAddress()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.MultiConnectAsync(MultiConnectOperation.Connect, PhoneAddress, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, h.LastPayload(OppoCommand.OperateMultiConnect));
        Assert.True(h.State.SourceDevices!.Value.Single(d => d.Name == "Phone").IsConnected);
    }

    [Fact]
    public async Task MultiConnect_ConnectWaitsWhileTheEarbudsPageThePhone()
    {
        // Seen on an Enco X4: the phone connected about 4 s after the acknowledgement, past the usual settle reads.
        await using var h = await StartAsync(d => d.ConnectSettleQueries = 5, new FastTimeProvider(20));
        Assert.True((await h.Controller.MultiConnectAsync(MultiConnectOperation.Connect, PhoneAddress, Ct)).IsSuccess);
        Assert.True(h.State.SourceDevices!.Value.Single(d => d.Name == "Phone").IsConnected);
        Assert.False(h.State.IsPending("multi-connect"));
    }

    [Fact]
    public async Task MultiConnect_AcknowledgementWithoutStateChangeIsNotSuccess()
    {
        await using var h = await StartAsync(time: new FastTimeProvider(20));
        h.Device.ApplySets = false;
        var result = await h.Controller.MultiConnectAsync(MultiConnectOperation.Connect, PhoneAddress, Ct);
        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.False(h.State.SourceDevices!.Value.Single(d => d.Name == "Phone").IsConnected);
        Assert.False(h.State.IsPending("multi-connect"));
    }

    [Fact]
    public async Task MultiConnect_RequiresManagementCapabilitySeparateFromMultipoint()
    {
        await using var h = await StartAsync(d => d.Capabilities[7] &= 0xF7); // bit 59
        var model = Assert.IsType<OppoDeviceProfile>(h.State.Profile).Definition;
        Assert.False(model.SupportsMultiConnectOperations);
        Assert.Contains(model.FeatureSwitches, f => f.Toggle == DeviceToggle.DualDeviceConnection);
        Assert.NotEmpty(h.State.SourceDevices!.Value);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.MultiConnectAsync(MultiConnectOperation.Connect, PhoneAddress, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetPreferredDeviceAsync(PcAddress, Ct)).Status);
        Assert.Equal(0, h.Count(OppoCommand.OperateMultiConnect));
    }

    [Fact]
    public async Task MultiConnect_RejectsUnknownDeviceAndInvalidPreferredAddress()
    {
        await using var h = await StartAsync();
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.MultiConnectAsync(MultiConnectOperation.Connect, new byte[6], Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetPreferredDeviceAsync(new byte[5], Ct)).Status);
        Assert.Equal(0, h.Count(OppoCommand.OperateMultiConnect));
    }

    [Fact]
    public async Task MultiConnect_LastRemovedDeviceClearsTheList()
    {
        await using var h = await StartAsync(d => d.Sources.RemoveAt(0));
        Assert.True((await h.Controller.MultiConnectAsync(MultiConnectOperation.Remove, PhoneAddress, Ct)).IsSuccess);
        Assert.Empty(h.State.SourceDevices!.Value);
        Assert.False(h.State.IsPending("multi-connect"));
    }

    [Fact]
    public async Task MultiConnect_ThisDeviceIsNotDisconnectedThroughTheEarbuds()
    {
        await using var h = await StartAsync();
        var result = await h.Controller.MultiConnectAsync(MultiConnectOperation.Disconnect, PcAddress, Ct);
        Assert.Equal(CommandStatus.Unsupported, result.Status);
        Assert.Equal(0, h.Count(OppoCommand.OperateMultiConnect));
    }

    [Fact]
    public async Task MultiConnect_OnlyUnconnectedDevicesCanBeRemoved()
    {
        await using var h = await StartAsync();
        Assert.Equal(new CommandResult(CommandStatus.Rejected, "remove-connected"), await h.Controller.MultiConnectAsync(MultiConnectOperation.Remove, PcAddress, Ct));

        Assert.True((await h.Controller.MultiConnectAsync(MultiConnectOperation.Remove, PhoneAddress, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x03, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, h.LastPayload(OppoCommand.OperateMultiConnect));
        Assert.DoesNotContain(h.State.SourceDevices!.Value, d => d.Name == "Phone");
    }

    [Fact]
    public async Task SetPreferredDevice_AndBackToAuto()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.SetPreferredDeviceAsync(PcAddress, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x04, 0x01, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 }, h.LastPayload(OppoCommand.OperateMultiConnect));
        Assert.Equal(PcAddress, h.State.PreferredDevice!.Value.Address);

        Assert.True((await h.Controller.SetPreferredDeviceAsync(null, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x04, 0x00 }, h.LastPayload(OppoCommand.OperateMultiConnect));
        Assert.Null(h.State.PreferredDevice!.Value.Address);
    }

    // ---- custom EQ -----------------------------------------------------------------------------

    [Fact]
    public void EncodeCustomEqualizer_MatchesOfficialAddRecord()
    {
        var spec = OppoModels.EncoX4.CustomEqualizer!;
        var preset = new CustomEqualizer(0, "自定义1", spec.MinGain, spec.MaxGain, false, spec.BandFrequencies.Select(f => new EqualizerBand(f, 0)).ToArray());
        var expected = Convert.FromHexString("01FA06000AE887AAE5AE9AE4B989310A1F00003E00007D0000FA0000F40100E80300D00700A00F00401F00803E00");
        Assert.Equal(expected, OppoFeatureParsers.EncodeCustomEqualizer(0x01, preset));
    }

    [Fact]
    public async Task CustomEqualizer_AddUsesNextDefaultNameAndSelectsIt()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.AddCustomEqualizerAsync(Ct)).IsSuccess);
        var presets = h.State.CustomEqualizers!.Value;
        Assert.Equal(2, presets.Count);
        var added = presets.Single(p => p.IsSelected);
        Assert.Equal("自定义2", added.Name);
        Assert.Equal(0x01, h.LastPayload(OppoCommand.SetCustomEqualizer)[0]);
        Assert.Equal(0x00, h.LastPayload(OppoCommand.SetCustomEqualizer)[3]);
    }

    [Fact]
    public async Task CustomEqualizer_AtMostThreePresets()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.AddCustomEqualizerAsync(Ct)).IsSuccess);
        Assert.True((await h.Controller.AddCustomEqualizerAsync(Ct)).IsSuccess);
        int sent = h.Count(OppoCommand.SetCustomEqualizer);
        Assert.Equal(new CommandResult(CommandStatus.Rejected, "max-presets"), await h.Controller.AddCustomEqualizerAsync(Ct));
        Assert.Equal(sent, h.Count(OppoCommand.SetCustomEqualizer));
    }

    [Fact]
    public async Task CustomEqualizer_SelectSendsFullRecordWithAction2()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.SelectCustomEqualizerAsync(4, Ct)).IsSuccess);
        var payload = h.LastPayload(OppoCommand.SetCustomEqualizer);
        Assert.Equal(new byte[] { 0x02, 0xFA, 0x06, 0x04 }, payload[..4]);
        Assert.True(h.State.CustomEqualizers!.Value.Single().IsSelected);
        Assert.Equal(0, h.Count(OppoCommand.SetEqualizer));
    }

    [Fact]
    public async Task CustomEqualizer_BuiltInPresetDeselectsItSoItCanBeSelectedAgain()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.SelectCustomEqualizerAsync(4, Ct)).IsSuccess);
        string builtIn = OppoModels.EncoX4.EqualizerPresets.Values.First().Id;
        Assert.True((await h.Controller.SetEqualizerPresetAsync(builtIn, Ct)).IsSuccess);
        Assert.False(h.State.CustomEqualizers!.Value.Single().IsSelected);
        Assert.True((await h.Controller.SelectCustomEqualizerAsync(4, Ct)).IsSuccess);
        Assert.True(h.State.CustomEqualizers!.Value.Single().IsSelected);
    }

    [Fact]
    public async Task CustomEqualizer_RejectedBuiltInPresetKeepsCustomSelected()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.SelectCustomEqualizerAsync(4, Ct)).IsSuccess);
        var equalizerBefore = h.State.Equalizer;
        h.Device.SetStatus = 0x02;

        string builtIn = OppoModels.EncoX4.EqualizerPresets.Values.First().Id;
        var result = await h.Controller.SetEqualizerPresetAsync(builtIn, Ct);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        var state = h.State;
        Assert.True(state.CustomEqualizers!.Value.Single().IsSelected);
        Assert.NotEqual(StateSource.Optimistic, state.CustomEqualizers.Source);
        Assert.Equal(equalizerBefore, state.Equalizer);
        Assert.Empty(state.PendingCommands);
        Assert.Equal(CommandStatus.Rejected, state.LastError!.Status);
    }

    [Fact]
    public async Task CustomEqualizer_SaveClampsGainsAndRenames()
    {
        await using var h = await StartAsync();
        var preset = h.State.CustomEqualizers!.Value.Single();
        var edited = preset with { Name = "  Bass  ", Bands = preset.Bands.Select((b, i) => b with { Gain = i == 0 ? 9 : -2 }).ToArray() };
        Assert.True((await h.Controller.SaveCustomEqualizerAsync(edited, Ct)).IsSuccess);
        var stored = h.State.CustomEqualizers!.Value.Single();
        Assert.Equal("Bass", stored.Name);
        Assert.Equal(6, stored.Bands[0].Gain);
        Assert.Equal(-2, stored.Bands[1].Gain);
    }

    [Fact]
    public async Task CustomEqualizer_RejectsEmptyName()
    {
        await using var h = await StartAsync();
        var preset = h.State.CustomEqualizers!.Value.Single();
        Assert.Equal(new CommandResult(CommandStatus.Rejected, "invalid-name"), await h.Controller.SaveCustomEqualizerAsync(preset with { Name = "   " }, Ct));
    }

    [Fact]
    public async Task CustomEqualizer_DeleteSendsFullRecordWithAction3()
    {
        await using var h = await StartAsync();
        Assert.True((await h.Controller.DeleteCustomEqualizerAsync(4, Ct)).IsSuccess);
        Assert.Equal(0x03, h.LastPayload(OppoCommand.SetCustomEqualizer)[0]);
        Assert.Empty(h.State.CustomEqualizers!.Value);
    }

    // ---- generic fallback ----------------------------------------------------------------------

    [Fact]
    public async Task GenericModel_OffersNoFeatureWrites()
    {
        await using var h = new Harness(OppoModels.GenericOppo);
        h.Device.ProductId = 0x123456; // an OPPO model EncoDeck has no definition for
        await h.Controller.InitializeAsync(Ct);
        Assert.Equal("oppo-generic", h.Controller.Profile.ModelId);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetSpatialAudioAsync(SpatialAudioMode.Fixed, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.FindEarbudsAsync(true, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.RunFitTestAsync(Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetPromptVolumeAsync(5, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.MultiConnectAsync(MultiConnectOperation.Connect, PhoneAddress, Ct)).Status);
        Assert.DoesNotContain(h.Device.Received, f => f.Command is OppoCommand.SetSpatialAudio or OppoCommand.FindEarbuds or OppoCommand.FitTest);
    }
}

/// <summary>The system clock with timers <paramref name="factor"/> times faster: long settle waits without slow tests.</summary>
internal sealed class FastTimeProvider(int factor) : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, Scale(dueTime), Scale(period));

    private TimeSpan Scale(TimeSpan span) => span <= TimeSpan.Zero ? span : span / factor;
}
