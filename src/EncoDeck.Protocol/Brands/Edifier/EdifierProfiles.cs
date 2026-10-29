// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Per-model feature gating follows mEDIFIER Qt/devices/deviceinfo.json (https://github.com/wh201906/mEDIFIER,
// 10056b5, MIT, Copyright (c) 2023 Self Not Found; MIT notice in THIRD_PARTY_NOTICES.md) and the
// device records of HyperEars (https://github.com/silverpoetry/HyperEars, 361d279, GPL-3.0-only).
using System.Text;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Edifier;

/// <summary>Built-in EDIFIER profiles: known SPP models plus a name-only family fallback.</summary>
public static partial class EdifierProfiles
{
    private const EdifierFeatures W820Features =
        EdifierFeatures.Noise | EdifierFeatures.AmbientLevel | EdifierFeatures.GameMode | EdifierFeatures.PowerOffTimer;

    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
    [
        // mEDIFIER: tested on W820NB (no sound effect / button control); Macifier: W820NB hardware test.
        new EdifierProfile("edifier-w820nb", "EDIFIER W820NB", "w820nb", isGeneric: false,
            spec: EdifierSpec.Headphones(W820Features, "W820NB: mEDIFIER and Macifier hardware tests (noise, ambient level, game mode, power-off timer)")),
        // mEDIFIER capture doc/KnownCommands/W820NB_Double_Gold.txt; HyperEars PR #50 real-device V1 record.
        new EdifierProfile("edifier-w820nb-double-gold", "EDIFIER W820NB Double Gold", "w820nb双金标", isGeneric: false,
            spec: EdifierSpec.Headphones(W820Features | EdifierFeatures.KeyControl | EdifierFeatures.SoundEffect,
                "W820NB Double Gold: mEDIFIER frame capture and HyperEars PR #50 real-device record",
                aliases: ["w820nb双金标版", "w820nbdoublegold"])),
        new EdifierProfile("edifier-w200bt", "EDIFIER W200BT", "w200bt", isGeneric: false),
        // mEDIFIER deviceinfo.json hides noise, ambient, button control and game mode on W200BT Plus.
        new EdifierProfile("edifier-w200bt-plus", "EDIFIER W200BT Plus", "w200btplus", isGeneric: false,
            spec: EdifierSpec.Headphones(EdifierFeatures.PowerOffTimer | EdifierFeatures.SoundEffect,
                "W200BT Plus: mEDIFIER hardware test (sound effect, power-off timer); no noise control", noise: EdifierAnc.None)),
        // HyperEars: W860NB PRO verified on real hardware (BES v2, XOR payloads, noise slot 0x10).
        new EdifierProfile("edifier-w860nb-pro", "EDIFIER W860NB PRO", "w860nbpro", isGeneric: false,
            spec: new EdifierSpec(EdifierFraming.V2Xor, StateBattery: false, EdifierAnc.SlotW860, EdifierFeatures.Noise,
                Evidence: "W860NB PRO: HyperEars real-device verification (battery, noise slot 0x10)")),
        .. CreateTwsProfiles(),
        new EdifierProfile("edifier", "EDIFIER headphones", "edifier", isGeneric: true),
    ];

    /// <summary>Lower-case letters and digits only, so "W820NB Plus" becomes "w820nbplus".</summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>How a model frames its requests.</summary>
internal enum EdifierFraming
{
    /// <summary><c>AA len cmd … crc16</c>, plaintext.</summary>
    V1,

    /// <summary>BES v2 <c>AA EC cmd lenHi lenLo … sum8</c>, plaintext payloads.</summary>
    V2,

    /// <summary>BES v2 with XOR 0xA5 payloads.</summary>
    V2Xor,

    /// <summary>Try v2 with a read-only state query, fall back to v1; XOR detected from the answer.</summary>
    Negotiate,
}

/// <summary>Noise-control dialect: how <c>0xCC</c> / <c>0xC1</c> payloads are laid out.</summary>
internal enum EdifierAnc
{
    /// <summary>No noise control; <c>0xCC</c> is never sent (FitClip Ultra drops the link on it).</summary>
    None,

    /// <summary>V1 <c>[mode][ambient level]</c>: 1 standard (off), 2 noise cancelling, 3 ambient.</summary>
    V1,

    /// <summary>Unknown TWS, or a v1 headset found on v2 framing: V1 modes, queried only over v1 framing.</summary>
    Probe,

    /// <summary><c>[10][value]</c>: 1 deep, 2 comfort, 3 wind, 4 ambient, 5 off (W860NB PRO).</summary>
    SlotW860,

    /// <summary><c>[1B][value]</c>: 1–3 noise cancelling, 4 wind, 5 ambient, 6 off (花再 Evo Pro, FitBuds Turbo).</summary>
    SlotEvo,

    /// <summary><c>[17][value]</c>: 1 noise cancelling, 2 ambient, 3 off (X5 Pro).</summary>
    SlotX5,
}

/// <summary>Model settings beyond battery; each is offered only after the headset answers its query.</summary>
[Flags]
public enum EdifierFeatures
{
    None = 0,
    Noise = 1,
    AmbientLevel = 2,
    KeyControl = 4,
    GameMode = 8,
    PowerOffTimer = 16,
    InEarSwitch = 32,
    SoundEffect = 64,
}

/// <summary>What a model is known to speak, and with what evidence.</summary>
/// <param name="Experimental">Features without reply samples: offered after read-back only while experimental features are on.</param>
/// <param name="RequireBrand">Only match names carrying "EDIFIER" / "漫步者" (the bare model name is used by other brands).</param>
internal sealed record EdifierSpec(
    EdifierFraming Framing,
    bool StateBattery,
    EdifierAnc Anc,
    EdifierFeatures Features,
    EdifierFeatures Experimental = EdifierFeatures.None,
    string Evidence = "EDIFIER v1 protocol (mEDIFIER via OppoPodsManager). No local hardware verification.",
    IReadOnlyList<string>? Aliases = null,
    bool RequireBrand = false)
{
    /// <summary>Noise control that is offered before any read-back (v1 headphones, as before).</summary>
    public bool StaticNoise => Anc == EdifierAnc.V1 && !StateBattery;

    public static EdifierSpec Headphones(EdifierFeatures features, string evidence, IReadOnlyList<string>? aliases = null, EdifierAnc noise = EdifierAnc.V1) =>
        new(EdifierFraming.V1, StateBattery: false, noise, features, Evidence: evidence, Aliases: aliases);

    public static EdifierSpec LegacyHeadphones { get; } = new(EdifierFraming.V1, false, EdifierAnc.V1, EdifierFeatures.Noise);

    public static EdifierSpec LegacyTws { get; } = new(EdifierFraming.Negotiate, true, EdifierAnc.Probe, EdifierFeatures.Noise,
        Evidence: "TWS state from the EDIFIER v2 protocol; noise control only after a v1 read-back. No hardware verification.");

    /// <summary>
    /// A v1 headset that answered only the BES v2 battery query: battery alone. Its noise layout and
    /// settings are unknown, so noise control is never queried or offered (<see cref="EdifierAnc.Probe"/>
    /// only reads it over v1 framing).
    /// </summary>
    public static EdifierSpec BesFallback(EdifierSpec listed) => listed with
    {
        Framing = EdifierFraming.Negotiate,
        Anc = listed.Anc == EdifierAnc.None ? EdifierAnc.None : EdifierAnc.Probe,
        Features = EdifierFeatures.None,
        Experimental = EdifierFeatures.None,
        Evidence = "Battery answered over BES v2 framing, not the v1 framing this model is listed with; noise control and settings are unknown for this variant.",
    };
}

/// <summary>One EDIFIER model (or the generic family).</summary>
public sealed class EdifierProfile : IControllableProfile, IBrandGestureProfile
{
    private readonly string _nameKey;
    private readonly bool _offerExperimental;

    internal EdifierProfile(string modelId, string displayName, string nameKey, bool isGeneric,
        bool isTws = false, bool hasSpp = true, bool noiseConfirmed = false, string? gattService = null,
        EdifierSpec? spec = null, string? alternateGattService = null,
        EdifierFeatures confirmed = EdifierFeatures.None, bool offerExperimental = false)
    {
        ModelId = modelId;
        DisplayName = displayName;
        _nameKey = nameKey;
        IsGeneric = isGeneric;
        IsTws = isTws;
        HasSpp = hasSpp;
        GattService = gattService;
        AlternateGattService = alternateGattService;
        Spec = spec ?? (isTws ? EdifierSpec.LegacyTws : EdifierSpec.LegacyHeadphones);
        Confirmed = noiseConfirmed ? confirmed | EdifierFeatures.Noise : confirmed;
        _offerExperimental = offerExperimental;
        Capabilities = BuildCapabilities();
        NoiseControlOptions = BuildNoiseOptions();
        EqualizerPresets = Confirmed.HasFlag(EdifierFeatures.SoundEffect) ? EdifierSettings.SoundEffects : [];
        GestureSlots = Spec.Features.HasFlag(EdifierFeatures.KeyControl) ? [EdifierSettings.KeyControlSlot] : [];
    }

    public string Brand => "edifier";

    public bool IsGeneric { get; }

    public bool IsTws { get; }

    internal bool HasSpp { get; }

    internal EdifierSpec Spec { get; }

    /// <summary>Settings the headset answered for in this session.</summary>
    public EdifierFeatures Confirmed { get; }

    internal bool NoiseConfirmed => Confirmed.HasFlag(EdifierFeatures.Noise);

    internal string? GattService { get; }

    internal string? AlternateGattService { get; }

    internal EdifierProfile WithNoiseConfirmed(bool confirmed = true) =>
        WithConfirmed(confirmed ? Confirmed | EdifierFeatures.Noise : Confirmed & ~EdifierFeatures.Noise, _offerExperimental);

    internal EdifierProfile WithConfirmed(EdifierFeatures confirmed, bool offerExperimental) =>
        confirmed == Confirmed && offerExperimental == _offerExperimental
            ? this
            : new(ModelId, DisplayName, _nameKey, IsGeneric, IsTws, HasSpp, false, GattService, Spec, AlternateGattService, confirmed, offerExperimental);

    /// <summary>The same model speaking another dialect than listed; nothing is confirmed for it yet.</summary>
    internal EdifierProfile WithSpec(EdifierSpec spec) =>
        new(ModelId, DisplayName, _nameKey, IsGeneric, IsTws, HasSpp, false, GattService, spec, AlternateGattService, EdifierFeatures.None, _offerExperimental);

    /// <summary>Whether a confirmed feature may be offered: always, or for features without reply samples only when opted in.</summary>
    internal bool Offers(EdifierFeatures feature) =>
        Confirmed.HasFlag(feature) && (!Spec.Experimental.HasFlag(feature) || _offerExperimental);

    public string ModelId { get; }

    public string DisplayName { get; }

    public string ImageKey => ModelId;

    public DeviceForm Form => IsTws ? DeviceForm.Earbuds : DeviceForm.Headphones;

    public IDeviceCapabilities Capabilities { get; }

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions { get; }

    public IReadOnlyList<EqualizerPreset> EqualizerPresets { get; }

    public IReadOnlyList<BrandGestureSlot> GestureSlots { get; }

    public IReadOnlyCollection<DeviceToggle> Toggles =>
    [
        .. Offers(EdifierFeatures.GameMode) ? new[] { DeviceToggle.GamingMode } : [],
        .. Offers(EdifierFeatures.InEarSwitch) ? new[] { DeviceToggle.InEarDetection } : [],
    ];

    public IReadOnlyList<Guid> ControlServiceUuids =>
    [
        .. HasSpp ? new[] { EdifierConstants.SppServiceId } : [],
        .. GattService is { } uuid ? new[] { new Guid(uuid) } : [],
        .. AlternateGattService is { } alternate ? new[] { new Guid(alternate) } : [],
    ];

    /// <summary>Exact model name scores 100, a variant ("W820NB Plus") 90, the family fallback 10.</summary>
    public int MatchScore(DeviceIdentity identity)
    {
        string name = EdifierProfiles.Normalize(identity.Name);
        if (name.Length == 0) return 0;
        if (IsGeneric) return name.Contains(_nameKey, StringComparison.Ordinal) ? 10 : 0;
        bool branded = false;
        string withoutBrand = name;
        if (withoutBrand.StartsWith("edifier", StringComparison.Ordinal))
        {
            withoutBrand = withoutBrand["edifier".Length..];
            branded = true;
        }
        if (withoutBrand.StartsWith("漫步者", StringComparison.Ordinal))
        {
            withoutBrand = withoutBrand[3..];
            branded = true;
        }
        // 花再 is an EDIFIER sub-brand ("漫步者・花再 Evo Pro").
        if (withoutBrand.StartsWith("花再", StringComparison.Ordinal))
        {
            withoutBrand = withoutBrand[2..];
            branded = true;
        }
        if (Spec.RequireBrand && !branded) return 0;
        // Loli is a common spelling of Lolli. Exact matching prevents Pro 2S/3i and
        // Max 2.0 from being swallowed by the shorter model (or claiming unrelated Atom devices).
        if (IsTws) return withoutBrand.Replace("loli", "lolli", StringComparison.Ordinal) == _nameKey ? 100 : 0;
        if (withoutBrand == _nameKey || Spec.Aliases?.Contains(withoutBrand) == true) return 100;
        return name.Contains(_nameKey, StringComparison.Ordinal) ? 90 : 0;
    }

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new EdifierDeviceController(channel, context, this);

    public override string ToString() => ModelId;

    private DeviceCapabilities BuildCapabilities()
    {
        string evidence = Spec.Evidence;
        var b = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, evidence)
            .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Unsupported, "No per-component charging evidence.")
            .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Unsupported, "Not implemented.")
            .Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Unsupported, "Not implemented.");

        b.Set(DeviceCapability.NoiseCancellation, Spec.Anc switch
        {
            EdifierAnc.None => CapabilitySupport.Unsupported,
            _ when Spec.StaticNoise && !IsTws => CapabilitySupport.Supported,
            _ => NoiseConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown,
        }, evidence);
        bool levels = NoiseConfirmed && (Confirmed.HasFlag(EdifierFeatures.AmbientLevel) || Spec.Anc is EdifierAnc.SlotW860 or EdifierAnc.SlotEvo);
        b.Set(DeviceCapability.NoiseCancellationLevel, levels ? CapabilitySupport.Supported : CapabilitySupport.Unsupported,
            levels ? evidence : "No level read back.");
        Feature(DeviceCapability.GamingMode, EdifierFeatures.GameMode);
        Feature(DeviceCapability.TouchCustomization, EdifierFeatures.KeyControl);
        Feature(DeviceCapability.InEarDetection, EdifierFeatures.InEarSwitch);
        Feature(DeviceCapability.Equalizer, EdifierFeatures.SoundEffect);
        return b.Build();

        void Feature(DeviceCapability capability, EdifierFeatures feature)
        {
            if (!Spec.Features.HasFlag(feature))
            {
                b.Set(capability, CapabilitySupport.Unsupported, "Not implemented for this model.");
            }
            else if (!Confirmed.HasFlag(feature))
            {
                b.Set(capability, CapabilitySupport.Unknown, "Offered after the headset answers the query.");
            }
            else
            {
                bool experimental = Spec.Experimental.HasFlag(feature);
                b.Set(capability, experimental ? CapabilitySupport.Experimental : CapabilitySupport.Supported,
                    experimental ? $"{evidence}; no reply samples, so experimental after read-back." : evidence);
            }
        }
    }

    private IReadOnlyList<NoiseControlOption> BuildNoiseOptions()
    {
        if (Spec.Anc == EdifierAnc.None || (!NoiseConfirmed && !(Spec.StaticNoise && !IsTws))) return [];
        var options = new List<NoiseControlOption>
        {
            Option(NoiseControlMode.Off, null, "Off", "NoiseMode_Off"),
            Option(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation"),
            Option(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency"),
        };
        switch (Spec.Anc)
        {
            case EdifierAnc.V1 when Confirmed.HasFlag(EdifierFeatures.AmbientLevel):
                for (int level = EdifierConstants.AmbientLevelMin; level <= EdifierConstants.AmbientLevelMax; level++)
                {
                    // Numbers need no translation: an empty resource key shows the display name.
                    options.Add(Option(NoiseControlMode.Transparency, EdifierSettings.AmbientLevelId(level), level.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture), ""));
                }
                break;
            case EdifierAnc.SlotW860:
                options.Add(Option(NoiseControlMode.NoiseCancellation, EdifierSettings.DeepLevel, "Deep", "NoiseLevel_Deep"));
                options.Add(Option(NoiseControlMode.NoiseCancellation, EdifierSettings.ComfortLevel, "Comfort", "Edifier_AncComfort"));
                options.Add(Option(NoiseControlMode.NoiseCancellation, EdifierSettings.WindLevel, "Anti-wind", "NoiseLevel_AntiWind"));
                break;
            case EdifierAnc.SlotEvo:
                options.Add(Option(NoiseControlMode.NoiseCancellation, EdifierSettings.WindLevel, "Anti-wind", "NoiseLevel_AntiWind"));
                break;
        }
        return options;

        static NoiseControlOption Option(NoiseControlMode mode, string? level, string name, string key) =>
            new(mode, level, name, key, CapabilitySupport.Supported);
    }
}
