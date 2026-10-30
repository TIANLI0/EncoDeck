// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Oppo.Models;

/// <summary>
/// One selectable noise-control option and its protocol indices.
/// </summary>
/// <param name="Option">Domain-level description shown in the UI.</param>
/// <param name="SetIndex">Protocol index sent in <c>0x0404 [01 01 mask]</c> to select this option.</param>
/// <param name="ReportedIndices">Indices the device reports while this option is active.</param>
public sealed record OppoNoiseMode(NoiseControlOption Option, int SetIndex, IReadOnlyList<int> ReportedIndices);

/// <summary>
/// Index sent when the user picks a top-level mode without a level (the device then chooses its
/// remembered level). When the parent itself is not allowed, <paramref name="DefaultLevelId"/> is selected instead.
/// </summary>
public sealed record OppoNoiseParent(int Index, CapabilitySupport Support, string? DefaultLevelId = null);

/// <summary>Maps a boolean device setting to its feature-switch id (0x0403 / 0x010D / 0x0503).</summary>
public sealed record OppoFeatureSwitch(DeviceToggle Toggle, byte FeatureId, CapabilitySupport Support);

/// <summary>
/// One configurable gesture row: which functions the official client offers for it.
/// Per-ear rows are written once per earbud (devType 1/2); call rows and <see cref="BothEars"/>
/// rows use devType 4. Call rows use button 6 (neckband call buttons use button 1).
/// </summary>
public sealed record OppoGestureRow(GestureAction Action, bool IsCallControl, IReadOnlyList<int> AllowedFunctions, string ResourceKey)
{
    public int Button { get; init; } = IsCallControl ? KeyFunction.CallButton : 1;

    /// <summary>One setting for both earbuds (official <c>showEar</c> 1).</summary>
    public bool BothEars { get; init; }

    /// <summary>Function shown while the earbuds have not reported one (official <c>defaultFunction</c>).</summary>
    public int? DefaultFunction { get; init; }

    /// <summary>
    /// Hold row whose noise-control choice opens the long-press cycle editor. With no
    /// <see cref="OppoGestureRow.AllowedFunctions"/> the row only edits the cycle (no 0x0401 write).
    /// </summary>
    public bool EditsNoiseCycle { get; init; }

    public bool IsWritten => AllowedFunctions.Count > 0;
}

/// <summary>Custom EQ layout for a model.</summary>
public sealed record OppoCustomEqualizerSpec(IReadOnlyList<int> BandFrequencies, int MinGain, int MaxGain, int MaxPresets);

/// <summary>
/// Declarative description of one OPPO model. Adding a model means adding one of these with
/// evidence for every value — never copying values from a different model on faith.
/// </summary>
public sealed record OppoModelDefinition
{
    /// <summary>Offers experimental standard LE pairing for this exact model; does not assert host audio support.</summary>
    public bool SupportsLeAudioPairing { get; init; }

    /// <summary>The earbuds answer status 01 to noise-mode changes once settled on LE Audio; set only where verified.</summary>
    public bool RejectsNoiseChangeOnLeAudio { get; init; }
    public required string ModelId { get; init; }

    public required string DisplayName { get; init; }

    public required string ImageKey { get; init; }

    public DeviceForm Form { get; init; }

    /// <summary>24-bit product ids reported by <c>0x0103</c>.</summary>
    public IReadOnlyList<uint> ProductIds { get; init; } = [];

    /// <summary>Exact Bluetooth friendly names (case-insensitive) that identify the model before the channel is open.</summary>
    public IReadOnlyList<string> BluetoothNames { get; init; } = [];

    public IReadOnlyList<Guid> ServiceUuids { get; init; } = [OppoServiceUuids.Secondary, OppoServiceUuids.Primary];

    public required DeviceCapabilities Capabilities { get; init; }

    public IReadOnlyList<OppoNoiseMode> NoiseModes { get; init; } = [];

    /// <summary>Parent indices used when only a top-level mode is chosen (device picks its last level).</summary>
    public IReadOnlyDictionary<NoiseControlMode, OppoNoiseParent> NoiseModeParents { get; init; } = new Dictionary<NoiseControlMode, OppoNoiseParent>();

    /// <summary>EQ presets keyed by protocol index.</summary>
    public IReadOnlyDictionary<byte, EqualizerPreset> EqualizerPresets { get; init; } = new Dictionary<byte, EqualizerPreset>();

    /// <summary>Presets offered only from a firmware version on (protocol index → lowest earbud firmware).</summary>
    public IReadOnlyDictionary<byte, int> EqualizerMinFirmware { get; init; } = new Dictionary<byte, int>();

    public IReadOnlyList<OppoFeatureSwitch> FeatureSwitches { get; init; } = [];

    /// <summary>Feature ids requested in the status query (0x010D), in the official client's order.</summary>
    public IReadOnlyList<byte> FeatureStatusIds { get; init; } = [];

    /// <summary>
    /// Notification event ids EncoDeck subscribes to (intersected with what the device offers).
    /// Null means "every offered event", like the official client; EncoDeck then answers F2/F4.
    /// </summary>
    public IReadOnlyList<byte>? SubscribedEvents { get; init; }

    public IReadOnlyList<OppoGestureRow> GestureRows { get; init; } = [];

    /// <summary>Top-level noise indices offered in the long-press cycle editor (bit = 1 &lt;&lt; index).</summary>
    public IReadOnlyList<int> NoiseCycleIndices { get; init; } = [];

    /// <summary>
    /// The long-press cycle is one setting for both earbuds (<c>0x0404 [02 01 mask]</c>, read
    /// <c>0x010C [02 01]</c>) instead of one per earbud (types 3 and 4).
    /// </summary>
    public bool NoiseCycleShared { get; init; }

    /// <summary>Label (resource key) of each <see cref="NoiseCycleIndices"/> entry.</summary>
    public IReadOnlyDictionary<int, string> NoiseCycleLabels { get; init; } = new Dictionary<int, string>();

    /// <summary>Fewest modes the long-press cycle may contain.</summary>
    public int NoiseCycleMinimum { get; init; } = 2;

    /// <summary>Spatial audio modes offered (0 off, 1 fixed, 2 head tracking).</summary>
    public IReadOnlyList<int> SpatialTypes { get; init; } = [0, 1, 2];

    /// <summary>Gesture function ids whose label differs on this model (id → resource key).</summary>
    public IReadOnlyDictionary<int, string> FunctionLabels { get; init; } = new Dictionary<int, string>();

    /// <summary>Prompt volume range (official default 1–10).</summary>
    public (int Min, int Max) PromptVolumeRange { get; init; } = (1, 10);

    /// <summary>Game sound types offered (0 = off).</summary>
    public IReadOnlyList<int> GameSoundTypes { get; init; } = [];

    public OppoCustomEqualizerSpec? CustomEqualizer { get; init; }

    /// <summary>Whether spatial audio uses the V2 commands 0x012A / 0x0422.</summary>
    public bool HasSpatialAudioV2 { get; init; }

    /// <summary>Official mutex: turning spatial audio on turns Hi-Res (feature 24) off.</summary>
    public bool SpatialHiResMutex { get; init; }

    /// <summary>Remote connect/disconnect/remove and preferred-device commands (capability bit 59), separate from multipoint.</summary>
    public bool SupportsMultiConnectOperations { get; init; } = true;

    /// <summary>Distinguishes definitions adapted to one pair of earbuds (see <see cref="Adapt"/>); part of <see cref="ProfileKey"/>.</summary>
    public string Variant { get; init; } = string.Empty;

    /// <summary>The model's own definition this one was adapted from (null when not adapted).</summary>
    public OppoModelDefinition? Base { get; init; }

    /// <summary>Changes whenever the offered settings change (model, capabilities or firmware).</summary>
    public string ProfileKey => Variant.Length == 0 ? ModelId : $"{ModelId}/{Variant}";

    /// <summary>Config flag <c>utcTimeZone</c>: the time reply carries the UTC offset.</summary>
    public bool TimeReplyIncludesOffset { get; init; }

    /// <summary>True when this is the catch-all definition for unrecognized OPPO devices (read-only).</summary>
    public bool IsGeneric { get; init; }

    /// <summary>
    /// Applies what the connected earbuds report, the way the official client decides at run time:
    /// spatial V2 (0x0422) versus V1 (feature 27), the game page when 0x0423 is offered, custom EQ
    /// only with 0x0418, and EQ presets gated by firmware (lower of the two earbuds).
    /// </summary>
    public OppoModelDefinition Adapt(OppoCapabilityBitmap? capabilities, int? firmware)
    {
        if (Base is { } original) return original.Adapt(capabilities, firmware);
        if (IsGeneric) return this;
        var next = this;
        var variant = new List<string>();

        if (capabilities is { } caps)
        {
            if (FeatureSwitches.Any(f => f.Toggle == DeviceToggle.DualDeviceConnection)
                && !caps.Allows(OppoCommand.OperateMultiConnect))
            {
                next = next with { SupportsMultiConnectOperations = false };
                variant.Add("mc0");
            }
            bool v2 = caps.Allows(OppoCommand.SetSpatialAudio);
            if (SpatialTypes.Count > 0 && v2 != HasSpatialAudioV2)
            {
                next = next with { HasSpatialAudioV2 = v2 };
            }
            if (SpatialTypes.Count > 0) variant.Add(v2 ? "s2" : "s1");
            if (caps.Allows(OppoCommand.SetGameSound)
                && FeatureSwitches.FirstOrDefault(f => f.Toggle == DeviceToggle.GamingMode) is { FeatureId: 0x06 } single
                && FeatureSwitches.All(f => f.Toggle != DeviceToggle.LowLatency))
            {
                // Official: earbuds with game sound (0x0423) take the single game-mode switch as the
                // master switch 40, and the status query asks for 39/40.
                next = next with
                {
                    FeatureSwitches = [.. FeatureSwitches.Select(f => f == single ? single with { FeatureId = 0x28 } : f)],
                    FeatureStatusIds = [.. FeatureStatusIds, .. new byte[] { 0x27, 0x28 }.Where(id => !FeatureStatusIds.Contains(id))],
                };
                variant.Add("g40");
            }
            if (CustomEqualizer is not null && !caps.Allows(OppoCommand.SetCustomEqualizer))
            {
                next = next with { CustomEqualizer = null, Capabilities = next.Capabilities.With(DeviceCapability.CustomEqualizer, CapabilitySupport.Unsupported, "Earbuds do not offer 0x0418.") };
                variant.Add("nc");
            }
        }

        if (firmware is { } fw && EqualizerMinFirmware.Count > 0)
        {
            next = next with
            {
                EqualizerPresets = EqualizerPresets
                    .Where(p => !EqualizerMinFirmware.TryGetValue(p.Key, out int min) || fw >= min)
                    .ToDictionary(p => p.Key, p => p.Value),
            };
            // The variant names the outcome (which presets), not the version: a firmware update
            // that changes nothing leaves the settings page as it is.
            variant.Add("eq" + string.Join('.', next.EqualizerPresets.Keys.Order()));
        }
        // Only what changes the offered settings goes into the key, so remembered and live
        // capabilities that lead to the same settings never rebuild the UI.
        return next with { Variant = string.Join('-', variant), Base = this };
    }

    public OppoNoiseMode? FindNoiseModeByReportedBitmap(uint bitmap)
    {
        foreach (var mode in NoiseModes)
        {
            foreach (int index in mode.ReportedIndices)
            {
                if (index < 32 && (bitmap & (1u << index)) != 0) return mode;
            }
        }
        return null;
    }

    public OppoNoiseMode? FindNoiseMode(NoiseControlState state) =>
        NoiseModes.FirstOrDefault(m => m.Option.Matches(state));
}
