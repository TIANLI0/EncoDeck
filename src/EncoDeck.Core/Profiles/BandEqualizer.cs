// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.Profiles;

/// <summary>
/// Limits of a brand's single device-side user EQ curve (non-OPPO brands). Gains are in steps of
/// 1 / <paramref name="GainScale"/> dB (10 = tenths of a dB); band frequencies come from the device.
/// The curve is one fixed slot: it cannot be added, renamed or deleted, and selecting it switches
/// the device to that curve.
/// </summary>
public sealed record BandEqualizerSpec(int MinGain, int MaxGain, int GainScale = 10)
{
    /// <summary>Curves offered as starting points; applying one writes it as the user curve.</summary>
    public IReadOnlyList<BandCurvePreset> Presets { get; init; } = [];

    /// <summary>
    /// The device never reports its curve: the editor shows the curve EncoDeck last sent (state source
    /// <c>Sent</c>, or <c>Cache</c> from an earlier session), which another app may have changed since.
    /// </summary>
    public bool WriteOnly { get; init; }
}

/// <summary>A named curve for <see cref="BandEqualizerSpec.Presets"/>; <paramref name="Variant"/> &gt; 0 is appended to the name.</summary>
public sealed record BandCurvePreset(string LabelKey, int Variant, IReadOnlyList<int> Gains);

/// <summary>A profile whose controller reports one fixed user curve through <c>DeviceState.CustomEqualizers</c>.</summary>
public interface IBandEqualizerProfile
{
    /// <summary>Null until the device has returned a complete, valid curve.</summary>
    BandEqualizerSpec? BandEqualizer { get; }
}
