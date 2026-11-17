// SPDX-License-Identifier: GPL-3.0-only
// Protocol facts from the independent Android app "我的X5s Pro" by shui-tiao
// (https://github.com/shui-tiao/my-x5s-pro, GPL-3.0), verified on a
// HONOR CHOICE Earbuds X5s Pro (Wingcloud / USFY, firmware 1.1.64.2).
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Wingcloud;

/// <summary>Built-in Wingcloud profiles. Only the USFY model is verified, so only it is matched.</summary>
public static class WingcloudProfiles
{
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() => [new WingcloudX5sProProfile()];
}

/// <summary>
/// HONOR CHOICE Earbuds X5s Pro. Everything is offered only after this connection read it back:
/// battery and firmware from <c>2B 39</c> / <c>2B 0A</c>, noise control from <c>2B 2A</c>, EQ and the
/// LDAC / spatial switches from the <c>01 3A</c> property channel, gestures from service 4 / 5.
/// </summary>
public sealed class WingcloudX5sProProfile : IControllableProfile, IBrandGestureProfile
{
    private const string Evidence = "Independent Android implementation (我的X5s Pro) verified on X5s Pro (USFY), firmware 1.1.64.2.";

    public const string Id = "wingcloud-x5s-pro";

    internal WingcloudX5sProProfile(
        bool noiseConfirmed = false,
        bool eqConfirmed = false,
        bool qualityConfirmed = false,
        bool gesturesConfirmed = false,
        bool cycleConfirmed = false)
    {
        NoiseConfirmed = noiseConfirmed;
        EqConfirmed = eqConfirmed;
        QualityConfirmed = qualityConfirmed;
        GesturesConfirmed = gesturesConfirmed;
        CycleConfirmed = cycleConfirmed;
    }

    internal bool NoiseConfirmed { get; }
    internal bool EqConfirmed { get; }
    internal bool QualityConfirmed { get; }
    internal bool GesturesConfirmed { get; }
    internal bool CycleConfirmed { get; }

    internal WingcloudX5sProProfile With(
        bool noise = false, bool eq = false, bool quality = false, bool gestures = false, bool cycle = false) =>
        new(noise || NoiseConfirmed, eq || EqConfirmed, quality || QualityConfirmed,
            gestures || GesturesConfirmed, cycle || CycleConfirmed);

    public string Brand => "wingcloud";

    public bool IsGeneric => false;

    public string ModelId => Id;

    public string DisplayName => "HONOR CHOICE Earbuds X5s Pro";

    public string ImageKey => Id;

    public DeviceForm Form => DeviceForm.Earbuds;

    /// <summary>Any of the three services the phone app falls back to; the SPP UUID is tried first.</summary>
    public IReadOnlyList<Guid> ControlServiceUuids =>
    [
        new("00001101-0000-1000-8000-00805f9b34fb"),
        new("82ff3820-8411-400c-b85a-55bdb32cf060"),
        new("c9770a18-4c3d-453a-8aaf-d7ec7bbd2785"),
    ];

    /// <summary>Control runs over Classic SPP; whether audio uses LE Audio is Windows' decision.</summary>
    public bool ManagesLeAudioLink => false;

    /// <summary>LDAC and surround sound are wire settings; gaming mode and dual connection are not in the app.</summary>
    public IReadOnlyCollection<DeviceToggle> Toggles => [DeviceToggle.Ldac, DeviceToggle.SpatialAudio];

    public IDeviceCapabilities Capabilities => DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, Evidence)
        // The X5s Pro information frame never carries a charging bit; nothing is claimed.
        .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Unsupported, "Not reported by the X5s Pro information frame.")
        .Set(DeviceCapability.NoiseCancellation, NoiseConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown, Evidence)
        .Set(DeviceCapability.NoiseCancellationLevel, NoiseConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown, Evidence)
        .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Unsupported, "The intelligent level is a noise-cancelling level, not a separate mode.")
        .Set(DeviceCapability.Equalizer, EqConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown, Evidence)
        .Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Unsupported, "The firmware has three fixed presets.")
        .Set(DeviceCapability.SpatialAudio, QualityConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown, Evidence)
        .Set(DeviceCapability.HeadTracking, CapabilitySupport.Unsupported, "Surround sound is fixed, without head tracking.")
        .Set(DeviceCapability.TouchCustomization, GesturesConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown, Evidence)
        .Set(DeviceCapability.FirmwareInformation, CapabilitySupport.Supported, Evidence)
        .Build();

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => !NoiseConfirmed ? [] : Options;

    private static readonly IReadOnlyList<NoiseControlOption> Options =
    [
        new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
        new(NoiseControlMode.NoiseCancellation, "dynamic", "Intelligent", "Honor_Anc_Dynamic", CapabilitySupport.Supported),
        new(NoiseControlMode.NoiseCancellation, "cozy", "Light", "Honor_Anc_Cozy", CapabilitySupport.Supported),
        new(NoiseControlMode.NoiseCancellation, "general", "Moderate", "Honor_Anc_General", CapabilitySupport.Supported),
        new(NoiseControlMode.NoiseCancellation, "ultra", "Deep", "Honor_Anc_Ultra", CapabilitySupport.Supported),
        new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported),
    ];

    /// <summary>Wire values are the firmware enum (0 standard, 2 bass boost, 100 treble boost); 1 is not a preset.</summary>
    internal static readonly (int Value, string Name, string Key)[] EqNames =
    [
        (0, "Standard", "Wingcloud_Eq_Standard"),
        (2, "Bass boost", "Wingcloud_Eq_Bass"),
        (100, "Treble boost", "Wingcloud_Eq_Treble"),
    ];

    public IReadOnlyList<EqualizerPreset> EqualizerPresets => EqConfirmed
        ? [.. EqNames.Select(p => new EqualizerPreset(p.Value.ToString(), p.Name, p.Key, CapabilitySupport.Supported))]
        : [];

    public IReadOnlyList<BrandGestureSlot> GestureSlots => GesturesConfirmed ? WingcloudGestures.Slots : [];

    public int MatchScore(DeviceIdentity identity)
    {
        string name = WingcloudName.Normalize(identity.Name);
        return name.Length > 0 && WingcloudGestures.Names.Any(a => name == WingcloudName.Normalize(a)) ? 100 : 0;
    }

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new WingcloudDeviceController(channel, context, this);

    public override string ToString() => ModelId;
}

internal static class WingcloudName
{
    public static string Normalize(string? value) =>
        new((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
