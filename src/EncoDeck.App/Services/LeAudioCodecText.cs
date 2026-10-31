// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using EncoDeck.Platform.LeAudioCodec;

namespace EncoDeck.App.Services;

/// <summary>Texts of the Labs LE Audio bitrate card and its "switched" notice.</summary>
internal static class LeAudioCodecText
{
    private static string Number(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);

    public static string Preset(LeAudioCodecPreset preset) => preset.IsWindowsDefault
        ? Loc.Get("LeAudioCodec_PresetDefault")
        : Loc.Format("LeAudioCodec_PresetFormat", Number(preset.Kbps), Number(preset.FrameUs / 1000.0));

    public static string Mode(LeAudioLatencyMode mode) => Loc.Get($"LeAudioCodec_Mode_{mode}");

    /// <summary>"124.8 kbps · 7.5 ms frames · low latency (at most 15 ms, 5 retransmissions)".</summary>
    public static string Link(LeAudioLinkConfig link) => Loc.Format("LeAudioCodec_LinkFormat", Number(link.Kbps),
        Number(link.FrameUs / 1000.0), Loc.Get($"LeAudioCodec_ModeName_{link.Mode}"), link.LatencyMs, link.Retransmissions);
}
