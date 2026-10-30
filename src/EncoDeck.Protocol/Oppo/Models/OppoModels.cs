// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Oppo.Models;

/// <summary>
/// Built-in model definitions. Every value follows the official client's implementation for the
/// model (its per-model configuration plus the code path for each feature); where EncoDeck also
/// verified a feature on real hardware the evidence says so. See docs/COMPATIBILITY.md.
/// </summary>
public static class OppoModels
{
    private const string X4Hw = "Verified on Enco X4 (fw 116/116/100)";
    private const string X4Official = "Official client implementation for 06EC10";

    public static OppoModelDefinition EncoX4 { get; } = new()
    {
        ModelId = "oppo-enco-x4",
        SupportsLeAudioPairing = true,
        // Verified on fw 130: 0x0404 01 01 … answers 01 over the LE control channel once in LE mode.
        RejectsNoiseChangeOnLeAudio = true,
        DisplayName = "OPPO Enco X4",
        ImageKey = "enco-x4",
        ProductIds = [0x06EC10],
        // The official configuration also lists the platform alias "OPPO Enco Bohr".
        BluetoothNames = ["OPPO Enco X4", "OPPO Enco Bohr"],
        // SDP on the X4 exposes only 0000079A-… ("ELNK", RFCOMM channel 15).
        ServiceUuids = [OppoServiceUuids.Secondary],
        Capabilities = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, X4Hw + ": 0x0106 query and 0x0204/01 events.")
            .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Supported, X4Hw + ": bit 7 of each battery entry.")
            .Set(DeviceCapability.NoiseCancellation, CapabilitySupport.Supported, X4Hw + ": 0x0404 set, 0x8404 ack, 0x010C read-back, 0x0204/03 events.")
            .Set(DeviceCapability.NoiseCancellationLevel, CapabilitySupport.Supported, X4Hw + ": High/Low written and read back; " + X4Official + " for Moderate/Auto.")
            .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Supported, X4Hw + ": Adaptive/Standard written and read back; " + X4Official + " for Less/More noise.")
            .Set(DeviceCapability.FirmwareInformation, CapabilitySupport.Supported, X4Hw + ": 0x0105 version CSV.")
            .Set(DeviceCapability.Equalizer, CapabilitySupport.Supported, X4Official + ": 0x0406 preset + 0x010F read-back; presets observed on hardware.")
            .Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Supported, X4Official + ": 0x0418 add/select/edit/delete + 0x0122 read-back; list read on hardware.")
            .Set(DeviceCapability.SpatialAudio, CapabilitySupport.Supported, X4Official + ": 0x012A / 0x0422 (off, fixed, head tracking).")
            .Set(DeviceCapability.HeadTracking, CapabilitySupport.Supported, X4Official + ": spatial mode 2 (follow head).")
            .Set(DeviceCapability.GamingMode, CapabilitySupport.Supported, X4Official + ": master feature 40, low latency feature 6, game sound 0x012B/0x0423.")
            .Set(DeviceCapability.InEarDetection, CapabilitySupport.Supported, X4Official + ": feature 4; wear state verified on hardware.")
            .Set(DeviceCapability.TouchCustomization, CapabilitySupport.Supported, X4Official + ": 0x0401 per earbud + 0x0108 read-back; mapping read on hardware.")
            .Set(DeviceCapability.DualDeviceConnection, CapabilitySupport.Supported, X4Official + ": feature 17, 0x0112 list, 0x0429 operations, 0x0132 preferred device.")
            .Set(DeviceCapability.FindMyEarbuds, CapabilitySupport.Supported, X4Official + ": 0x0400 start/stop, 50 s auto-stop.")
            .Set(DeviceCapability.FitTest, CapabilitySupport.Supported, X4Official + ": 0x0405 start/stop, result event 04 (offered by the X4).")
            .Set(DeviceCapability.PromptVolume, CapabilitySupport.Supported, X4Official + ": 0x0130 read, 0x0427 set (1-10).")
            .Set(DeviceCapability.BassEngine, CapabilitySupport.Supported, X4Official + ": feature 29 switch, 0x0124 read, 0x041B level.")
            .Build(),
        NoiseModeParents = new Dictionary<NoiseControlMode, OppoNoiseParent>
        {
            [NoiseControlMode.Off] = new(0, CapabilitySupport.Supported),
            [NoiseControlMode.NoiseCancellation] = new(1, CapabilitySupport.Supported),
            [NoiseControlMode.Transparency] = new(2, CapabilitySupport.Supported),
            // The official top-level Adaptive button sends its own index 11.
            [NoiseControlMode.Adaptive] = new(11, CapabilitySupport.Supported, DefaultLevelId: "standard"),
        },
        NoiseModes =
        [
            new(new NoiseControlOption(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported), SetIndex: 0, ReportedIndices: [3, 0]),
            new(new NoiseControlOption(NoiseControlMode.NoiseCancellation, "smart", "Auto", "NoiseLevel_Smart", CapabilitySupport.Supported), SetIndex: 7, ReportedIndices: [7]),
            new(new NoiseControlOption(NoiseControlMode.NoiseCancellation, "deep", "High", "NoiseLevel_Deep", CapabilitySupport.Supported), SetIndex: 4, ReportedIndices: [4]),
            new(new NoiseControlOption(NoiseControlMode.NoiseCancellation, "medium", "Moderate", "NoiseLevel_Medium", CapabilitySupport.Supported), SetIndex: 5, ReportedIndices: [5]),
            new(new NoiseControlOption(NoiseControlMode.NoiseCancellation, "light", "Low", "NoiseLevel_Light", CapabilitySupport.Supported), SetIndex: 6, ReportedIndices: [6]),
            new(new NoiseControlOption(NoiseControlMode.Adaptive, "less", "Less noise", "NoiseLevel_AdaptiveLess", CapabilitySupport.Supported), SetIndex: 12, ReportedIndices: [12]),
            new(new NoiseControlOption(NoiseControlMode.Adaptive, "standard", "Standard", "NoiseLevel_AdaptiveStandard", CapabilitySupport.Supported), SetIndex: 13, ReportedIndices: [13, 11]),
            new(new NoiseControlOption(NoiseControlMode.Adaptive, "more", "More noise", "NoiseLevel_AdaptiveMore", CapabilitySupport.Supported), SetIndex: 14, ReportedIndices: [14]),
            new(new NoiseControlOption(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported), SetIndex: 2, ReportedIndices: [8, 2]),
        ],
        // UI v2 order: noise cancelling, adaptive, transparency, off.
        NoiseCycleIndices = [1, 11, 2, 0],
        EqualizerPresets = new Dictionary<byte, EqualizerPreset>
        {
            [0] = new("dynaudio-original", "Dynaudio original sound", "Eq_DynaudioOriginal", CapabilitySupport.Supported),
            [7] = new("dynaudio-hires", "Dynaudio high-resolution", "Eq_DynaudioHiRes", CapabilitySupport.Supported),
            [2] = new("pure-vocals", "Pure vocals", "Eq_PureVocals", CapabilitySupport.Supported),
            [1] = new("crisp-highs", "Crisp highs", "Eq_CrispHighs", CapabilitySupport.Supported),
            [3] = new("thundering-bass", "Thundering bass", "Eq_ThunderingBass", CapabilitySupport.Supported),
        },
        CustomEqualizer = new OppoCustomEqualizerSpec([31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000], -6, 6, MaxPresets: 3),
        FeatureSwitches =
        [
            new(DeviceToggle.GamingMode, 0x28, CapabilitySupport.Supported),
            new(DeviceToggle.LowLatency, 0x06, CapabilitySupport.Supported),
            new(DeviceToggle.InEarDetection, 0x04, CapabilitySupport.Supported),
            new(DeviceToggle.DualDeviceConnection, 0x11, CapabilitySupport.Supported),
            new(DeviceToggle.SleepDetection, 0x3A, CapabilitySupport.Supported),
            new(DeviceToggle.SwiftPair, 0x37, CapabilitySupport.Supported),
            new(DeviceToggle.VoiceCommands, 0x19, CapabilitySupport.Supported),
            new(DeviceToggle.HeadGestures, 0x3B, CapabilitySupport.Supported),
            new(DeviceToggle.BassEngine, 0x1D, CapabilitySupport.Supported),
            // Read only on Windows (LHDC is not available); needed for the spatial-audio mutex.
            new(DeviceToggle.HiRes, 0x18, CapabilitySupport.Supported),
        ],
        // The official client's 24-id status query for this configuration.
        FeatureStatusIds = [0x05, 0x04, 0x0B, 0x0F, 0x11, 0x13, 0x18, 0x19, 0x06, 0x1B, 0x1D, 0x1C, 0x1E, 0x22, 0x23, 0x24, 0x3C, 0x27, 0x28, 0x32, 0x37, 0x3B, 0x3D, 0x3A],
        GestureRows =
        [
            new(GestureAction.SingleSqueeze, false, [0, 1], "Gesture_Single"),
            new(GestureAction.DoubleSqueeze, false, [0, 1, 5, 6, 3, 17], "Gesture_Double"),
            // Zen (18) is filtered by the official client for zenMode 3.
            new(GestureAction.TripleSqueeze, false, [0, 5, 6, 3, 17], "Gesture_Triple"),
            new(GestureAction.Slide, false, [0, 7, 10], "Gesture_Slide"),
            // longPressType 0x2281 → none, noise control, voice assistant, game mode.
            new(GestureAction.SqueezeAndHold, false, [0, 8, 3, 17], "Gesture_Hold") { EditsNoiseCycle = true },
            new(GestureAction.SingleSqueeze, true, [0, 29], "Gesture_CallSingle") { DefaultFunction = 29 },
            new(GestureAction.DoubleSqueeze, true, [0, 28], "Gesture_CallDouble") { DefaultFunction = 28 },
        ],
        NoiseCycleLabels = new Dictionary<int, string>
        {
            [1] = "NoiseMode_NoiseCancellation",
            [11] = "NoiseMode_Adaptive",
            [2] = "NoiseMode_Transparency",
            [0] = "NoiseMode_Off",
        },
        GameSoundTypes = [3, 0],
        SpatialTypes = [0, 1, 2],
        HasSpatialAudioV2 = true,
        SpatialHiResMutex = true,
        TimeReplyIncludesOffset = true,
    };

    /// <summary>
    /// Fallback for OPPO-protocol devices that are not in the official client's model list either:
    /// battery, charging and firmware are read with the commands every OPPO model shares; without
    /// the model's configuration there is nothing to base a setting on, so nothing is written.
    /// </summary>
    public static OppoModelDefinition GenericOppo { get; } = new()
    {
        ModelId = "oppo-generic",
        DisplayName = "OPPO earbuds (unverified model)",
        ImageKey = "generic",
        IsGeneric = true,
        Capabilities = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, "Shared OPPO battery query 0x0106 (read only).")
            .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Supported, "Shared OPPO battery query 0x0106 (read only).")
            .Set(DeviceCapability.FirmwareInformation, CapabilitySupport.Supported, "Shared OPPO version query 0x0105 (read only).")
            .Build(),
        SubscribedEvents = [OppoEventCode.Battery],
    };

    /// <summary>
    /// Every model the official client knows: the hand-made, hardware-verified Enco X4 plus the
    /// definitions built from the official configuration (<see cref="OppoCatalog"/>).
    /// </summary>
    public static IReadOnlyList<OppoModelDefinition> All { get; } =
        [EncoX4, .. OppoCatalog.Definitions.Where(d => !d.ProductIds.Any(EncoX4.ProductIds.Contains))];

    public static OppoModelDefinition? FindByProductId(uint productId) =>
        All.FirstOrDefault(m => m.ProductIds.Contains(productId));
}
