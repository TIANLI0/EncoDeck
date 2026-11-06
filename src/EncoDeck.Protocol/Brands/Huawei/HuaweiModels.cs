// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Per-model data extended from OpenFreebuds (https://github.com/melianmiko/OpenFreebuds, MelianMiko and
// contributors, GPL-3.0) incl. the vicrodh fork (https://github.com/vicrodh/OpenFreebuds), HuaweiPods
// (https://github.com/Nshpiter/HuaweiPods, Nshpiter and contributors, GPL-3.0) and EZBuds
// (https://github.com/CenSher/EZBuds, CenSher, GPL-3.0). Revisions are pinned in THIRD_PARTY_NOTICES.md.
using System.Text;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Huawei;

/// <summary>One discrete noise-cancelling or transparency level and its protocol sub-mode.</summary>
public sealed record HuaweiAncLevel(string LevelId, byte SubMode, string DisplayName, string ResourceKey);

/// <summary>One built-in EQ preset and its protocol id.</summary>
public sealed record HuaweiEqPreset(byte ProtocolId, string PresetId, string DisplayName, string ResourceKey);

/// <summary>Features addressed through <see cref="HuaweiCommands.FeatureState"/>; each opens only after the earbuds report it.</summary>
[Flags]
public enum HuaweiSmartFeatures
{
    None = 0,
    AdaptiveVolume = 1,
    HeadGestures = 2,
}

/// <summary>
/// What a HUAWEI model supports, as the community projects' per-model tables declare it. Not verified
/// on hardware by EncoDeck; COMPATIBILITY.md marks the items with community real-device evidence.
/// </summary>
public sealed record HuaweiModel(
    string ModelId,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    bool Anc,
    bool Transparency,
    bool AncReadback,
    IReadOnlyList<HuaweiAncLevel> AncLevels,
    bool RfcommBattery,
    bool WearDetection,
    IReadOnlyList<HuaweiEqPreset> EqPresets,
    bool LowLatency,
    bool DualConnect,
    bool HasChargingCase,
    byte TransparencySubMode = HuaweiCommands.AncSubModeDefault,
    byte PreferredSppChannel = 0,
    HuaweiProtocol Protocol = HuaweiProtocol.Legacy,
    bool IsGeneric = false)
{
    /// <summary>Selectable transparency levels (normal / voice boost); empty for a single transparency mode.</summary>
    public IReadOnlyList<HuaweiAncLevel> TransparencyLevels { get; init; } = [];

    /// <summary>Per-earbud wear state is read from <see cref="HuaweiCommands.WearState"/> at session start.</summary>
    public bool WearState { get; init; }

    /// <summary>Feature-id switches probed (read only) at session start.</summary>
    public HuaweiSmartFeatures SmartFeatures { get; init; }

    /// <summary>
    /// Independent spatial audio modes (feature 24) that community real-device records cover for this
    /// model. Not a gate: every model is probed (read only) and the control opens once the earbuds report
    /// a valid state; models without records offer <see cref="HuaweiDeviceProfile.DefaultSpatialModes"/>.
    /// </summary>
    public IReadOnlyList<SpatialAudioMode> SpatialModes { get; init; } = [];

    /// <summary>Noise control is offered only where the state can be read back to confirm a write.</summary>
    public bool NoiseControlUsable => Anc && AncReadback;
}

/// <summary>Built-in HUAWEI model table and name matching.</summary>
public static class HuaweiModels
{
    private static HuaweiAncLevel Dynamic => new("dynamic", 0x03, "Dynamic", "Huawei_NoiseLevel_Dynamic");
    private static HuaweiAncLevel Cozy => new("cozy", 0x01, "Cozy", "Huawei_NoiseLevel_Cozy");
    private static HuaweiAncLevel General => new("general", 0x00, "General", "Huawei_NoiseLevel_General");
    private static HuaweiAncLevel Ultra => new("ultra", 0x02, "Ultra", "Huawei_NoiseLevel_Ultra");

    // OpenFreebuds cancel_level_options: comfort = 1, normal = 0, ultra = 2, dynamic = 3. HuaweiPods'
    // captures confirm it for FreeBuds 5 / 5i / 6 / 6i / 7 / Pro 5.
    private static readonly HuaweiAncLevel[] FullLevels = [Dynamic, Cozy, General, Ultra];
    private static readonly HuaweiAncLevel[] NoDynamicLevels = [Cozy, General, Ultra];
    private static readonly HuaweiAncLevel[] FreeBuds5Levels = [Dynamic, Cozy, General];
    private static readonly HuaweiAncLevel[] FreeBuds4ELevels = [Cozy, General];

    // HuaweiPods' captures of FreeBuds Pro 3 and 7i use another order: dynamic = 1, cozy = 0,
    // general = 2, ultra = 3 (OpenFreebuds labels these values with the table above).
    private static readonly HuaweiAncLevel[] Pro3Levels =
        [Dynamic with { SubMode = 0x01 }, Cozy with { SubMode = 0x00 }, General with { SubMode = 0x02 }, Ultra with { SubMode = 0x03 }];

    // Transparency sub-modes: 2 = normal, 1 = voice boost (OpenFreebuds awareness_level_options;
    // HuaweiPods captures of FreeBuds 6i and Pro 5).
    private static readonly HuaweiAncLevel[] VoiceBoostLevels =
    [
        new("normal", 0x02, "Standard transparency", "NoiseLevel_TransparencyStandard"),
        new("voice", 0x01, "Voice boost", "NoiseLevel_EnhancedVoices"),
    ];

    // Independent spatial audio (feature 24, TLV 2): 0 = off, 1 = head tracking, 2 = fixed. HuaweiPods
    // writes and reads back all three on FreeClip 2 (both editions), FreeBuds 7, 7i and Pro 5; the
    // OpenFreebuds vicrodh fork tests the same values on FreeBuds 7i.
    private static readonly SpatialAudioMode[] AllSpatialModes = [SpatialAudioMode.Off, SpatialAudioMode.Fixed, SpatialAudioMode.HeadTracking];

    private static HuaweiEqPreset Eq(byte id, string presetId, string name, string key) => new(id, presetId, name, "Huawei_Eq_" + key);

    // Pro 3 / 4 and FreeClip use different ids from the other models (OpenFreebuds buds_pro_3.py).
    private static readonly HuaweiEqPreset[] ProEq =
        [Eq(5, "default", "Default", "Default"), Eq(1, "bass", "Bass boost", "Bass"), Eq(2, "treble", "Treble boost", "Treble"), Eq(9, "voices", "Vocals", "Voices")];
    private static readonly HuaweiEqPreset[] ThreeEq =
        [Eq(1, "default", "Default", "Default"), Eq(2, "bass", "Bass boost", "Bass"), Eq(3, "treble", "Treble boost", "Treble")];
    private static readonly HuaweiEqPreset[] StandardEq = [.. ThreeEq, Eq(9, "voices", "Vocals", "Voices")];
    private static readonly HuaweiEqPreset Sport = Eq(0x0A, "sport", "Sport", "Sport");

    // HuaweiPods captures: FreeBuds 6 has no preset 1 and uses 0 as its default.
    private static readonly HuaweiEqPreset[] FreeBuds6Eq =
        [Eq(0, "default", "Default", "Default"), Eq(2, "bass", "Bass boost", "Bass"), Eq(3, "treble", "Treble boost", "Treble"), Eq(9, "voices", "Vocals", "Voices")];

    // HuaweiPods captures of FreeBuds 7 and Pro 5 (Pro 5's classical curve 0xC9 and AI 0x11 are left out).
    private static readonly HuaweiEqPreset[] SceneEq =
    [
        Eq(5, "balanced", "Balanced", "Balanced"), Eq(9, "voices", "Vocals", "Voices"), Eq(2, "bass", "Bass boost", "Bass"),
        Eq(0x0D, "movie", "Movie", "Movie"), Eq(0x0F, "podcast", "Podcast", "Podcast"), Eq(0x0E, "game", "Game", "Game"),
        Eq(0x10, "sport", "Sport", "Sport"),
    ];

    /// <summary>Every built-in model (27).</summary>
    public static IReadOnlyList<HuaweiModel> All { get; } =
    [
        // Battery over SPP read on hardware (OpenFreebuds #128); noise control has no read-back, so it stays closed.
        M("huawei-freebuds-3", "HUAWEI FreeBuds 3", anc: true, readback: false, battery: true, hasCase: true, channel: 1),
        M("huawei-freebuds-3i", "HUAWEI FreeBuds 3i", anc: true, transparency: true, readback: true, battery: true, hasCase: true),
        M("huawei-freebuds-4e", "HUAWEI FreeBuds 4E", anc: true, readback: true, levels: FreeBuds4ELevels, battery: true, wear: true, eq: ThreeEq, hasCase: true),
        M("huawei-freebuds-4i", "HUAWEI FreeBuds 4i", anc: true, transparency: true, readback: true, battery: true, wear: true, hasCase: true),
        M("huawei-freebuds-5", "HUAWEI FreeBuds 5", anc: true, readback: true, levels: FreeBuds5Levels, battery: true, wear: true, eq: StandardEq, lowLatency: true, hasCase: true),
        M("huawei-freebuds-5i", "HUAWEI FreeBuds 5i", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, eq: StandardEq, lowLatency: true, dual: true, hasCase: true, channel: 16),
        M("huawei-freebuds-6", "HUAWEI FreeBuds 6", anc: true, readback: true, levels: FreeBuds5Levels, battery: true, wear: true, eq: FreeBuds6Eq, lowLatency: true, hasCase: true)
            with { SmartFeatures = HuaweiSmartFeatures.AdaptiveVolume },
        M("huawei-freebuds-6i", "HUAWEI FreeBuds 6i", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, eq: StandardEq, lowLatency: true, dual: true, hasCase: true, transparencySub: 0x02, channel: 1)
            with { TransparencyLevels = VoiceBoostLevels },
        M("huawei-freebuds-7", "HUAWEI FreeBuds 7", anc: true, transparency: true, readback: true, levels: FreeBuds5Levels, battery: true, wear: true, eq: SceneEq, lowLatency: true, hasCase: true, transparencySub: 0x02)
            with { SmartFeatures = HuaweiSmartFeatures.AdaptiveVolume | HuaweiSmartFeatures.HeadGestures, SpatialModes = AllSpatialModes },
        M("huawei-freebuds-7i", "HUAWEI FreeBuds 7i", anc: true, transparency: true, readback: true, levels: Pro3Levels, battery: true, wear: true, eq: StandardEq, lowLatency: true, dual: true, hasCase: true, channel: 1)
            with { WearState = true, SmartFeatures = HuaweiSmartFeatures.HeadGestures, SpatialModes = AllSpatialModes },
        M("huawei-freebuds-pro", "HUAWEI FreeBuds Pro", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, dual: true, hasCase: true, channel: 1),
        M("huawei-freebuds-pro-2", "HUAWEI FreeBuds Pro 2", anc: true, transparency: true, readback: true, levels: NoDynamicLevels, battery: true, wear: true, eq: ThreeEq, dual: true, hasCase: true, channel: 1),
        M("huawei-freebuds-pro-3", "HUAWEI FreeBuds Pro 3", anc: true, transparency: true, readback: true, levels: Pro3Levels, battery: true, wear: true, eq: ProEq, lowLatency: true, dual: true, hasCase: true, channel: 1),
        // Off / noise cancelling / transparency read back on Windows (OpenFreebuds #138); level labels unverified, so no levels.
        M("huawei-freebuds-pro-4", "HUAWEI FreeBuds Pro 4", anc: true, transparency: true, readback: true, battery: true, wear: true, eq: ProEq, lowLatency: true, dual: true, hasCase: true, channel: 1)
            with { WearState = true },
        M("huawei-freebuds-pro-5", "HUAWEI FreeBuds Pro 5", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, eq: SceneEq, lowLatency: true, dual: true, hasCase: true, transparencySub: 0x02, channel: 1)
            with { TransparencyLevels = VoiceBoostLevels, SmartFeatures = HuaweiSmartFeatures.AdaptiveVolume | HuaweiSmartFeatures.HeadGestures, SpatialModes = AllSpatialModes },
        M("huawei-freebuds-se", "HUAWEI FreeBuds SE", battery: true, hasCase: true),
        M("huawei-freebuds-se-2", "HUAWEI FreeBuds SE 2", battery: true, eq: StandardEq, lowLatency: true, hasCase: true, channel: 1),
        M("huawei-freebuds-se-4", "HUAWEI FreeBuds SE 4", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, eq: StandardEq, lowLatency: true, hasCase: true, channel: 1, extraAliases: ["HUAWEI FreeBuds SE 4 ANC"]),
        M("huawei-freebuds-studio", "HUAWEI FreeBuds Studio", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, eq: ThreeEq, hasCase: false, channel: 1),
        // OpenFreebuds drives the FreeClip with its Pro 3 driver (device page: tested).
        M("huawei-freeclip", "HUAWEI FreeClip", battery: true, wear: true, eq: ProEq, lowLatency: true, dual: true, hasCase: true, channel: 1),
        M("huawei-freeclip-2", "HUAWEI FreeClip 2", battery: true, wear: true, eq: [.. StandardEq, Sport], lowLatency: true, dual: true, hasCase: true, channel: 1, extraAliases: ["HUAWEI FreeClip 2 典藏版"])
            with { SmartFeatures = HuaweiSmartFeatures.AdaptiveVolume | HuaweiSmartFeatures.HeadGestures, SpatialModes = AllSpatialModes },
        M("huawei-freearc", "HUAWEI FreeArc", battery: true, eq: [.. StandardEq, Sport], hasCase: true),
        M("huawei-freelace-pro", "HUAWEI FreeLace Pro", anc: true, transparency: true, readback: true, levels: NoDynamicLevels, battery: true, hasCase: false),
        // FreeLace Pro 2 only has custom EQ (no built-in presets), which EncoDeck does not port.
        M("huawei-freelace-pro-2", "HUAWEI FreeLace Pro 2", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, lowLatency: true, dual: true, hasCase: false, transparencySub: 0x02, channel: 1)
            with { TransparencyLevels = VoiceBoostLevels },
        M("huawei-eyewear", "HUAWEI Eyewear", battery: true, hasCase: false, shortAlias: false),
        M("huawei-eyewear-2", "HUAWEI Eyewear 2", battery: true, lowLatency: true, hasCase: false),
        M("huawei-eyewear-3", "HUAWEI Eyewear 3", battery: true, hasCase: false),
    ];

    /// <summary>Name-only fallback for unrecognized HUAWEI audio devices: battery only.</summary>
    public static HuaweiModel Generic { get; } = new(
        "huawei-generic", "HUAWEI earbuds", [], Anc: false, Transparency: false, AncReadback: false, AncLevels: [],
        RfcommBattery: true, WearDetection: false, EqPresets: [], LowLatency: false, DualConnect: false, HasChargingCase: true, IsGeneric: true);

    private static readonly string[] FamilyPrefixes = ["huawei", "freebuds", "freeclip", "freearc", "freelace", "eyewear"];

    private static readonly (string Alias, HuaweiModel Model)[] AliasTable =
        [.. All.SelectMany(m => m.Aliases.Select(a => (Normalize(a), m))).OrderByDescending(x => x.Item1.Length)];

    /// <summary>Lower-case letters and digits only (the reference's <c>normalizeDeviceName</c>).</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Exact alias match first, then the longest alias the name starts with (e.g. "FreeBuds Pro 3 (A1B2)").</summary>
    public static HuaweiModel? Detect(string? deviceName, out bool exact)
    {
        exact = false;
        string normalized = Normalize(deviceName);
        if (normalized.Length == 0) return null;
        foreach (var (alias, model) in AliasTable)
        {
            if (alias == normalized)
            {
                exact = true;
                return model;
            }
        }
        foreach (var (alias, model) in AliasTable)
        {
            if (normalized.StartsWith(alias, StringComparison.Ordinal)) return model;
        }
        return null;
    }

    /// <summary>Whether the name looks like any HUAWEI audio product family.</summary>
    public static bool IsFamilyName(string? deviceName)
    {
        string normalized = Normalize(deviceName);
        return normalized.Length > 0 && FamilyPrefixes.Any(p => normalized.StartsWith(p, StringComparison.Ordinal));
    }

    private static HuaweiModel M(
        string id,
        string name,
        bool anc = false,
        bool transparency = false,
        bool readback = false,
        HuaweiAncLevel[]? levels = null,
        bool battery = false,
        bool wear = false,
        HuaweiEqPreset[]? eq = null,
        bool lowLatency = false,
        bool dual = false,
        bool hasCase = true,
        byte transparencySub = HuaweiCommands.AncSubModeDefault,
        byte channel = 0,
        bool shortAlias = true,
        string[]? extraAliases = null)
    {
        string shortName = name["HUAWEI ".Length..];
        string[] aliases = shortAlias ? [name, shortName, .. extraAliases ?? []] : [name, .. extraAliases ?? []];
        return new HuaweiModel(id, name, aliases, anc, transparency, readback, levels ?? [], battery, wear, eq ?? [], lowLatency, dual, hasCase, transparencySub, channel);
    }
}
