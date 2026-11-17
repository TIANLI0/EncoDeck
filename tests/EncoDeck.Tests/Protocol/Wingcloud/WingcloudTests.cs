// SPDX-License-Identifier: GPL-3.0-only
// Frame vectors are captures from an HONOR CHOICE Earbuds X5s Pro (USFY, firmware 1.1.64.2), taken from
// the independent Android app "我的X5s Pro" by shui-tiao
// (https://github.com/shui-tiao/my-x5s-pro, GPL-3.0).
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Wingcloud;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol.Wingcloud;

public class WingcloudTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (IDeviceController Controller, DeviceStateStore Store) Create(FakeX5sProDevice device, string name = "HONOR CHOICE Earbuds X5s Pro")
    {
        var identity = new DeviceIdentity("dev-x5s", name, new BluetoothAddress(0x112233445566));
        var profile = BuiltInProfiles.Create().OfType<WingcloudX5sProProfile>().Single();
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile, Connection = DeviceConnectionState.ControlConnecting });
        var context = new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        return (profile.CreateController(device, context), store);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    [Fact]
    public void Codec_FramesUseTheWingcloudHeaderAndCrc()
    {
        var codec = new WingcloudFrameCodec();
        // 5A, length = payload + 3, control 0, service, command, TLV, CRC-16/XMODEM big-endian.
        byte[] query = codec.Encode(0x2B39, [1, 0]);
        Assert.Equal("5A0005002B390100", Hex(query[..8]));
        Assert.Equal(10, query.Length);
        // Property writes carry three 4-byte TLVs; the length covers service, command and TLVs.
        byte[] write = WingcloudFrameCodec.WriteProperty(6, 1, 1);
        Assert.Equal("010400000006020400000001030400000001", Hex(write));
        Assert.Equal(8 + write.Length, codec.Encode(0x0139, write).Length);
    }

    [Fact]
    public void Codec_AcceptsDeviceFramesAndRejectsCorruptedOnes()
    {
        // The device sends exactly what the codec encodes, so the round trip is the contract.
        var codec = new WingcloudFrameCodec();
        byte[] wire = codec.Encode(0x2B39, FakeX5sProDevice.InfoReply);
        var frame = Assert.Single(new WingcloudFrameCodec().Feed(wire));
        Assert.Equal(0x2B39, frame.Command);
        Assert.True(WingcloudFrameCodec.TryParseTlv(frame.Payload, out var fields));
        Assert.Equal([0x50, 0x4B, 0x00], fields[2]);

        wire[^1] ^= 0xFF;
        Assert.Empty(new WingcloudFrameCodec().Feed(wire));
    }

    [Fact]
    public void Codec_ParsesTwoByteTlvLengths()
    {
        byte[] payload = [.. WingcloudFrameCodec.Tlv(0, [.. Enumerable.Repeat((byte)'A', 200)]), .. WingcloudFrameCodec.Tlv(1, [1, 2, 3])];
        Assert.True(WingcloudFrameCodec.TryParseTlv(payload, out var fields));
        Assert.Equal(200, fields[0].Length);
        Assert.Equal([1, 2, 3], fields[1]);
        Assert.False(WingcloudFrameCodec.TryParseTlv(payload[..^1], out _));
    }

    [Fact]
    public void Profile_OnlyMatchesTheVerifiedModel()
    {
        var profile = BuiltInProfiles.Create().OfType<WingcloudX5sProProfile>().Single();
        foreach (string name in new[] { "HONOR CHOICE Earbuds X5s Pro", "HONOR CHOICE X5s Pro", "荣耀亲选耳机X5s Pro" })
            Assert.Equal(100, profile.MatchScore(new DeviceIdentity("d", name, new BluetoothAddress(1))));
        foreach (string name in new[] { "HONOR CHOICE Earbuds X5s", "Earbuds X5s Pro 2", "X5s" })
            Assert.Equal(0, profile.MatchScore(new DeviceIdentity("d", name, new BluetoothAddress(1))));
    }

    [Fact]
    public async Task Session_ReadsBatteryFirmwareAndOnlyOffersWhatAnswered()
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        bool initialized = await controller.InitializeAsync(Ct);
        Assert.True(initialized, "init failed; sent: " + string.Join(" | ", device.Received.Select(f => $"{f.Command:X4}:{Hex(f.Payload)}")));

        Assert.Equal(0x50, store.Current.Battery!.Left!.Value.Level);
        Assert.Equal(0x4B, store.Current.Battery!.Right!.Value.Level);
        Assert.Null(store.Current.Battery!.Case);
        Assert.Equal("BTV-ME10-00USFY", store.Current.Firmware!.Value.ModelCode);
        Assert.Equal("1.1.64.2", store.Current.Firmware!.Value.Version);

        var profile = (WingcloudX5sProProfile)store.Current.Profile!;
        Assert.True(profile.NoiseConfirmed);
        Assert.True(profile.EqConfirmed);
        Assert.True(profile.QualityConfirmed);
        Assert.True(profile.GesturesConfirmed);
        Assert.True(profile.CycleConfirmed);
        Assert.Equal(3, profile.EqualizerPresets.Count);
        Assert.Equal(4, profile.NoiseControlOptions.Count(o => o.Mode == NoiseControlMode.NoiseCancellation));
        Assert.True(profile.Capabilities.IsAvailable(DeviceCapability.SpatialAudio, false));
        Assert.False(profile.Capabilities.IsAvailable(DeviceCapability.ChargingStatus, false));
    }

    [Fact]
    public async Task Session_LeavesUnansweredSettingsClosed()
    {
        var device = new FakeX5sProDevice();
        device.Properties.Clear();
        device.AncSilent = true;
        var (controller, store) = Create(device); await using var session = controller;
        bool initialized = await controller.InitializeAsync(Ct);
        Assert.True(initialized, "init failed; sent: " + string.Join(" | ", device.Received.Select(f => $"{f.Command:X4}:{Hex(f.Payload)}")));
        var profile = (WingcloudX5sProProfile)store.Current.Profile!;
        Assert.False(profile.NoiseConfirmed);
        Assert.False(profile.EqConfirmed);
        Assert.False(profile.QualityConfirmed);
        Assert.False(profile.GesturesConfirmed);
        Assert.Empty(profile.EqualizerPresets);
        Assert.Empty(profile.GestureSlots);
        Assert.Null(store.Current.Equalizer);
        Assert.Empty(profile.NoiseControlOptions);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.Ldac, true, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct)).Status);
    }

    [Theory]
    [InlineData(NoiseControlMode.Off, null, 0, 0)]
    [InlineData(NoiseControlMode.NoiseCancellation, "ultra", 1, 3)]
    [InlineData(NoiseControlMode.NoiseCancellation, "dynamic", 1, 0)]
    [InlineData(NoiseControlMode.Transparency, null, 2, 0)]
    public async Task NoiseControl_WritesLevelThenModeAndReadsBack(NoiseControlMode mode, string? level, byte wireMode, byte wireLevel)
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        var result = await controller.SetNoiseControlAsync(new NoiseControlState(mode, level), Ct);
        Assert.True(result.IsSuccess, result.Detail);
        // The write sends mode then level; the read-back answers level then mode.
        Assert.Equal([wireLevel, wireMode], device.Anc);
        Assert.Equal(new NoiseControlState(mode, level), store.Current.NoiseControl!.Value);
        Assert.Equal(StateSource.CommandConfirmation, store.Current.NoiseControl!.Source);
        Assert.Contains(device.Received, f => f.Command == 0x2B04 && f.Payload.SequenceEqual(new byte[] { 1, 2, wireMode, wireLevel }));
    }

    [Fact]
    public async Task NoiseControl_ModeWithoutLevelUsesTheFirstLevelOfThatMode()
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        // Picking plain "noise cancelling" (no level) must not be refused: the earbuds need a concrete level.
        var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation), Ct);
        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal([0, 1], device.Anc);
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "dynamic"), store.Current.NoiseControl!.Value);
    }

    [Fact]
    public async Task NoiseControl_EveryOfferedLevelCanBeSelected()
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        foreach (var level in new[] { ("dynamic", (byte)0), ("cozy", (byte)1), ("general", (byte)2), ("ultra", (byte)3) })
        {
            var (id, wire) = level;
            var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation, id), Ct);
            Assert.True(result.IsSuccess, $"{id}: {result.Detail}");
            Assert.Equal([wire, (byte)1], device.Anc);
            Assert.Equal(id, store.Current.NoiseControl!.Value.LevelId);
        }
    }

    [Fact]
    public async Task NoiseControl_NotReportedBackIsRejected()
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.AncWrites = false;
        Assert.Equal(CommandStatus.Timeout, (await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct)).Status);
        Assert.Equal(NoiseControlMode.Off, store.Current.NoiseControl!.Value.Mode);
        Assert.NotNull(store.Current.LastError);
    }

    [Fact]
    public async Task UnsolicitedNoiseReport_UpdatesStateAndIsAcknowledged()
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.PushNoise(1, 2);
        for (int i = 0; i < 100 && store.Current.NoiseControl?.Source != StateSource.DeviceNotification; i++) await Task.Delay(20, Ct);
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "general"), store.Current.NoiseControl!.Value);
        Assert.Equal(StateSource.DeviceNotification, store.Current.NoiseControl!.Source);
        // Mode first in the report, level first in the acknowledgement.
        Assert.Contains(device.Received, f => f.Command == 0x2B04 && f.Payload.SequenceEqual(new byte[] { 2, 1, 0 }));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2", 2)]
    [InlineData("100", 100)]
    public async Task Equalizer_WritesPresetAndReadsBack(string presetId, int wire)
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        var result = await controller.SetEqualizerPresetAsync(presetId, Ct);
        Assert.True(result.IsSuccess, result.Detail);
        Assert.True(device.Properties[3 << 8 | 5] == wire, $"device {(device.Properties[3 << 8 | 5])} want {wire}");
        Assert.Equal(presetId, store.Current.Equalizer!.Value.ActivePresetId);
        Assert.Contains(device.Received, f => f.Command == 0x0139
            && WingcloudFrameCodec.TryParseTlv(f.Payload, out var t) && t[1].SequenceEqual(WingcloudFrameCodec.Int32(3)) && t[2].SequenceEqual(WingcloudFrameCodec.Int32(5)));
    }

    [Fact]
    public async Task Equalizer_ValueThatDoesNotStickIsRejected()
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.IgnorePropertyWrites = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetEqualizerPresetAsync("2", Ct)).Status);
        Assert.Equal("0", store.Current.Equalizer!.Value.ActivePresetId);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("1", Ct)).Status);
    }

    [Fact]
    public async Task Toggles_LdacAndSurroundSoundAreReadBackAndExcluding()
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct), "init failed");
        Assert.True((await controller.SetToggleAsync(DeviceToggle.Ldac, true, Ct)).IsSuccess, $"on failed: {device.Properties[6 << 8 | 0]}");
        Assert.Equal(1, device.Properties[6 << 8 | 0]);
        Assert.True(store.Current.Toggles[DeviceToggle.Ldac]!.Value);
        // The earbuds drop LDAC when the surround effect goes on.
        device.Properties[6 << 8 | 0] = 0;
        Assert.True((await controller.SetToggleAsync(DeviceToggle.SpatialAudio, true, Ct)).IsSuccess);
        Assert.Equal(1, device.Properties[6 << 8 | 1]);
        Assert.False(store.Current.Toggles[DeviceToggle.Ldac]!.Value);
        Assert.True(store.Current.Toggles[DeviceToggle.SpatialAudio]!.Value);
    }

    [Fact]
    public async Task Toggles_LdacChangeIsTrustedBecauseTheEarbudsRestart()
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.Silent = true; // the radio restarts: no acknowledgement and no read-back
        var result = await controller.SetToggleAsync(DeviceToggle.Ldac, true, Ct);
        Assert.True(result.IsSuccess, result.Detail);
        Assert.True(store.Current.Toggles[DeviceToggle.Ldac]!.Value);
        // LDAC always turns the surround effect off on this firmware.
        Assert.False(store.Current.Toggles[DeviceToggle.SpatialAudio]!.Value);
        // The expected restart is not an error, and the session reconnects instead of giving up.
        Assert.True(store.Current.LinkRestarting);
        Assert.Null(store.Current.LastError);
    }

    [Fact]
    public async Task Gestures_AreReadBackAndWrittenPerSlot()
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Contains(store.Current.KeyFunctions!.Value, k => k is { Side: EarSide.Right, Button: 6, Action: GestureAction.TripleSqueeze, Function: 0 });

        var assignment = new KeyFunction(EarSide.Left, 5, GestureAction.TripleSqueeze, 1);
        Assert.True((await controller.SetKeyFunctionAsync(assignment, Ct)).IsSuccess);
        Assert.Equal(1, device.Properties[4 << 8 | 5]);
        Assert.Contains(store.Current.KeyFunctions!.Value, k => k == assignment);

        // The hold gesture shares the pinch service (4/1) on this firmware; service 5 is not exposed.
        var hold = new KeyFunction(EarSide.Both, 1, GestureAction.SqueezeAndHold, 4);
        Assert.True((await controller.SetKeyFunctionAsync(hold, Ct)).IsSuccess);
        Assert.Equal(4, device.Properties[4 << 8 | 1]);
        Assert.Contains(device.Received, f => f.Command == 0x0139
            && WingcloudFrameCodec.TryParseTlv(f.Payload, out var t) && t[1].SequenceEqual(WingcloudFrameCodec.Int32(4)) && t[2].SequenceEqual(WingcloudFrameCodec.Int32(1)));
    }

    [Fact]
    public async Task NoiseCycle_IsWrittenToBothFieldsAndReadBack()
    {
        var device = new FakeX5sProDevice();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct), "init failed: " + string.Join(" | ", device.Received.Select(f => $"{f.Command:X4}:{Hex(f.Payload)}")));
        // Wire 2 offers all three modes: off, noise cancelling and transparency.
        Assert.Equal(7u, store.Current.NoiseCycle!.Value.LeftMask);
        Assert.True((await controller.SetNoiseCycleAsync(EarSide.Left, 6, Ct)).IsSuccess);
        Assert.Equal([3, 3], device.Cycle);
        Assert.Equal(new NoiseCycleState(6, 6), store.Current.NoiseCycle!.Value);
        Assert.Contains(device.Received, f => f.Command == 0x2B18 && f.Payload.SequenceEqual(new byte[] { 1, 1, 3, 2, 1, 3 }));
    }

    [Fact]
    public async Task Refresh_ResumesPollingOnlyAfterTheInformationFrame()
    {
        var device = new FakeX5sProDevice { Silent = true };
        var (controller, store) = Create(device); await using var session = controller;
        Assert.False(await controller.InitializeAsync(Ct));
        device.Silent = false;
        // 2B 39 is never answered on this firmware, so a refresh re-reads the settings instead.
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(0x50, store.Current.Battery!.Left!.Value.Level);
    }
}
