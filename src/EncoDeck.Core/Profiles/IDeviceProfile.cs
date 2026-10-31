// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;

namespace EncoDeck.Core.Profiles;

/// <summary>One selectable noise-control option, as the device model defines it.</summary>
/// <param name="Mode">Top-level mode this option belongs to.</param>
/// <param name="LevelId">Level id within the mode, or null for the mode's only option.</param>
/// <param name="DisplayName">Default (English) label; the UI localizes via <see cref="ResourceKey"/>.</param>
/// <param name="ResourceKey">Localization resource key.</param>
/// <param name="Support">Whether this concrete option is verified on the model.</param>
public sealed record NoiseControlOption(
    NoiseControlMode Mode,
    string? LevelId,
    string DisplayName,
    string ResourceKey,
    CapabilitySupport Support)
{
    public NoiseControlState ToState() => new(Mode, LevelId);

    public bool Matches(NoiseControlState state) => state.Mode == Mode && state.LevelId == LevelId;
}

/// <summary>A device-side EQ preset.</summary>
public sealed record EqualizerPreset(string Id, string DisplayName, string ResourceKey, CapabilitySupport Support, bool EditableBands = false);

/// <summary>Physical form, used to pick the generic illustration when a model has no product image.</summary>
public enum DeviceForm
{
    /// <summary>True wireless earbuds with a charging case.</summary>
    Earbuds,
    /// <summary>Over-ear headphones and neckbands (no charging case).</summary>
    Headphones,
}

/// <summary>
/// Static description of a device model: how to recognize it and what it can do.
/// Protocol details live in the protocol adapter, not here.
/// </summary>
public interface IDeviceProfile
{
    /// <summary>Stable id such as <c>oppo-enco-x4</c>.</summary>
    string ModelId { get; }

    string DisplayName { get; }

    /// <summary>Key used by the app's image resolver; a generic illustration is used when missing.</summary>
    string ImageKey { get; }

    DeviceForm Form => DeviceForm.Earbuds;

    IDeviceCapabilities Capabilities { get; }

    IReadOnlyList<NoiseControlOption> NoiseControlOptions { get; }

    IReadOnlyList<EqualizerPreset> EqualizerPresets { get; }

    /// <summary>RFCOMM service UUIDs to try for the control channel, in order.</summary>
    IReadOnlyList<Guid> ControlServiceUuids { get; }

    /// <summary>
    /// Whether a connected LE bond at the earbuds' address counts as LE Audio, and EncoDeck keeps that LE link up.
    /// False for earbuds controlled over Classic alone whose LE Audio Windows decides (EarFun): their LE link can be
    /// up next to A2DP, so only an active LE Audio output proves LE Audio, and keeping the link up would push it.
    /// </summary>
    bool ManagesLeAudioLink => true;

    /// <summary>Returns a match score (0 = not this model). Higher wins.</summary>
    int MatchScore(DeviceIdentity identity);
}

public interface IDeviceProfileRegistry
{
    IReadOnlyList<IDeviceProfile> Profiles { get; }

    IDeviceProfile? Match(DeviceIdentity identity);
}

public sealed class DeviceProfileRegistry(IEnumerable<IDeviceProfile> profiles) : IDeviceProfileRegistry
{
    public IReadOnlyList<IDeviceProfile> Profiles { get; } = profiles.ToArray();

    public IDeviceProfile? Match(DeviceIdentity identity) => Profiles
        .Select(p => (Profile: p, Score: p.MatchScore(identity)))
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .Select(x => x.Profile)
        .FirstOrDefault();
}
