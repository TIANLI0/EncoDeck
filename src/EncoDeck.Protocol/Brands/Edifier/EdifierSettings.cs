// SPDX-License-Identifier: GPL-3.0-only
// Wire values from mEDIFIER doc/KnownCommands/W820NB_Double_Gold.txt (https://github.com/wh201906/mEDIFIER,
// 10056b5, MIT, Copyright (c) 2023 Self Not Found; MIT notice in THIRD_PARTY_NOTICES.md), HyperEars
// docs/edifier-bes-protocol.md (https://github.com/silverpoetry/HyperEars, 361d279, GPL-3.0-only) and
// BudsLink edifierBudsConfig.js (https://github.com/maniacx/BudsLink, ba3160d, GPL-3.0). W830NB noise
// values, ambient levels and scenes from the user recordings in issue #18.
using System.Globalization;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Edifier;

/// <summary>Noise dialects, ambient levels, sound effects and button control of EDIFIER models.</summary>
internal static class EdifierSettings
{
    public const string DeepLevel = "deep";
    public const string ComfortLevel = "comfort";
    public const string WindLevel = "anti-wind";
    public const string BroadbandLevel = "broadband";

    private const string AmbientPrefix = "ambient";

    public static string AmbientLevelId(int level) => AmbientPrefix + level.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    public static bool TryParseAmbientLevelId(string? id, out int level)
    {
        level = 0;
        return id is not null && id.StartsWith(AmbientPrefix, StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(AmbientPrefix.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out level)
            && level is >= EdifierConstants.AmbientLevelMin and <= EdifierConstants.AmbientLevelMax;
    }

    /// <summary>Sound effect presets 0–3 (classic, pop, classical, rock).</summary>
    public static IReadOnlyList<EqualizerPreset> SoundEffects { get; } =
    [
        new(PresetId(0), "Classic", "Edifier_Eq_Classic", CapabilitySupport.Supported),
        new(PresetId(1), "Pop", "Edifier_Eq_Pop", CapabilitySupport.Supported),
        new(PresetId(2), "Classical", "Edifier_Eq_Classical", CapabilitySupport.Supported),
        new(PresetId(3), "Rock", "Edifier_Eq_Rock", CapabilitySupport.Supported),
    ];

    public static string PresetId(int value) => $"edifier-eq-{value}";

    public static bool TryParsePresetId(string presetId, out byte value)
    {
        value = 0;
        return presetId.StartsWith("edifier-eq-", StringComparison.Ordinal) && byte.TryParse(presetId.AsSpan(11), out value) && value <= 3;
    }

    /// <summary>The physical button's noise cycle as one setting for both ears (function = mode mask).</summary>
    public static BrandGestureSlot KeyControlSlot { get; } = new(
        EarSide.Both,
        0,
        GestureAction.SingleSqueeze,
        "Edifier_ButtonCycle",
        [.. EdifierConstants.KeyControlMasks.Select(mask => new BrandGestureChoice(mask, MaskLabels(mask)))]);

    private static string[] MaskLabels(int mask) =>
    [
        .. (mask & 1) != 0 ? new[] { "NoiseMode_Off" } : [],
        .. (mask & 2) != 0 ? new[] { "NoiseMode_NoiseCancellation" } : [],
        .. (mask & 4) != 0 ? new[] { "NoiseMode_Transparency" } : [],
    ];

    /// <summary>Slot byte that prefixes v2 noise payloads, or null for v1 dialects.</summary>
    public static byte? Slot(EdifierAnc anc) => anc switch
    {
        EdifierAnc.SlotW860 => EdifierConstants.AncSlotW860,
        EdifierAnc.SlotEvo or EdifierAnc.SlotW830 => EdifierConstants.AncSlotEvo,
        EdifierAnc.SlotX5 => EdifierConstants.AncSlotX5,
        _ => null,
    };

    /// <summary>W830NB value of the ambient mode; its level follows as a third byte.</summary>
    private const byte W830Ambient = 5;

    /// <summary>
    /// Decodes a (decrypted) <c>0xCC</c> answer. A payload for another slot, or a value outside the
    /// dialect, is not guessed at (null). Evo values 1–3 are all noise cancelling; their strengths are not named.
    /// </summary>
    public static NoiseControlState? DecodeNoise(EdifierAnc anc, ReadOnlySpan<byte> p, bool ambientLevels)
    {
        if (Slot(anc) is { } slot)
        {
            if (p.Length < 2 || p[0] != slot) return null;
            if (anc == EdifierAnc.SlotW830 && p[1] == W830Ambient)
            {
                // A level outside −3…+3 is not guessed at; a report without one is plain ambient.
                if (p.Length < 3) return new(NoiseControlMode.Transparency);
                return AmbientLevel(p[2]) is { } ambient ? new(NoiseControlMode.Transparency, AmbientLevelId(ambient)) : null;
            }
            return (anc, p[1]) switch
            {
                (EdifierAnc.SlotW860, 1) => new(NoiseControlMode.NoiseCancellation, DeepLevel),
                (EdifierAnc.SlotW860, 2) => new(NoiseControlMode.NoiseCancellation, ComfortLevel),
                (EdifierAnc.SlotW860, 3) => new(NoiseControlMode.NoiseCancellation, WindLevel),
                (EdifierAnc.SlotW860, 4) => new(NoiseControlMode.Transparency),
                (EdifierAnc.SlotW860, 5) => new(NoiseControlMode.Off),
                (EdifierAnc.SlotEvo, 1 or 2 or 3) => new(NoiseControlMode.NoiseCancellation),
                (EdifierAnc.SlotEvo, 4) => new(NoiseControlMode.NoiseCancellation, WindLevel),
                (EdifierAnc.SlotEvo, 5) => new(NoiseControlMode.Transparency),
                (EdifierAnc.SlotEvo, 6) => new(NoiseControlMode.Off),
                (EdifierAnc.SlotW830, 1) => new(NoiseControlMode.NoiseCancellation, DeepLevel),
                (EdifierAnc.SlotW830, 2) => new(NoiseControlMode.NoiseCancellation, ComfortLevel),
                (EdifierAnc.SlotW830, 3) => new(NoiseControlMode.NoiseCancellation, BroadbandLevel),
                (EdifierAnc.SlotW830, 4) => new(NoiseControlMode.NoiseCancellation, WindLevel),
                (EdifierAnc.SlotW830, 6) => new(NoiseControlMode.Off),
                (EdifierAnc.SlotX5, 1) => new(NoiseControlMode.NoiseCancellation),
                (EdifierAnc.SlotX5, 2) => new(NoiseControlMode.Transparency),
                (EdifierAnc.SlotX5, 3) => new(NoiseControlMode.Off),
                _ => null,
            };
        }
        if (anc is not (EdifierAnc.V1 or EdifierAnc.Probe) || p.IsEmpty) return null;
        return p[0] switch
        {
            EdifierConstants.NoiseOff => new(NoiseControlMode.Off),
            EdifierConstants.NoiseAnc => new(NoiseControlMode.NoiseCancellation),
            EdifierConstants.NoiseTransparency when ambientLevels && p.Length >= 2 && AmbientLevel(p[1]) is { } level =>
                new(NoiseControlMode.Transparency, AmbientLevelId(level)),
            EdifierConstants.NoiseTransparency => new(NoiseControlMode.Transparency),
            _ => null,
        };
    }

    /// <summary>Ambient level −3…+3 for a level byte 3–9, else null.</summary>
    public static int? AmbientLevel(byte value) =>
        value - EdifierConstants.AmbientLevelOffset is var level and >= EdifierConstants.AmbientLevelMin and <= EdifierConstants.AmbientLevelMax ? level : null;

    /// <summary>
    /// Plaintext <c>0xC1</c> payload for a target, or null when the dialect cannot express it. The
    /// W830NB ambient payload mirrors its report <c>[1B][05][level]</c>; plain ambient keeps
    /// <paramref name="ambientFallback"/> (the last level the headset reported) rather than resetting it.
    /// </summary>
    public static byte[]? EncodeNoise(EdifierAnc anc, NoiseControlState target, bool ambientLevels, int ambientFallback = 0)
    {
        string? level = target.LevelId;
        if (anc == EdifierAnc.SlotW830 && target.Mode == NoiseControlMode.Transparency)
        {
            int ambient = ambientFallback;
            if (level is not null && !TryParseAmbientLevelId(level, out ambient)) return null;
            if (ambient is < EdifierConstants.AmbientLevelMin or > EdifierConstants.AmbientLevelMax) ambient = 0;
            return [EdifierConstants.AncSlotEvo, W830Ambient, (byte)(ambient + EdifierConstants.AmbientLevelOffset)];
        }
        if (Slot(anc) is { } slot)
        {
            byte? value = (anc, target.Mode, level) switch
            {
                (EdifierAnc.SlotW860, NoiseControlMode.Off, null) => 5,
                (EdifierAnc.SlotW860, NoiseControlMode.NoiseCancellation, null or DeepLevel) => 1,
                (EdifierAnc.SlotW860, NoiseControlMode.NoiseCancellation, ComfortLevel) => 2,
                (EdifierAnc.SlotW860, NoiseControlMode.NoiseCancellation, WindLevel) => 3,
                (EdifierAnc.SlotW860, NoiseControlMode.Transparency, null) => 4,
                (EdifierAnc.SlotEvo, NoiseControlMode.Off, null) => 6,
                (EdifierAnc.SlotEvo, NoiseControlMode.NoiseCancellation, null) => 1,
                (EdifierAnc.SlotEvo, NoiseControlMode.NoiseCancellation, WindLevel) => 4,
                (EdifierAnc.SlotEvo, NoiseControlMode.Transparency, null) => 5,
                (EdifierAnc.SlotW830, NoiseControlMode.Off, null) => 6,
                (EdifierAnc.SlotW830, NoiseControlMode.NoiseCancellation, null or DeepLevel) => 1,
                (EdifierAnc.SlotW830, NoiseControlMode.NoiseCancellation, ComfortLevel) => 2,
                (EdifierAnc.SlotW830, NoiseControlMode.NoiseCancellation, BroadbandLevel) => 3,
                (EdifierAnc.SlotW830, NoiseControlMode.NoiseCancellation, WindLevel) => 4,
                (EdifierAnc.SlotX5, NoiseControlMode.Off, null) => 3,
                (EdifierAnc.SlotX5, NoiseControlMode.NoiseCancellation, null) => 1,
                (EdifierAnc.SlotX5, NoiseControlMode.Transparency, null) => 2,
                _ => null,
            };
            return value is { } v ? [slot, v] : null;
        }
        if (anc is not (EdifierAnc.V1 or EdifierAnc.Probe)) return null;
        return (target.Mode, level) switch
        {
            (NoiseControlMode.Off, null) => [EdifierConstants.NoiseOff],
            (NoiseControlMode.NoiseCancellation, null) => [EdifierConstants.NoiseAnc],
            (NoiseControlMode.Transparency, null) => [EdifierConstants.NoiseTransparency],
            (NoiseControlMode.Transparency, _) when ambientLevels && TryParseAmbientLevelId(level, out int ambient) =>
                [EdifierConstants.NoiseTransparency, (byte)(ambient + EdifierConstants.AmbientLevelOffset)],
            _ => null,
        };
    }

    /// <summary>W830NB scenes in <c>0x08</c> / <c>0x09</c> byte 0 (issue #18 recordings).</summary>
    public const int SceneMusic = 0;
    public const int SceneGame = 1;
    public const int SceneSpatial = 2;

    /// <summary>Id of the W830NB scene in <see cref="DeviceState.Options"/>.</summary>
    public const string SceneOption = "edifier-scene-mode";

    public static DeviceOption SceneModeOption(int scene) => new(SceneOption, "Edifier_Scene", scene,
    [
        new DeviceOptionChoice(SceneMusic, "Edifier_Scene_Music"),
        new DeviceOptionChoice(SceneGame, "Edifier_Scene_Game"),
        new DeviceOptionChoice(SceneSpatial, "Edifier_Scene_Spatial"),
    ], 5) { DescriptionKey = "Edifier_Scene_Description" };

    /// <summary>A read-back satisfies a write: same mode, and the same level unless none was asked for.</summary>
    public static bool Satisfies(NoiseControlState actual, NoiseControlState requested) =>
        actual.Mode == requested.Mode && (requested.LevelId is null || actual.LevelId == requested.LevelId);
}
