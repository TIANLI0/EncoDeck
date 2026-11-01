// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

/// <summary>Boolean device-side settings. Each maps to a capability in the device profile.</summary>
public enum DeviceToggle
{
    /// <summary>Game mode master switch.</summary>
    GamingMode,
    InEarDetection,
    SpatialAudio,
    HeadTracking,
    DualDeviceConnection,
    /// <summary>Low game latency (sub-option of game mode).</summary>
    LowLatency,
    /// <summary>Pause playback when the wearer falls asleep.</summary>
    SleepDetection,
    /// <summary>Windows Swift Pair advertising.</summary>
    SwiftPair,
    /// <summary>On-earbud voice commands.</summary>
    VoiceCommands,
    /// <summary>Answer/decline calls by nodding or shaking the head.</summary>
    HeadGestures,
    /// <summary>BassWave bass engine.</summary>
    BassEngine,
    /// <summary>Device-side high-resolution codec mode; writability and mutual exclusions depend on the brand.</summary>
    HiRes,
    IndicatorLight,
    PromptTone,
    /// <summary>Device-side LDAC switch; not a Windows codec installation.</summary>
    Ldac,
    /// <summary>Device-side LC3 switch; independent of the Windows LE Audio transport.</summary>
    Lc3,
    /// <summary>Swap the left and right output channels on the device.</summary>
    ChannelSwap,
    /// <summary>Adjust playback volume as ambient noise changes (OPPO feature 28).</summary>
    AutoVolume,
    /// <summary>Earbud-side automatic answering of incoming calls (Xiaomi config 3).</summary>
    AutoAnswerCalls,
    /// <summary>Pause playback and let ambient sound in when the wearer starts talking (Sony Speak-to-Chat).</summary>
    SpeakToChat,
    /// <summary>Device-side upscaling of compressed audio (Sony DSEE).</summary>
    Upscaling,
    /// <summary>Touch panel / touch sensor operation on or off (off = touch locked; Sony, Samsung).</summary>
    TouchControls,
    /// <summary>Headset-side Bluetooth do-not-disturb while the 2.4 GHz link is in use.</summary>
    DoNotDisturb,
    /// <summary>Device-side aptX Adaptive switch; not a Windows codec installation.</summary>
    AptXAdaptive,
    /// <summary>Device-side aptX Lossless switch; not a Windows codec installation.</summary>
    AptXLossless,
}

/// <summary>Firmware / model information read from the device.</summary>
public sealed record FirmwareInfo(string? Version, string? ModelCode, IReadOnlyDictionary<string, string> Extra)
{
    public static FirmwareInfo Empty { get; } = new(null, null, new Dictionary<string, string>());
}

/// <summary>Device-side equalizer state. This is the EQ inside the earbuds, never a Windows audio effect.</summary>
public sealed record EqualizerState(string? ActivePresetId, int? RawPresetValue, IReadOnlyList<int>? Bands = null);
