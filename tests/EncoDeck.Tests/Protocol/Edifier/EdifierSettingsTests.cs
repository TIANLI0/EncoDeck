// SPDX-License-Identifier: GPL-3.0-only
// Fixtures: W820NB Double Gold request/response frames from mEDIFIER doc/KnownCommands/W820NB_Double_Gold.txt
// (https://github.com/wh201906/mEDIFIER, 10056b5, MIT, Copyright (c) 2023 Self Not Found; MIT notice in
// THIRD_PARTY_NOTICES.md); W860NB PRO, 花再 Evo Pro and FitBuds Turbo frames from HyperEars
// docs/edifier-bes-protocol.md (https://github.com/silverpoetry/HyperEars, 361d279, GPL-3.0-only);
// X5 Pro noise group from BudsLink deviceConfigs/X5Pro.js (https://github.com/maniacx/BudsLink, ba3160d, GPL-3.0).
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Edifier;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol.Edifier;

public sealed class EdifierSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    private static DeviceIdentity Device(string name) => new("dev-edifier", name, new BluetoothAddress(0x0011223344AA));

    private static EdifierProfile Match(string name) =>
        Assert.IsType<EdifierProfile>(new DeviceProfileRegistry(EdifierProfiles.CreateBuiltIn()).Match(Device(name)));

    // ---- captured frames ------------------------------------------------------------------------

    [Theory]
    [InlineData(0xC1, "03 09", "AA 03 C1 03 09 21 93")] // ambient level +3
    [InlineData(0xC1, "03 03", "AA 03 C1 03 03 21 8D")] // ambient level -3
    [InlineData(0xC1, "02", "AA 02 C1 02 21 88")] // noise cancelling
    [InlineData(0xF1, "0A 07", "AA 03 F1 0A 07 21 C8")] // button cycles all three modes
    [InlineData(0xF1, "0A 03", "AA 03 F1 0A 03 21 C4")] // standard + noise cancelling
    [InlineData(0xF0, "0A", "AA 02 F0 0A 21 BF")] // button control query
    [InlineData(0xD1, "00 05", "AA 03 D1 00 05 21 9C")] // power off after 5 min
    [InlineData(0xD1, "00 B4", "AA 03 D1 00 B4 22 4B")] // power off after 3 h
    [InlineData(0xD2, "", "AA 01 D2 21 96")] // timer off
    [InlineData(0xD3, "", "AA 01 D3 21 97")] // timer query
    [InlineData(0x09, "01", "AA 02 09 01 20 CF")] // game mode on
    [InlineData(0x08, "", "AA 01 08 20 CC")] // game mode query
    [InlineData(0xC4, "03", "AA 02 C4 03 21 8C")] // rock
    [InlineData(0xD5, "", "AA 01 D5 21 99")] // sound effect query
    public void V1Requests_MatchTheW820nbDoubleGoldCapture(int command, string payload, string wire) =>
        Assert.Equal(Hex(wire), EdifierFrameCodec.Encode((byte)command, Hex(payload)));

    [Theory]
    [InlineData("BB 03 CC 01 06 21 AA", 0xCC, "01 06")]
    [InlineData("BB 02 08 00 20 DE", 0x08, "00")]
    [InlineData("BB 03 F0 0A 06 21 D7", 0xF0, "0A 06")]
    [InlineData("BB 02 D3 00 21 A9", 0xD3, "00")]
    [InlineData("BB 03 D3 00 05 21 AF", 0xD3, "00 05")]
    [InlineData("CC 02 D1 01 21 B9", 0xD1, "01")]
    [InlineData("BB 02 D5 03 21 AE", 0xD5, "03")]
    public void V1Responses_DecodeTheW820nbDoubleGoldCapture(string wire, int command, string payload)
    {
        var frame = Assert.Single(new EdifierFrameDecoder().Feed(Hex(wire)));
        Assert.Equal(command, frame.Command);
        Assert.Equal(Hex(payload), frame.Payload);
    }

    [Fact]
    public void PowerOffTimer_ParsesOffAndMinutes()
    {
        Assert.Equal(0, EdifierDeviceController.ParsePowerOffTimer([0]));
        Assert.Equal(5, EdifierDeviceController.ParsePowerOffTimer([0, 5]));
        Assert.Equal(180, EdifierDeviceController.ParsePowerOffTimer([0, 0xB4]));
        Assert.Null(EdifierDeviceController.ParsePowerOffTimer([]));
        Assert.Null(EdifierDeviceController.ParsePowerOffTimer([0, 0, 0]));
    }

    [Fact]
    public void BesV2_XorFramesMatchTheHyperEarsCaptures()
    {
        // W860NB PRO: set wind noise (10 03), off report (10 05), battery 60 %.
        Assert.Equal(Hex("AA EC C1 00 02 B5 A6 B4"), EdifierFrameCodec.EncodeV2(0xC1, [0x10, 0x03], xor: true));
        // 花再 Evo Pro: wind noise write (1B 04).
        Assert.Equal(Hex("AA EC C1 00 02 BE A1 B8"), EdifierFrameCodec.EncodeV2(0xC1, [0x1B, 0x04], xor: true));
        // FitBuds Turbo: plaintext ANC write.
        Assert.Equal(Hex("AA EC C1 00 02 1B 01 75"), EdifierFrameCodec.EncodeV2(0xC1, [0x1B, 0x01]));

        var frames = new EdifierFrameDecoder(version2: true).Feed(Hex("BB EC CC 00 02 B5 A0 CA BB EC D0 00 01 99 11 BB EC F2 00 06 A6 C1 C7 A5 A6 B4 CC"));
        Assert.Equal(3, frames.Count);
        byte[] noise = [.. frames[0].Payload];
        EdifierFrameCodec.Xor(noise);
        Assert.Equal(new NoiseControlState(NoiseControlMode.Off), EdifierSettings.DecodeNoise(EdifierAnc.SlotW860, noise, false));
        Assert.Equal(60, frames[1].Payload[0] ^ EdifierConstants.PayloadXor);
        byte[] state = [.. frames[2].Payload];
        EdifierFrameCodec.Xor(state);
        var battery = EdifierDeviceController.ParseTwsBattery(state, DateTimeOffset.UnixEpoch, StateSource.QueryResponse)!;
        Assert.Equal(100, battery.Left?.Value.Level);
        Assert.Equal(98, battery.Right?.Value.Level);
        Assert.Null(battery.Case);
    }

    [Fact]
    public void TwsState_CaseMarkedOfflineIsNotShown()
    {
        var online = EdifierDeviceController.ParseTwsBattery([3, 80, 70, 60, 1], DateTimeOffset.UnixEpoch, StateSource.QueryResponse)!;
        Assert.Equal(60, online.Case?.Value.Level);
        var offline = EdifierDeviceController.ParseTwsBattery([3, 80, 70, 60, 3], DateTimeOffset.UnixEpoch, StateSource.QueryResponse)!;
        Assert.Null(offline.Case);
        Assert.Equal(80, offline.Left?.Value.Level);
    }

    [Theory]
    [InlineData((int)EdifierAnc.SlotEvo, "1B 01", NoiseControlMode.NoiseCancellation, null)]
    [InlineData((int)EdifierAnc.SlotEvo, "1B 03", NoiseControlMode.NoiseCancellation, null)]
    [InlineData((int)EdifierAnc.SlotEvo, "1B 04", NoiseControlMode.NoiseCancellation, EdifierSettings.WindLevel)]
    [InlineData((int)EdifierAnc.SlotEvo, "1B 05", NoiseControlMode.Transparency, null)]
    [InlineData((int)EdifierAnc.SlotEvo, "1B 06", NoiseControlMode.Off, null)]
    [InlineData((int)EdifierAnc.SlotW860, "10 02", NoiseControlMode.NoiseCancellation, EdifierSettings.ComfortLevel)]
    [InlineData((int)EdifierAnc.SlotW860, "10 04", NoiseControlMode.Transparency, null)]
    [InlineData((int)EdifierAnc.SlotX5, "17 01", NoiseControlMode.NoiseCancellation, null)]
    [InlineData((int)EdifierAnc.SlotX5, "17 02", NoiseControlMode.Transparency, null)]
    [InlineData((int)EdifierAnc.SlotX5, "17 03", NoiseControlMode.Off, null)]
    [InlineData((int)EdifierAnc.V1, "03 09", NoiseControlMode.Transparency, "ambient+3")]
    [InlineData((int)EdifierAnc.V1, "01 06", NoiseControlMode.Off, null)]
    public void NoiseDialects_Decode(int anc, string payload, NoiseControlMode mode, string? level) =>
        Assert.Equal(new NoiseControlState(mode, level), EdifierSettings.DecodeNoise((EdifierAnc)anc, Hex(payload), ambientLevels: true));

    [Theory]
    [InlineData((int)EdifierAnc.SlotEvo, "10 01")] // another model's slot
    [InlineData((int)EdifierAnc.SlotEvo, "1B 07")]
    [InlineData((int)EdifierAnc.SlotX5, "17 04")]
    [InlineData((int)EdifierAnc.SlotW860, "10")]
    [InlineData((int)EdifierAnc.None, "01")]
    [InlineData((int)EdifierAnc.V1, "04")]
    public void NoiseDialects_DoNotGuessUnknownValues(int anc, string payload) =>
        Assert.Null(EdifierSettings.DecodeNoise((EdifierAnc)anc, Hex(payload), ambientLevels: true));

    // ---- model matching -------------------------------------------------------------------------

    [Theory]
    [InlineData("EDIFIER W820NB Double Gold", "edifier-w820nb-double-gold")]
    [InlineData("W820NB双金标版", "edifier-w820nb-double-gold")]
    [InlineData("EDIFIER W820NB", "edifier-w820nb")]
    [InlineData("EDIFIER W860NB Pro", "edifier-w860nb-pro")]
    [InlineData("漫步者・花再 Evo Pro", "edifier-evo-pro")]
    [InlineData("EDIFIER FitBuds Turbo", "edifier-fitbuds-turbo")]
    [InlineData("EDIFIER FitClip Ultra", "edifier-fitclip-ultra")]
    [InlineData("EDIFIER X5 Pro", "edifier-x5-pro")]
    public void CommunityModels_MatchTheirRecordedNames(string name, string modelId) =>
        Assert.Equal(modelId, Match(name).ModelId);

    [Theory]
    [InlineData("Evo Pro")]
    [InlineData("X5 Pro")]
    public void BareNamesUsedByOtherBrands_AreNotClaimedAsCommunityModels(string name)
    {
        var match = new DeviceProfileRegistry(EdifierProfiles.CreateBuiltIn()).Match(Device(name));
        Assert.True(match is null || match.ModelId is not ("edifier-evo-pro" or "edifier-x5-pro"));
    }

    [Fact]
    public void Profiles_OfferNothingBeyondBatteryBeforeReadBack()
    {
        var doubleGold = Match("EDIFIER W820NB Double Gold");
        Assert.Equal(CapabilitySupport.Unknown, doubleGold.Capabilities.Get(DeviceCapability.GamingMode).Support);
        Assert.Equal(CapabilitySupport.Unknown, doubleGold.Capabilities.Get(DeviceCapability.TouchCustomization).Support);
        Assert.Empty(doubleGold.Toggles);
        Assert.Empty(doubleGold.EqualizerPresets);
        // Standard noise modes stay available for v1 headphones, as before; ambient levels wait for a read.
        Assert.Equal(3, doubleGold.NoiseControlOptions.Count);

        var w200 = Match("EDIFIER W200BT Plus");
        Assert.Empty(w200.NoiseControlOptions);
        Assert.Equal(CapabilitySupport.Unsupported, w200.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        Assert.Equal(CapabilitySupport.Unsupported, w200.Capabilities.Get(DeviceCapability.GamingMode).Support);
        Assert.Equal(CapabilitySupport.Unsupported, Match("EDIFIER W860NB Pro").Capabilities.Get(DeviceCapability.ChargingStatus).Support);
    }

    // ---- simulated devices ----------------------------------------------------------------------

    [Fact]
    public async Task W820nbDoubleGold_ReadsEverySettingAndConfirmsWrites()
    {
        var device = new FakeEdifier(Hex("01 06")) { KeyMask = 6, SoundEffect = 1 };
        await RunAsync("EDIFIER W820NB Double Gold", device, false, async (controller, store) =>
        {
            var profile = Assert.IsType<EdifierProfile>(store.Current.Profile);
            Assert.Equal(EdifierFeatures.Noise | EdifierFeatures.AmbientLevel | EdifierFeatures.GameMode | EdifierFeatures.KeyControl
                | EdifierFeatures.PowerOffTimer | EdifierFeatures.SoundEffect, profile.Confirmed);
            Assert.Equal(10, profile.NoiseControlOptions.Count);
            Assert.Contains(DeviceToggle.GamingMode, profile.Toggles);
            Assert.Equal(4, profile.EqualizerPresets.Count);
            Assert.Equal(CapabilitySupport.Supported, profile.Capabilities.Get(DeviceCapability.TouchCustomization).Support);
            Assert.Equal(0, Timer(store));
            Assert.Equal(6, Assert.Single(store.Current.KeyFunctions!.Value).Function);
            Assert.Equal("edifier-eq-1", store.Current.Equalizer?.Value.ActivePresetId);
            Assert.Equal(new NoiseControlState(NoiseControlMode.Off), store.Current.NoiseControl?.Value);

            Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency, "ambient+3"), Ct)).IsSuccess);
            Assert.Equal(Hex("03 09"), device.Last(0xC1));
            Assert.Equal("ambient+3", store.Current.NoiseControl?.Value.LevelId);

            Assert.True((await controller.SetOptionAsync(EdifierDeviceController.PowerOffOption, 180, Ct)).IsSuccess);
            Assert.Equal(Hex("00 B4"), device.Last(0xD1));
            Assert.Equal(180, Timer(store));
            Assert.True((await controller.SetOptionAsync(EdifierDeviceController.PowerOffOption, 0, Ct)).IsSuccess);
            Assert.Empty(device.Last(0xD2));
            Assert.Equal(0, Timer(store));
            // Only the official choices are written; a zero-minute set would power off at once.
            Assert.Equal(CommandStatus.Unsupported, (await controller.SetOptionAsync(EdifierDeviceController.PowerOffOption, 7, Ct)).Status);
            Assert.DoesNotContain(device.Requests, r => r.Command == 0xD1 && r.Payload.SequenceEqual(new byte[] { 0, 0 }));

            Assert.True((await controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Both, 0, GestureAction.SingleSqueeze, 5), Ct)).IsSuccess);
            Assert.Equal(Hex("0A 05"), device.Last(0xF1));
            Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Both, 0, GestureAction.SingleSqueeze, 1), Ct)).Status);

            Assert.True((await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct)).IsSuccess);
            Assert.Equal(Hex("01"), device.Last(0x09));
            Assert.True(store.Current.Toggles[DeviceToggle.GamingMode].Value);

            Assert.True((await controller.SetEqualizerPresetAsync("edifier-eq-3", Ct)).IsSuccess);
            Assert.Equal(Hex("03"), device.Last(0xC4));
            Assert.Equal("edifier-eq-3", store.Current.Equalizer?.Value.ActivePresetId);
        });
    }

    [Fact]
    public async Task W820nb_WritesTheHeadsetIgnoresAreNotReportedAsApplied()
    {
        var device = new FakeEdifier(Hex("02 06")) { IgnoreWrites = true };
        await RunAsync("EDIFIER W820NB", device, false, async (controller, store) =>
        {
            // The plain W820NB has no button control or sound effect: neither is queried.
            Assert.DoesNotContain(device.Requests, r => r.Command is 0xF0 or 0xD5);
            Assert.Equal(CommandStatus.Rejected, (await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct)).Status);
            Assert.False(store.Current.Toggles[DeviceToggle.GamingMode].Value);
            Assert.Equal(CommandStatus.Rejected, (await controller.SetOptionAsync(EdifierDeviceController.PowerOffOption, 30, Ct)).Status);
            Assert.Equal(0, Timer(store));
            Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("edifier-eq-0", Ct)).Status);
        });
    }

    [Fact]
    public async Task W200btPlus_NeverAsksForNoiseControl()
    {
        var device = new FakeEdifier(Hex("01 06")) { SoundEffect = 0 };
        await RunAsync("EDIFIER W200BT Plus", device, false, (controller, store) =>
        {
            Assert.DoesNotContain(device.Requests, r => r.Command is 0xCC or 0xC1 or 0x08 or 0xF0);
            Assert.Equal(0, Timer(store));
            Assert.Equal("edifier-eq-0", store.Current.Equalizer?.Value.ActivePresetId);
            Assert.Empty(store.Current.Profile!.NoiseControlOptions);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task W860nbPro_SpeaksXorPayloadsAndConfirmsWindNoise()
    {
        var device = new FakeEdifier(Hex("10 05"), version2: true, xor: true) { Battery = [60] };
        await RunAsync("EDIFIER W860NB Pro", device, false, async (controller, store) =>
        {
            Assert.Equal(60, store.Current.Battery.Left?.Value.Level);
            Assert.Equal(new NoiseControlState(NoiseControlMode.Off), store.Current.NoiseControl?.Value);
            Assert.Contains(store.Current.Profile!.NoiseControlOptions, o => o.LevelId == EdifierSettings.WindLevel);
            Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, EdifierSettings.WindLevel), Ct)).IsSuccess);
            Assert.Equal(Hex("10 03"), device.Last(0xC1));
            Assert.Contains(device.Written, w => w.SequenceEqual(Hex("AA EC C1 00 02 B5 A6 B4")));
            Assert.Equal(EdifierSettings.WindLevel, store.Current.NoiseControl?.Value.LevelId);
        });
    }

    [Fact]
    public async Task EvoPro_ReadsTheCapturedStateAndOffersWindNoise()
    {
        var device = new FakeEdifier(Hex("1B 06"), version2: true, xor: true) { State = Hex("03 64 62 00 03 11") };
        await RunAsync("漫步者・花再 Evo Pro", device, false, async (controller, store) =>
        {
            Assert.Equal(100, store.Current.Battery.Left?.Value.Level);
            Assert.Equal(98, store.Current.Battery.Right?.Value.Level);
            Assert.Null(store.Current.Battery.Case);
            Assert.Equal(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
            Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, EdifierSettings.WindLevel), Ct)).IsSuccess);
            Assert.Contains(device.Written, w => w.SequenceEqual(Hex("AA EC C1 00 02 BE A1 B8")));
            Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
            Assert.Equal(Hex("1B 05"), device.Last(0xC1));
        });
    }

    [Fact]
    public async Task FitBudsTurbo_UsesPlaintextPayloadsAndGameMode()
    {
        var device = new FakeEdifier(Hex("1B 06"), version2: true) { State = Hex("03 64 64 00 03 11") };
        await RunAsync("EDIFIER FitBuds Turbo", device, false, async (controller, store) =>
        {
            Assert.Equal(100, store.Current.Battery.Right?.Value.Level);
            Assert.Contains(DeviceToggle.GamingMode, Assert.IsType<EdifierProfile>(store.Current.Profile).Toggles);
            Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);
            Assert.Contains(device.Written, w => w.SequenceEqual(Hex("AA EC C1 00 02 1B 01 75")));
            Assert.True((await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct)).IsSuccess);
        });
    }

    [Fact]
    public async Task FitClipUltra_NeverSendsNoiseControl()
    {
        var device = new FakeEdifier(Hex("1B 06"), version2: true, xor: true) { State = Hex("03 50 4B 00 03") };
        await RunAsync("EDIFIER FitClip Ultra", device, false, async (controller, store) =>
        {
            Assert.Equal(80, store.Current.Battery.Left?.Value.Level);
            Assert.Contains(DeviceToggle.GamingMode, Assert.IsType<EdifierProfile>(store.Current.Profile).Toggles);
            Assert.Empty(store.Current.Profile!.NoiseControlOptions);
            Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
            Assert.Equal(CommandStatus.Success, (await controller.RefreshAsync(Ct)).Status);
            Assert.DoesNotContain(device.Requests, r => r.Command is 0xCC or 0xC1);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task X5Pro_KeepsSettingsWithoutReplySamplesExperimental(bool allowExperimental)
    {
        var device = new FakeEdifier(Hex("17 03"), version2: true, xor: true) { State = Hex("03 5A 55 46 01") };
        await RunAsync("EDIFIER X5 Pro", device, allowExperimental, async (controller, store) =>
        {
            Assert.Equal(70, store.Current.Battery.Case?.Value.Level);
            Assert.Equal(new NoiseControlState(NoiseControlMode.Off), store.Current.NoiseControl?.Value);
            var profile = Assert.IsType<EdifierProfile>(store.Current.Profile);
            Assert.True(profile.Confirmed.HasFlag(EdifierFeatures.InEarSwitch));
            Assert.Equal(CapabilitySupport.Experimental, profile.Capabilities.Get(DeviceCapability.InEarDetection).Support);
            var result = await controller.SetToggleAsync(DeviceToggle.InEarDetection, false, Ct);
            if (allowExperimental)
            {
                Assert.Equal([DeviceToggle.GamingMode, DeviceToggle.InEarDetection], profile.Toggles);
                Assert.True(result.IsSuccess);
                Assert.Equal(Hex("00"), device.Last(0xFC));
            }
            else
            {
                Assert.Empty(profile.Toggles);
                Assert.Equal(CommandStatus.Unsupported, result.Status);
                Assert.DoesNotContain(device.Requests, r => r.Command == 0xFC);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V1HeadsetOnBesFraming_FallsBackToBatteryOnly(bool xor)
    {
        // Matched as a W820NB variant but ignoring every v1 frame, like the W820NB 三金标版 diagnostics.
        var device = new FakeEdifier(Hex("02 06"), version2: true, xor: xor) { Battery = [70] };
        await RunAsync("EDIFIER W820NB三金标版", device, true, async (controller, store) =>
        {
            Assert.Equal(70, store.Current.Battery.Left?.Value.Level);
            var profile = Assert.IsType<EdifierProfile>(store.Current.Profile);
            Assert.Equal("edifier-w820nb", profile.ModelId);
            Assert.Empty(profile.NoiseControlOptions);
            Assert.Empty(profile.Toggles);
            Assert.Null(store.Current.NoiseControl);
            Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
            Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct)).Status);
            Assert.Equal(CommandStatus.Success, (await controller.RefreshAsync(Ct)).Status);
            // Over v2 only the read-only battery query is ever sent.
            Assert.All(device.Requests, r => Assert.Equal(0xD0, r.Command));
        });
    }

    [Fact]
    public async Task RawTrace_LogsKnownPayloadsButOnlyTheLengthOfOthers()
    {
        var device = new FakeEdifier(Hex("02 06"));
        var trace = new RecordingTrace();
        var identity = Device("EDIFIER W820NB");
        var profile = (IControllableProfile)new DeviceProfileRegistry(EdifierProfiles.CreateBuiltIn()).Match(identity)!;
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var context = new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System, trace);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var emulator = Task.Run(() => device.RunAsync(stop.Token), Ct);
        await using var controller = profile.CreateController(device.Channel, context);
        try
        {
            Assert.True(await controller.InitializeAsync(Ct));
            // An unsolicited frame with a command outside the known set (e.g. a name) and stray bytes.
            device.Channel.Receive(EdifierFrameCodec.Encode(EdifierConstants.ResponseHeadBb, 0xC9, "Secret"u8));
            device.Channel.Receive([0x01, 0x02, 0x03, 0x04]);
            await Task.Delay(100, Ct);
            Assert.Contains("TX EDIFIER v1 D0 ", trace.Lines);
            Assert.Contains("RX EDIFIER v1 D0 50", trace.Lines);
            Assert.Contains("RX EDIFIER v1 CC 0206", trace.Lines);
            Assert.Contains("RX EDIFIER v1 C9 <6 bytes>", trace.Lines);
            Assert.Contains("RX EDIFIER no complete frame in 4 bytes, head 010203", trace.Lines);
            Assert.DoesNotContain(trace.Lines, l => l.Contains("536563726574", StringComparison.Ordinal));
        }
        finally
        {
            await stop.CancelAsync();
            await emulator;
        }
    }

    private sealed class RecordingTrace : EncoDeck.Protocol.Oppo.IProtocolTrace
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return [.. _lines]; }
        }

        public void OnFrame(EncoDeck.Protocol.Oppo.FrameDirection direction, EncoDeck.Protocol.Oppo.OppoFrame frame, DateTimeOffset timestamp)
        {
        }

        public void OnRawReceived(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
        {
        }

        public void OnBrandFrame(EncoDeck.Protocol.Oppo.FrameDirection direction, string summary, DateTimeOffset timestamp)
        {
            lock (_lines) _lines.Add($"{(direction == EncoDeck.Protocol.Oppo.FrameDirection.Sent ? "TX" : "RX")} {summary}");
        }
    }

    [Fact]
    public async Task W820nb_SilentGameModeAndTimerDoNotHoldBackBatteryOrNoise()
    {
        // A variant that never answers game mode (0x08) or the timer (0xD3): the session is ready with
        // battery and noise control first; the two settings stay hidden once their reads time out.
        var device = new FakeEdifier(Hex("02 06")) { Silent = [0x08, 0xD3] };
        var identity = Device("EDIFIER W820NB");
        var profile = (IControllableProfile)new DeviceProfileRegistry(EdifierProfiles.CreateBuiltIn()).Match(identity)!;
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var context = new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var emulator = Task.Run(() => device.RunAsync(stop.Token), Ct);
        await using var controller = (EdifierDeviceController)profile.CreateController(device.Channel, context);
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(await controller.InitializeAsync(Ct));
            Assert.True(started.Elapsed < EdifierDeviceController.RequestTimeout, $"Session took {started.Elapsed}.");
            Assert.NotNull(store.Current.Battery);
            Assert.NotNull(store.Current.NoiseControl);

            await controller.DeferredSettingsRead.WaitAsync(Ct);
            Assert.Null(Timer(store));
            Assert.False(store.Current.Toggles.ContainsKey(DeviceToggle.GamingMode));
            Assert.DoesNotContain(DeviceToggle.GamingMode, ((IControllableProfile)store.Current.Profile!).Toggles);
        }
        finally
        {
            await stop.CancelAsync();
            await emulator;
        }
    }

    /// <summary>The power-off timer the headset reported, in minutes (a shared device option).</summary>
    private static int? Timer(DeviceStateStore store) =>
        store.Current.Options.TryGetValue(EdifierDeviceController.PowerOffOption, out var option) ? option.Value.Value : null;

    [Fact]
    public void PowerOffTimer_IsOfferedAsADeviceOption()
    {
        var option = EdifierDeviceController.PowerOffTimerOption(60);
        Assert.Equal(new[] { 0, 5, 15, 30, 60, 180 }, option.Choices.Select(c => c.Value));
        Assert.Equal(("PowerOff_Hours", "1"), (option.Choices[4].LabelKey, option.Choices[4].Argument));
        Assert.True(option.Accepts(180));
        // A reported value outside the official list is shown as reported.
        Assert.Contains(EdifierDeviceController.PowerOffTimerOption(45).Choices, c => c.Value == 45 && c.LabelKey == "PowerOff_Minutes");
    }

    private static async Task RunAsync(string name, FakeEdifier device, bool allowExperimental,
        Func<IDeviceController, DeviceStateStore, Task> assertions)
    {
        var identity = Device(name);
        var profile = (IControllableProfile)new DeviceProfileRegistry(EdifierProfiles.CreateBuiltIn()).Match(identity)!;
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var context = new ControllerContext(identity, store, () => allowExperimental, NullLoggerFactory.Instance, TimeProvider.System);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var emulator = Task.Run(() => device.RunAsync(stop.Token), Ct);
        await using var controller = profile.CreateController(device.Channel, context);
        try
        {
            Assert.True(await controller.InitializeAsync(Ct));
            // Game mode and the power-off timer are read in the background after the session is ready.
            if (controller is EdifierDeviceController edifier) await edifier.DeferredSettingsRead.WaitAsync(Ct);
            await assertions(controller, store);
        }
        finally
        {
            await stop.CancelAsync();
            await emulator;
        }
    }

    /// <summary>
    /// An EDIFIER headset that answers like the captured sessions: queries report the current value,
    /// writes are echoed (or acknowledged with a <c>CC</c> head) and applied unless told to ignore them.
    /// </summary>
    private sealed class FakeEdifier(byte[] noise, bool version2 = false, bool xor = false)
    {
        private readonly List<(byte Command, byte[] Payload)> _requests = [];
        private byte[] _noise = noise;
        private byte[]? _timer;
        private byte _game, _inEar = 1;

        public FakeByteChannel Channel { get; } = new();

        public bool IgnoreWrites { get; init; }

        /// <summary>Commands this variant never answers.</summary>
        public byte[] Silent { get; init; } = [];

        public byte[] Battery { get; init; } = [80];

        public byte[] State { get; init; } = [3, 80, 70, 60];

        public byte KeyMask { get; set; } = 7;

        public byte SoundEffect { get; set; }

        public IReadOnlyList<(byte Command, byte[] Payload)> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        public IReadOnlyList<byte[]> Written => Channel.Written;

        public byte[] Last(byte command) => Requests.Last(r => r.Command == command).Payload;

        public async Task RunAsync(CancellationToken ct)
        {
            int handled = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var writes = Channel.Written;
                    for (; handled < writes.Count; handled++)
                    {
                        // A request in the other framing goes unanswered, as on the headset.
                        if (Parse(writes[handled]) is not (var command, var payload)) continue;
                        lock (_requests) _requests.Add((command, payload));
                        if (!Silent.Contains(command) && Answer(command, payload) is { } reply) Channel.Receive(Encode(reply.Head, command, reply.Payload));
                    }
                    await Task.Delay(5, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        private (byte Command, byte[] Payload)? Parse(byte[] request)
        {
            bool isVersion2 = request[1] == EdifierConstants.AppCode;
            if (isVersion2 != version2) return null;
            if (!version2) return (request[2], request[3..^2]);
            byte[] payload = request[5..^1];
            if (xor) EdifierFrameCodec.Xor(payload);
            return (request[2], payload);
        }

        private byte[] Encode(byte head, byte command, byte[] payload)
        {
            if (!version2) return EdifierFrameCodec.Encode(head, command, payload);
            byte[] sent = [.. payload];
            if (xor) EdifierFrameCodec.Xor(sent);
            return EdifierFrameCodec.EncodeV2(command, sent, head);
        }

        private (byte Head, byte[] Payload)? Answer(byte command, byte[] p)
        {
            const byte bb = EdifierConstants.ResponseHeadBb, cc = EdifierConstants.ResponseHeadCc;
            switch (command)
            {
                case 0xD0: return (bb, Battery);
                case 0xF2: return (bb, State);
                case 0xCC: return (bb, _noise);
                case 0xC1:
                    if (!IgnoreWrites) _noise = version2 ? p : p.Length == 2 ? p : [p[0], _noise.Length > 1 ? _noise[1] : (byte)6];
                    return (bb, p);
                case 0x08: return (bb, [_game]);
                case 0x09:
                    if (!IgnoreWrites) _game = p[0];
                    return (bb, p);
                case 0xFB: return (bb, [_inEar]);
                case 0xFC:
                    if (!IgnoreWrites) _inEar = p[0];
                    return (bb, p);
                case 0xF0: return (bb, [0x0A, KeyMask]);
                case 0xF1:
                    if (!IgnoreWrites) KeyMask = p[1];
                    return (bb, p);
                case 0xD5: return (bb, [SoundEffect]);
                case 0xC4:
                    if (!IgnoreWrites) SoundEffect = p[0];
                    return (cc, p);
                case 0xD3: return (bb, _timer ?? [0]);
                case 0xD1:
                    if (!IgnoreWrites) _timer = p;
                    return (cc, [1]);
                case 0xD2:
                    if (!IgnoreWrites) _timer = null;
                    return (cc, [1]);
                default: return null;
            }
        }
    }
}
