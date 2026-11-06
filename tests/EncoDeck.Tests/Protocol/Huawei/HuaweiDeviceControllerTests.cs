// SPDX-License-Identifier: GPL-3.0-only
// Byte fixtures are real-device captures published by community projects: OpenFreebuds and its
// vicrodh fork (write acknowledgements, FreeBuds 7i wear state), HuaweiPods (FreeBuds 7 EQ state),
// EZBuds (FreeClip 2 battery and device-list layout) and OpenFreebuds-Android (FreeBuds 5i handshake).
// The FreeBuds 7i spatial-audio state report is from the OpenFreebuds vicrodh fork.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Huawei;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol.Huawei;

public class HuaweiDeviceControllerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (IDeviceController Controller, DeviceStateStore Store) Create(FakeHuaweiDevice device, string name = "HUAWEI FreeBuds Pro 3")
    {
        var identity = new DeviceIdentity("dev-huawei", name, new BluetoothAddress(0x112233445566));
        var profile = HuaweiProfiles.CreateBuiltIn().Cast<HuaweiDeviceProfile>().Single(p => p.MatchScore(identity) == 100);
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile, Connection = DeviceConnectionState.ControlConnecting });
        var context = new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        return (profile.CreateController(device, context), store);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(20, Ct);
        Assert.True(condition());
    }

    [Fact]
    public async Task IndependentSpatial_RequiresProbeAndConfirmsWrites()
    {
        var device = new FakeHuaweiDevice { SpatialAvailable = true };
        var (controller, store) = Create(device, "HUAWEI FreeClip 2"); await using var session = controller;
        Assert.False(store.Current.Profile!.Capabilities.IsAvailable(DeviceCapability.SpatialAudio, false));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(SpatialAudioMode.Off, store.Current.Spatial!.Value);
        var profile = (HuaweiDeviceProfile)store.Current.Profile!;
        Assert.True(profile.SpatialConfirmed);
        Assert.True(profile.Capabilities.IsAvailable(DeviceCapability.SpatialAudio, false));
        Assert.True(profile.Capabilities.IsAvailable(DeviceCapability.HeadTracking, false));
        Assert.Equal([SpatialAudioMode.Off, SpatialAudioMode.Fixed, SpatialAudioMode.HeadTracking], profile.SpatialModes);
        Assert.True((await controller.SetSpatialAudioAsync(SpatialAudioMode.HeadTracking, Ct)).IsSuccess);
        Assert.Equal(1, device.SpatialMode);
        Assert.Equal(StateSource.CommandConfirmation, store.Current.Spatial!.Source);
        device.IgnoreSpatialWrites = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetSpatialAudioAsync(SpatialAudioMode.Fixed, Ct)).Status);
        Assert.Equal(SpatialAudioMode.HeadTracking, store.Current.Spatial!.Value);
        Assert.NotNull(store.Current.LastError);
        // Only the mode that did not read back is hidden; the others stay usable.
        Assert.Equal([SpatialAudioMode.Off, SpatialAudioMode.HeadTracking], ((HuaweiDeviceProfile)store.Current.Profile!).SpatialModes);
        device.IgnoreSpatialWrites = false;
        Assert.True((await controller.SetSpatialAudioAsync(SpatialAudioMode.Off, Ct)).IsSuccess);
        Assert.Equal(SpatialAudioMode.Off, store.Current.Spatial!.Value);
        Assert.Contains(device.Received, f => f.Command == 0x2BB4 && f.Payload.SequenceEqual(new byte[] { 1, 1, 24, 2, 0, 3, 0 }));
    }

    [Theory]
    [InlineData(SpatialAudioMode.Fixed, 2)]
    [InlineData(SpatialAudioMode.HeadTracking, 1)]
    [InlineData(SpatialAudioMode.Off, 0)]
    public async Task FreeClip2Spatial_EveryModeIsWrittenAndReadBack(SpatialAudioMode mode, byte wire)
    {
        var device = new FakeHuaweiDevice { SpatialAvailable = true, SpatialMode = wire == 0 ? (byte)1 : (byte)0 };
        var (controller, store) = Create(device, "HUAWEI FreeClip 2"); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.True((await controller.SetSpatialAudioAsync(mode, Ct)).IsSuccess);
        Assert.Equal(wire, device.SpatialMode);
        Assert.Equal(mode, store.Current.Spatial!.Value);
        Assert.Equal(StateSource.CommandConfirmation, store.Current.Spatial!.Source);
        Assert.Contains(device.Received, f => f.Command == 0x2BB4 && f.Payload.SequenceEqual(new byte[] { 1, 1, 24, 2, 1, wire }));
    }

    [Theory]
    [InlineData("HUAWEI FreeBuds Pro 3")]
    [InlineData("HUAWEI FreeBuds Pro 4")]
    [InlineData("HUAWEI FreeBuds 6")]
    public async Task UnrecordedModel_OpensAllModesOnceTheEarbudsReadBack(string name)
    {
        var device = new FakeHuaweiDevice { SpatialAvailable = true };
        var (controller, store) = Create(device, name); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        var profile = (HuaweiDeviceProfile)store.Current.Profile!;
        Assert.True(profile.SpatialConfirmed);
        Assert.Empty(profile.Model.SpatialModes);
        Assert.Equal([SpatialAudioMode.Off, SpatialAudioMode.Fixed, SpatialAudioMode.HeadTracking], profile.SpatialModes);
        Assert.True(profile.Capabilities.IsAvailable(DeviceCapability.SpatialAudio, false));
        Assert.True((await controller.SetSpatialAudioAsync(SpatialAudioMode.Fixed, Ct)).IsSuccess);
        Assert.Equal(2, device.SpatialMode);
    }

    [Fact]
    public async Task SpatialWithoutReadBack_StaysClosed()
    {
        var device = new FakeHuaweiDevice { SpatialAvailable = false };
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Contains(device.Received, f => f.Command == 0x2BB4 && f.Payload.SequenceEqual(new byte[] { 1, 1, 24, 2, 0, 3, 0 }));
        var profile = (HuaweiDeviceProfile)store.Current.Profile!;
        Assert.False(profile.SpatialConfirmed);
        Assert.Empty(profile.SpatialModes);
        Assert.Null(store.Current.Spatial);
        Assert.False(profile.Capabilities.IsAvailable(DeviceCapability.SpatialAudio, false));
        Assert.False(SpatialAudioOptions.For(profile, c => profile.Capabilities.IsAvailable(c, false)).IsAvailable);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetSpatialAudioAsync(SpatialAudioMode.HeadTracking, Ct)).Status);
        Assert.DoesNotContain(device.Received, f => f.Command == 0x2BB4 && f.Payload.SequenceEqual(new byte[] { 1, 1, 24, 2, 1, 1 }));
    }

    [Fact]
    public async Task RefusedSpatialMode_IsHiddenAloneAndTheOthersStayUsable()
    {
        var device = new FakeHuaweiDevice { SpatialAvailable = true };
        device.RefusedSpatialValues.Add(1); // head tracking
        var (controller, store) = Create(device, "HUAWEI FreeBuds Pro 3"); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(CommandStatus.Rejected, (await controller.SetSpatialAudioAsync(SpatialAudioMode.HeadTracking, Ct)).Status);
        var profile = (HuaweiDeviceProfile)store.Current.Profile!;
        Assert.Equal([SpatialAudioMode.Off, SpatialAudioMode.Fixed], profile.SpatialModes);
        Assert.False(profile.Capabilities.IsAvailable(DeviceCapability.HeadTracking, false));
        Assert.True(profile.Capabilities.IsAvailable(DeviceCapability.SpatialAudio, false));
        Assert.Equal([SpatialAudioMode.Off, SpatialAudioMode.Fixed], SpatialAudioOptions.For(profile, c => profile.Capabilities.IsAvailable(c, false)).Modes);
        Assert.Equal(SpatialAudioMode.Off, store.Current.Spatial!.Value);
        Assert.True((await controller.SetSpatialAudioAsync(SpatialAudioMode.Fixed, Ct)).IsSuccess);
        Assert.Equal(SpatialAudioMode.Fixed, store.Current.Spatial!.Value);
        Assert.Equal([SpatialAudioMode.Off, SpatialAudioMode.Fixed], ((HuaweiDeviceProfile)store.Current.Profile!).SpatialModes);
    }

    [Fact]
    public void FreeBuds7iSpatialCapture_IsHeadTracking()
    {
        var frame = Assert.Single(new LegacyHuaweiFrameCodec().Feed(Convert.FromHexString("5A000C002BB4010118020101030100A19E")));
        Assert.Equal(HuaweiCommands.FeatureState, frame.Command);
        Assert.Equal(SpatialAudioMode.HeadTracking, HuaweiDeviceController.ParseSpatial(frame.Payload));
    }

    [Theory]
    [InlineData("010118020101", SpatialAudioMode.HeadTracking)]
    [InlineData("010118020102030100", SpatialAudioMode.Fixed)]
    [InlineData("010118020100", SpatialAudioMode.Off)]
    [InlineData("010117020101", null)]
    [InlineData("010118020103", null)]
    [InlineData("0101180201010302", null)]
    [InlineData("010118020101020100", null)]
    [InlineData("0101180201017F0101", null)]
    public void SpatialParser_RejectsOtherFeaturesAndMalformedTlv(string hex, SpatialAudioMode? expected) =>
        Assert.Equal(expected, HuaweiDeviceController.ParseSpatial(Convert.FromHexString(hex)));

    [Fact]
    public async Task ErrorResponses_DoNotProveCompatibleDevice()
    {
        var device = new FakeHuaweiDevice { ErrorOnly = true };
        var (controller, store) = Create(device); await using var session = controller;
        Assert.False(await controller.InitializeAsync(Ct));
        Assert.Null(store.Current.NoiseControl);
        Assert.Null(store.Current.Battery.Left);
    }

    [Theory]
    [InlineData("0101010302")]
    [InlineData("010101010100")]
    [InlineData("01")]
    public void ControlTlv_RejectsTruncationAndDuplicateFields(string hex) =>
        Assert.False(HuaweiWire.TryParseTlv(Convert.FromHexString(hex), out _));

    [Fact]
    public async Task Initialize_ReadsBatteryAndNoiseIntoStore()
    {
        // FreeBuds Pro 3 levels follow HuaweiPods' capture: 3 = ultra.
        var device = new FakeHuaweiDevice { AncState = [0x03, 0x01] };
        var (controller, store) = Create(device);
        await using var _ = controller;

        Assert.True(await controller.InitializeAsync(Ct));

        var s = store.Current;
        Assert.Equal(new BatteryReading(80, true), s.Battery.Left!.Value);
        Assert.Equal(new BatteryReading(70, false), s.Battery.Right!.Value);
        Assert.Equal(new BatteryReading(32, true), s.Battery.Case!.Value);
        Assert.Equal(StateSource.QueryResponse, s.Battery.Left.Source);
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "ultra"), s.NoiseControl!.Value);
        Assert.NotNull(s.LastResponseAt);
        Assert.Equal("5A0009000108010002000300FBB9", Convert.ToHexString(device.Inner.Written[0]));
    }

    [Fact]
    public async Task Initialize_FailsWhenDeviceIsSilent()
    {
        var device = new FakeHuaweiDevice { Silent = true };
        var (controller, _) = Create(device);
        await using var _c = controller;
        Assert.False(await controller.InitializeAsync(Ct));
    }

    [Fact]
    public async Task SetNoiseControl_IsConfirmedByReadBack()
    {
        var device = new FakeHuaweiDevice();
        var (controller, store) = Create(device);
        await using var _ = controller;
        await controller.InitializeAsync(Ct);

        var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation, "dynamic"), Ct);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Contains(device.Received, f => f.Command == HuaweiCommands.SetAnc && f.Payload.SequenceEqual(new byte[] { 0x01, 0x02, 0x01, 0x01 }));
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "dynamic"), store.Current.NoiseControl!.Value);
        Assert.True(store.Current.NoiseControl.IsConfirmed);
        Assert.Empty(store.Current.PendingCommands);
    }

    [Fact]
    public async Task SetNoiseControl_RollsBackWhenDeviceKeepsOldValue()
    {
        var device = new FakeHuaweiDevice { IgnoreAncWrites = true };
        var (controller, store) = Create(device);
        await using var _ = controller;
        await controller.InitializeAsync(Ct);

        var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Equal(new NoiseControlState(NoiseControlMode.Off), store.Current.NoiseControl!.Value);
        Assert.Empty(store.Current.PendingCommands);
    }

    [Fact]
    public async Task UnsupportedFeature_IsReportedAsUnsupported()
    {
        var device = new FakeHuaweiDevice();
        var (controller, _) = Create(device);
        await using var _c = controller;
        await controller.InitializeAsync(Ct);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.SpatialAudio, true, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.AutoVolume, true, Ct)).Status);
    }

    [Fact]
    public async Task WriteAcknowledgement_IsNotTakenAsRejectionOrReadBack()
    {
        // Real firmware answers writes with 7F 04 000186A0 (result 100000) before the state is read back.
        var device = new FakeHuaweiDevice();
        var (controller, store) = Create(device);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        Assert.True((await controller.SetToggleAsync(DeviceToggle.InEarDetection, false, Ct)).IsSuccess);
        Assert.False(store.Current.Toggles[DeviceToggle.InEarDetection].Value);
        // Low latency shares one command for write and read: the acknowledgement must not end the read.
        Assert.True((await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct)).IsSuccess);
        Assert.True(store.Current.Toggles[DeviceToggle.GamingMode].Value);
        Assert.True((await controller.SetEqualizerPresetAsync("treble", Ct)).IsSuccess);
        Assert.Equal(2, store.Current.Equalizer!.Value.RawPresetValue);
        Assert.Null(store.Current.LastError);
    }

    [Fact]
    public async Task ErrorResultCode_RejectsAndRollsBack()
    {
        var device = new FakeHuaweiDevice();
        var (controller, store) = Create(device);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.RejectWrites = true;

        var result = await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, false, Ct);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Contains("000186A3", result.Detail);
        Assert.True(store.Current.Toggles[DeviceToggle.DualDeviceConnection].Value);
    }

    [Fact]
    public async Task NoiseWrite_WithoutAckAndStaleReadBack_IsConfirmedOnSettle()
    {
        // OpenFreebuds #133: FreeBuds Pro 5 may apply 2B04 without acknowledging it, and the first
        // 2B2A read can still report the previous mode.
        var device = new FakeHuaweiDevice { AckAncWrites = false };
        var (controller, store) = Create(device, "HUAWEI FreeBuds Pro 5");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.StaleAncReads = 1;

        var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation, "ultra"), Ct);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "ultra"), store.Current.NoiseControl!.Value);
        int write = device.Received.FindIndex(f => f.Command == HuaweiCommands.SetAnc);
        Assert.True(device.Received.Skip(write).Count(f => f.Command == HuaweiCommands.QueryAnc) >= 2);
    }

    [Fact]
    public async Task ModeButton_KeepsTheCurrentLevel()
    {
        var device = new FakeHuaweiDevice();
        var (controller, store) = Create(device, "HUAWEI FreeBuds Pro 5");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation), Ct);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Contains(device.Received, f => f.Command == HuaweiCommands.SetAnc && f.Payload.SequenceEqual(new byte[] { 0x01, 0x02, 0x01, 0xFF }));
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "dynamic"), store.Current.NoiseControl!.Value);
    }

    [Fact]
    public async Task VoiceBoost_IsATransparencyLevel()
    {
        var device = new FakeHuaweiDevice();
        var (controller, store) = Create(device, "HUAWEI FreeBuds Pro 5");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        var levels = store.Current.Profile!.NoiseControlOptions.Where(o => o.Mode == NoiseControlMode.Transparency).Select(o => o.LevelId);
        Assert.Equal(["normal", "voice"], levels);

        Assert.True((await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency, "voice"), Ct)).IsSuccess);
        Assert.Contains(device.Received, f => f.Command == HuaweiCommands.SetAnc && f.Payload.SequenceEqual(new byte[] { 0x01, 0x02, 0x02, 0x01 }));
        Assert.Equal(new NoiseControlState(NoiseControlMode.Transparency, "voice"), store.Current.NoiseControl!.Value);

        // Smart dynamic transparency (sub-mode 4) is kept as transparency with an unknown level.
        device.Reply(HuaweiCommands.QueryAnc, [0x01, 0x02, 0x04, 0x02]);
        await Until(() => store.Current.NoiseControl!.Value == new NoiseControlState(NoiseControlMode.Transparency));
    }

    [Fact]
    public async Task BigDataCapabilityRequest_IsAnswered()
    {
        var device = new FakeHuaweiDevice();
        var (controller, _) = Create(device, "HUAWEI FreeBuds 5i");
        await using var _c = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        device.Reply(HuaweiCommands.BigDataCapability, [0x01, 0x01, 0x00]);

        // OpenFreebuds-Android test vector for the FreeBuds 5i reply.
        await Until(() => device.Inner.Written.Any(w => Convert.ToHexString(w) == "5A0006000A0E020100FB3F"));
    }

    [Fact]
    public async Task WearState_ReadsPerEarbudFlags()
    {
        var device = new FakeHuaweiDevice { WearState = (1, 1) };
        var (controller, store) = Create(device, "HUAWEI FreeBuds 7i");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(new WearStatus(true, true, null, null, null), store.Current.Wear!.Value);

        // FreeBuds 7i pushes (vicrodh/OpenFreebuds test_buds7i.py): right earbud out, then both out.
        device.Inner.Receive(Convert.FromHexString("5a000f002b25010101020100030100040100597a"));
        await Until(() => store.Current.Wear!.Value == new WearStatus(true, false, null, null, null));
        device.Inner.Receive(Convert.FromHexString("5a000f002b25010100020100030100040100363f"));
        await Until(() => store.Current.Wear!.Value == new WearStatus(false, false, null, null, null));
    }

    [Fact]
    public async Task Battery_ZeroInThePartListIsNotReported()
    {
        var device = new FakeHuaweiDevice { BatteryReply = [0x02, 0x03, 0x50, 0x00, 0x32, 0x03, 0x03, 0x00, 0x00, 0x00] };
        var (controller, store) = Create(device);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(80, store.Current.Battery.Left!.Value.Level);
        Assert.Null(store.Current.Battery.Right);
        Assert.Equal(50, store.Current.Battery.Case!.Value.Level);
    }

    [Fact]
    public async Task FreeClip2BatteryCapture_IsDecoded()
    {
        var device = new FakeHuaweiDevice();
        var (controller, store) = Create(device, "HUAWEI FreeClip 2");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        // EZBuds PROTOCOL.md, FreeClip 2 on Windows: overall 96 %, left 99 %, right 96 %, case 69 %, none charging.
        device.Inner.Receive(Convert.FromHexString("5a001b000108010160020363604503030000000402140a0502000006010acbfb"));

        await Until(() => store.Current.Battery.Case?.Value.Level == 69);
        Assert.Equal(new BatteryReading(99, false), store.Current.Battery.Left!.Value);
        Assert.Equal(new BatteryReading(96, false), store.Current.Battery.Right!.Value);
    }

    [Fact]
    public async Task DeviceList_IsReadAsSourceDevices()
    {
        // Layout of EZBuds' FreeClip 2 capture; the address bytes are replaced.
        static byte[] Row(byte index, byte[] reversed, byte state, byte preferred, string name) =>
        [
            2, 1, 2, 3, 1, index, 4, 6, .. reversed, 5, 1, state, 6, 1, 4, 7, 1, preferred, 8, 1, 1,
            9, (byte)System.Text.Encoding.UTF8.GetByteCount(name), .. System.Text.Encoding.UTF8.GetBytes(name), 10, 1, 1, 11, 1, 0, 12, 1, 0, 13, 1, 0,
        ];
        var device = new FakeHuaweiDevice();
        device.Sources.Add(Row(0, [0x56, 0x34, 0x12, 0x17, 0x60, 0xE4], 1, 0, "DESKTOP-TEST"));
        device.Sources.Add(Row(1, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06], 9, 1, "Phone"));
        var (controller, store) = Create(device, "HUAWEI FreeClip 2");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        await Until(() => store.Current.SourceDevices is not null);

        var sources = store.Current.SourceDevices!.Value;
        Assert.Equal(["DESKTOP-TEST", "Phone"], sources.Select(d => d.Name));
        Assert.Equal("E4:60:17:12:34:56", sources[0].AddressText);
        Assert.True(sources[0].IsConnected);
        Assert.False(sources[0].IsAudioActive);
        Assert.True(sources[1].IsAudioActive);
        Assert.Equal(new byte[] { 6, 5, 4, 3, 2, 1 }, store.Current.PreferredDevice!.Value.Address);
        Assert.Equal(CommandStatus.Unsupported, (await controller.MultiConnectAsync(MultiConnectOperation.Disconnect, sources[1].Address, Ct)).Status);
    }

    [Fact]
    public async Task Equalizer_ShowsOnlyPresetsTheEarbudsList()
    {
        var device = new FakeHuaweiDevice { Eq = 1, EqOffered = [0x01, 0x0A, 0x03, 0x09] };
        var (controller, store) = Create(device, "HUAWEI FreeClip 2");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        Assert.Equal(["default", "treble", "voices", "sport"], store.Current.Profile!.EqualizerPresets.Select(p => p.Id));
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("bass", Ct)).Status);
        Assert.True((await controller.SetEqualizerPresetAsync("sport", Ct)).IsSuccess);
        Assert.Contains(device.Received, f => f.Command == HuaweiCommands.SetEqualizer && f.Payload.SequenceEqual(new byte[] { 1, 1, 0x0A }));

        // A custom preset id is kept as the raw value, not mapped to a built-in name.
        device.Reply(HuaweiCommands.QueryEqualizer, [0x01, 0x01, 0x01, 0x02, 0x01, 0x64, 0x03, 0x04, 0x01, 0x0A, 0x03, 0x09]);
        await Until(() => store.Current.Equalizer!.Value == new EqualizerState(null, 0x64));
    }

    [Fact]
    public async Task FreeBuds7EqualizerCapture_NarrowsTheSceneList()
    {
        var device = new FakeHuaweiDevice { Eq = 9 };
        var (controller, store) = Create(device, "HUAWEI FreeBuds 7");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(7, store.Current.Profile!.EqualizerPresets.Count);

        // HuaweiPods FreeBuds 7 capture: selected 5, offered 2/5/9/13/14/15/16.
        device.Inner.Receive(Convert.FromHexString("5A0017002B4A01010102010503070205090D0E0F1004010508003E7A"));

        await Until(() => store.Current.Equalizer!.Value == new EqualizerState("balanced", 5));
        Assert.Equal(["balanced", "voices", "bass", "movie", "podcast", "game", "sport"], store.Current.Profile!.EqualizerPresets.Select(p => p.Id));
    }

    [Fact]
    public async Task SmartFeatures_OpenAfterReportAndConfirmWrites()
    {
        var device = new FakeHuaweiDevice();
        device.Features[HuaweiCommands.FeatureAdaptiveVolume] = new() { [2] = 0 };
        device.Features[HuaweiCommands.FeatureHeadGestures] = new() { [2] = 1, [3] = 1, [4] = 2 };
        var (controller, store) = Create(device, "HUAWEI FreeBuds 7");
        await using var _ = controller;
        Assert.DoesNotContain(DeviceToggle.AutoVolume, ((IControllableProfile)store.Current.Profile!).Toggles);
        Assert.True(await controller.InitializeAsync(Ct));

        var toggles = ((IControllableProfile)store.Current.Profile!).Toggles;
        Assert.Contains(DeviceToggle.AutoVolume, toggles);
        Assert.Contains(DeviceToggle.HeadGestures, toggles);
        Assert.False(store.Current.Toggles[DeviceToggle.AutoVolume].Value);
        Assert.Equal(0, store.Current.HeadMotionType!.Value);

        Assert.True((await controller.SetToggleAsync(DeviceToggle.AutoVolume, true, Ct)).IsSuccess);
        Assert.Contains(device.Received, f => f.Command == HuaweiCommands.FeatureState && f.Payload.SequenceEqual(new byte[] { 1, 1, 2, 2, 1, 1 }));
        Assert.True(store.Current.Toggles[DeviceToggle.AutoVolume].Value);

        Assert.True((await controller.SetHeadMotionTypeAsync(1, Ct)).IsSuccess);
        Assert.Equal(2, device.Features[HuaweiCommands.FeatureHeadGestures][3]);
        Assert.Equal(1, device.Features[HuaweiCommands.FeatureHeadGestures][4]);
        Assert.Equal(1, store.Current.HeadMotionType!.Value);

        // Nod and shake both answering is not one of the two choices: hidden, not guessed.
        device.Reply(HuaweiCommands.FeatureState, [1, 1, 0x0B, 2, 1, 1, 3, 1, 1, 4, 1, 1]);
        await Until(() => store.Current.HeadMotionType is null);
    }

    [Fact]
    public async Task SmartFeatures_StayClosedWhenUnreported()
    {
        var device = new FakeHuaweiDevice();
        var (controller, store) = Create(device, "HUAWEI FreeBuds 7");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.DoesNotContain(DeviceToggle.AutoVolume, ((IControllableProfile)store.Current.Profile!).Toggles);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.HeadGestures, true, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetHeadMotionTypeAsync(0, Ct)).Status);
    }
}
