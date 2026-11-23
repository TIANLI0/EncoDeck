// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;

namespace EncoDeck.App.Controls;

/// <summary>
/// Where a device setting is shown (issue #16): every brand's switches and reported options are sorted
/// into a few named cards on the page they belong to, instead of one "More settings" list.
/// </summary>
public enum SettingGroup
{
    /// <summary>Not listed: the setting has its own card (game mode, dual connection, bass…) or lives in noise control.</summary>
    None,

    /// <summary>Home page, under noise control: levels of a noise mode that are not one of its segmented rows (Sony's ambient level).</summary>
    NoiseControl,

    /// <summary>Sound page: codec and processing switches that change audio quality (Hi-Res, LDAC, aptX, LC3, DSEE, gain).</summary>
    AudioQuality,

    /// <summary>Sound page: how sound adapts to the listener (sound scene, surround, volume that follows the surroundings).</summary>
    Listening,

    /// <summary>Controls page: how the earbuds are operated (touch lock, button assignments, voice commands).</summary>
    Operation,

    /// <summary>Controls page: wear detection and calls (in-ear detection, sleep, speak-to-chat, auto answer, microphone).</summary>
    WearAndCalls,

    /// <summary>Controls page: prompts, indicator light and power.</summary>
    General,

    /// <summary>Connections page: pairing and link behaviour (Swift Pair, do not disturb while on 2.4 GHz).</summary>
    Connection,
}

public static class SettingGroups
{
    public static SettingGroup Of(DeviceToggle toggle) => toggle switch
    {
        DeviceToggle.HiRes or DeviceToggle.Ldac or DeviceToggle.Lc3 or DeviceToggle.AptXAdaptive or DeviceToggle.AptXLossless
            or DeviceToggle.Upscaling or DeviceToggle.ChannelSwap => SettingGroup.AudioQuality,
        DeviceToggle.SpatialAudio or DeviceToggle.AutoVolume => SettingGroup.Listening,
        DeviceToggle.TouchControls or DeviceToggle.VoiceCommands => SettingGroup.Operation,
        DeviceToggle.InEarDetection or DeviceToggle.SleepDetection or DeviceToggle.SpeakToChat or DeviceToggle.AutoAnswerCalls => SettingGroup.WearAndCalls,
        DeviceToggle.PromptTone or DeviceToggle.IndicatorLight => SettingGroup.General,
        DeviceToggle.SwiftPair or DeviceToggle.DoNotDisturb => SettingGroup.Connection,
        // Game mode, low latency, dual connection, head gestures and tracking, and the bass engine have their own cards.
        _ => SettingGroup.None,
    };

    /// <summary>
    /// Device-reported options by id. Sub-modes and levels of a noise mode are shown with noise control on the
    /// home page; an unknown option falls back to <see cref="SettingGroup.General"/> so it is never lost.
    /// </summary>
    public static SettingGroup Of(DeviceOption option) => option.Id switch
    {
        // EarFun's sub-modes are the segmented rows of noise control itself.
        "earfun-sub" or "earfun-ambient" => SettingGroup.None,
        "sony-ambient-level" => SettingGroup.NoiseControl,
        "edifier-scene-mode" => SettingGroup.Listening,
        "earfun-touchlock" => SettingGroup.Operation,
        var id when id.StartsWith("sony-key-", StringComparison.Ordinal) => SettingGroup.Operation,
        "earfun-mic" => SettingGroup.WearAndCalls,
        _ => SettingGroup.General,
    };
}
