// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Moondrop;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public sealed partial class MoondropTests
{
    [Fact]
    public async Task InferredGainAndVolumeGestureRequireOptIn()
    {
        var (profile, store, identity) = Session("MOONDROP MOCA");
        var device = new PodChannel(false);
        bool experimental = false;
        await using var controller = profile.CreateController(device, new(identity, store, () => experimental, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetGainAsync(0, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SingleSqueeze, 4), Ct)).Status);
        experimental = true;
        Assert.True((await controller.SetGainAsync(0, Ct)).IsSuccess);
        Assert.True((await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SingleSqueeze, 4), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x41, 0x23, 0xEF, 0x77, 0x89 }, device.Touch);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(new(EarSide.Right, 0, GestureAction.TripleSqueeze, 15), Ct)).Status);
    }

    [Theory]
    [InlineData("MOONDROP EDGE", 0, 2)]
    [InlineData("MOONDROP PUDDING", 2, 0)]
    [InlineData("MOONDROP GOLDEN AGES 2", 2, 0)]
    [InlineData("MOONDROP SPACE TRAVEL 2", 2, 0)]
    [InlineData("MOONDROP MOCA", 0, 2)]
    [InlineData("MOONDROP SPACE TRAVEL 2 ULTRA", 2, 0)]
    public async Task GainUsesCatalogMappingAndActualReadback(string name, int low, int high)
    {
        var (profile, store, identity) = Session(name);
        var device = new PodChannel(false);
        await using var controller = profile.CreateController(device, new(identity, store, () => true, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.True((await controller.SetGainAsync(0, Ct)).IsSuccess);
        Assert.Equal((byte)low, device.LastGainWrite);
        Assert.Equal(0, store.Current.Gain?.Value);
        Assert.True((await controller.SetGainAsync(2, Ct)).IsSuccess);
        Assert.Equal((byte)high, device.LastGainWrite);
        Assert.Equal(2, store.Current.Gain?.Value);
        device.RejectGain = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetGainAsync(0, Ct)).Status);
        Assert.Equal(2, store.Current.Gain?.Value);
    }

    [Theory]
    [InlineData("MOONDROP unknown", false)]
    [InlineData("MOONDROP BLOCK", false)]
    [InlineData("MOONDROP PUDDING", true)]
    public async Task UnknownMappingOrMissingDiscoveryCannotWriteGain(string name, bool omit)
    {
        var (profile, store, identity) = Session(name);
        var device = new PodChannel(false);
        if (omit) device.OmittedFeatures.Add(15);
        await using var controller = profile.CreateController(device, new(identity, store, () => true, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetGainAsync(0, Ct)).Status);
        Assert.Null(device.LastGainWrite);
        Assert.Null(store.Current.Gain);
    }

    [Fact]
    public async Task MissingSettingFeaturesRejectAllNewWrites()
    {
        var (profile, store, identity) = Session("MOONDROP PUDDING");
        var device = new PodChannel(false);
        device.OmittedFeatures.UnionWith([14, 15, 18, 19, 22]);
        await using var controller = profile.CreateController(device, new(identity, store, () => true, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.PromptTone, true, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.IndicatorLight, false, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetPromptVolumeAsync(50, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SingleSqueeze, 1), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetSpatialAudioAsync(SpatialAudioMode.Fixed, Ct)).Status);
        Assert.Equal(0, device.SettingWrites);
    }

    [Fact]
    public async Task VoiceAndLedPreserveWholeConfigurationAndRejectDifferentReadback()
    {
        var (profile, store, identity) = Session("MOONDROP PUDDING");
        var device = new PodChannel(false);
        await using var controller = profile.CreateController(device, new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.True((await controller.SetToggleAsync(DeviceToggle.PromptTone, true, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 1, 20, 7 }, device.Voice);
        Assert.True(store.Current.Toggles[DeviceToggle.PromptTone].Value);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.IndicatorLight, false, Ct)).IsSuccess);
        Assert.False(store.Current.Toggles[DeviceToggle.IndicatorLight].Value);
        device.RejectVoiceIndex = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetPromptVolumeAsync(50, Ct)).Status);
        device.Voice = [1, 50];
        int writes = device.SettingWrites;
        Assert.Equal(CommandStatus.Timeout, (await controller.SetToggleAsync(DeviceToggle.PromptTone, false, Ct)).Status);
        Assert.Equal(writes, device.SettingWrites);
        Assert.True(store.Current.Toggles[DeviceToggle.PromptTone].Value);
    }

    [Fact]
    public async Task TouchWritesPreserveNibblesAndOnlyClearSameEarAlternateHold()
    {
        var (profile, store, identity) = Session("MOONDROP PUDDING");
        var device = new PodChannel(false);
        await using var controller = profile.CreateController(device, new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Contains(store.Current.KeyFunctions!.Value, k => k.Function == 15);
        Assert.True((await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SingleSqueeze, 3), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x31, 0x23, 0xEF, 0x77, 0x89 }, device.LastTouchWrite);
        Assert.True((await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.LongHold, 6), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x31, 0x23, 0xEF, 0x07, 0x69 }, device.LastTouchWrite);
        Assert.True((await controller.SetKeyFunctionAsync(new(EarSide.Right, 0, GestureAction.SqueezeAndHold, 0), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x31, 0x23, 0xEF, 0x00, 0x69 }, device.LastTouchWrite);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SingleSqueeze, 4), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SingleSqueeze, 15), Ct)).Status);
        device.RejectTouchOtherSlot = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetKeyFunctionAsync(new(EarSide.Right, 0, GestureAction.LongHold, 7), Ct)).Status);
        device.Touch = [0x11, 0x23];
        int writes = device.SettingWrites;
        Assert.Equal(CommandStatus.Timeout, (await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SingleSqueeze, 1), Ct)).Status);
        Assert.Equal(writes, device.SettingWrites);
    }

    [Fact]
    public async Task GainTimeoutDoesNotInventRequestedState()
    {
        var (profile, store, identity) = Session("MOONDROP PUDDING");
        var device = new PodChannel(false);
        await using var controller = profile.CreateController(device, new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        var observed = store.Current.Gain;
        device.SuppressGainRead = true;
        Assert.Equal(CommandStatus.Timeout, (await controller.SetGainAsync(0, Ct)).Status);
        Assert.Equal(observed, store.Current.Gain);
        Assert.Empty(store.Current.PendingCommands);
    }

    [Theory]
    [InlineData(DeviceToggle.InEarDetection, 17)]
    [InlineData(DeviceToggle.SpatialAudio, 18)]
    [InlineData(DeviceToggle.HeadTracking, 18)]
    [InlineData(DeviceToggle.BassEngine, 27)]
    [InlineData(DeviceToggle.ChannelSwap, 30)]
    [InlineData(DeviceToggle.Ldac, 16)]
    [InlineData(DeviceToggle.Lc3, 16)]
    public async Task AdditionalSettingsUseDiscoveredCommandsAndConfirmedReadback(DeviceToggle toggle, int feature)
    {
        var (profile, store, identity) = Session("MOONDROP EDGE");
        var device = new PodChannel(false);
        await using var controller = profile.CreateController(device, new(identity, store, () => true, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(CapabilitySupport.Experimental, store.Current.Profile!.Capabilities.Get(MoondropProfile.ToggleCapability(toggle)).Support);
        Assert.Contains(toggle, store.Current.Toggles.Keys);
        bool target = !store.Current.Toggles[toggle].Value;
        var result = await controller.SetToggleAsync(toggle, target, Ct);
        Assert.True(result.IsSuccess, $"{result}; target={target}; state={string.Join(',', store.Current.Toggles.Select(t => $"{t.Key}={t.Value.Value}"))}");
        Assert.Equal(target, store.Current.Toggles[toggle].Value);
        int expectedSet = toggle switch
        {
            DeviceToggle.Ldac => 4,
            DeviceToggle.Lc3 => 3,
            DeviceToggle.HiRes => 6,
            DeviceToggle.HeadTracking => 4,
            _ => 2,
        };
        Assert.True(device.Writes.Any(w => w.Feature == feature && w.Command == expectedSet && w.Payload.SequenceEqual(new[] { target ? (byte)1 : (byte)0 })),
            $"Writes: {string.Join(';', device.Writes.Select(w => $"{w.Feature}/{w.Command}={Convert.ToHexString(w.Payload)}"))}");
    }

    [Fact]
    public async Task AdditionalSettingsRequireFeatureDiscoveryAndExperimentalOptIn()
    {
        var (profile, store, identity) = Session("MOONDROP EDGE");
        var device = new PodChannel(false);
        bool experimental = false;
        await using var controller = profile.CreateController(device, new(identity, store, () => experimental, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.InEarDetection, true, Ct)).Status);
        experimental = true;
        Assert.True((await controller.SetToggleAsync(DeviceToggle.InEarDetection, false, Ct)).IsSuccess);
        device.OmittedFeatures.UnionWith([17, 18, 27, 30]);
        var omittedDevice = new PodChannel(false);
        omittedDevice.OmittedFeatures.UnionWith(device.OmittedFeatures);
        var omittedProfile = (MoondropProfile)new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity)!;
        store.Reset(DeviceState.Empty with { Device = identity, Profile = omittedProfile });
        await using var omitted = omittedProfile.CreateController(omittedDevice, new(identity, store, () => true, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await omitted.InitializeAsync(Ct));
        foreach (var toggle in new[] { DeviceToggle.InEarDetection, DeviceToggle.SpatialAudio, DeviceToggle.HeadTracking, DeviceToggle.BassEngine, DeviceToggle.ChannelSwap })
            Assert.Equal(CommandStatus.Unsupported, (await omitted.SetToggleAsync(toggle, true, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await omitted.SetSpatialAudioAsync(SpatialAudioMode.Fixed, Ct)).Status);
    }

    [Fact]
    public async Task CodecDiscoveryRequiresValidIndividualResponses()
    {
        var (profile, store, identity) = Session("MOONDROP EDGE");
        var device = new PodChannel(false);
        device.UnsupportedCodecs.UnionWith([DeviceToggle.Ldac, DeviceToggle.Lc3]);
        await using var controller = profile.CreateController(device, new(identity, store, () => true, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        var discovered = Assert.IsType<MoondropProfile>(store.Current.Profile);
        Assert.Contains(DeviceToggle.HiRes, discovered.Toggles);
        Assert.DoesNotContain(DeviceToggle.Ldac, discovered.Toggles);
        Assert.DoesNotContain(DeviceToggle.Lc3, discovered.Toggles);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.Ldac, true, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.Lc3, true, Ct)).Status);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.HiRes, false, Ct)).IsSuccess);
    }

    [Fact]
    public async Task SpatialAudioUsesIndependentSpatialAndHeadTrackingReadback()
    {
        var (profile, store, identity) = Session("MOONDROP EDGE");
        var device = new PodChannel(false);
        await using var controller = profile.CreateController(device, new(identity, store, () => true, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(SpatialAudioMode.Off, store.Current.Spatial?.Value);
        Assert.True((await controller.SetSpatialAudioAsync(SpatialAudioMode.Fixed, Ct)).IsSuccess);
        Assert.True((await controller.SetSpatialAudioAsync(SpatialAudioMode.HeadTracking, Ct)).IsSuccess);
        Assert.Equal(SpatialAudioMode.HeadTracking, store.Current.Spatial?.Value);
        Assert.Contains(device.Writes, w => w.Feature == 18 && w.Command == 2 && w.Payload.SequenceEqual(new byte[] { 1 }));
        Assert.Contains(device.Writes, w => w.Feature == 18 && w.Command == 4 && w.Payload.SequenceEqual(new byte[] { 1 }));
        device.RejectSpatial = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetSpatialAudioAsync(SpatialAudioMode.Off, Ct)).Status);
        Assert.True(store.Current.Toggles[DeviceToggle.SpatialAudio].Value);
        Assert.False(store.Current.Toggles[DeviceToggle.HeadTracking].Value);
        Assert.Equal(SpatialAudioMode.Fixed, store.Current.Spatial?.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RfcommNegotiatesAndUsesSelectedFramingForSettings(bool raw)
    {
        var (profile, store, identity) = Session("MOONDROP PUDDING");
        var device = new PodChannel(true, raw);
        await using var controller = profile.CreateController(device, new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(1, device.FramedProbes);
        Assert.Equal(raw ? 1 : 0, device.RawProbes);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.PromptTone, true, Ct)).IsSuccess);
        Assert.True((await controller.SetGainAsync(0, Ct)).IsSuccess);
        Assert.True((await controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.LongHold, 7), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 1, 20, 7 }, device.Voice);
        Assert.Equal(0, store.Current.Gain?.Value);
    }

    [Fact]
    public void BareFixedLengthResponsesRetainFragmentsWithoutScanningPayloadMarkers()
    {
        var decoder = new GaiaFrameDecoder();
        byte[] voice = Reply(14, 1, [1, 29, 255], false);
        var frames = new List<GaiaFrame>();
        foreach (byte b in voice) frames.AddRange(decoder.Feed([b], true, false));
        Assert.Equal(new byte[] { 1, 29, 255 }, Assert.Single(frames).Payload);
        byte[] touch = Reply(22, 2, [0x00, 0x1D, 0xFF, 0x77, 0], false);
        Assert.Empty(decoder.Feed(touch[..5], true, false));
        Assert.Equal(new byte[] { 0x00, 0x1D, 0xFF, 0x77, 0 }, Assert.Single(decoder.Feed(touch[5..], true, false)).Payload);
        byte[] battery = Reply(13, 1, [1, 90, 2, 80], false);
        Assert.Empty(decoder.Feed(battery, true, false));
        Assert.Equal(new byte[] { 1, 90, 2, 80 }, Assert.Single(decoder.Feed([], true, true)).Payload);
    }

    private static (MoondropProfile Profile, DeviceStateStore Store, DeviceIdentity Identity) Session(string name)
    {
        var identity = new DeviceIdentity("moondrop-test", name, new BluetoothAddress(0));
        var profile = (MoondropProfile)new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity)!;
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        return (profile, store, identity);
    }
}
