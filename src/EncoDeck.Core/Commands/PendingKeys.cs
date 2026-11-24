// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;

namespace EncoDeck.Core.Commands;

/// <summary>
/// <see cref="PendingCommand.Description"/> values for device-changing commands. The UI disables a control
/// while <see cref="DeviceState.IsPending"/> matches its key (a prefix match), so controllers and UI must
/// agree on these exact strings.
/// </summary>
public static class PendingKeys
{
    public const string NoiseControl = "noise-control";

    public const string Equalizer = "equalizer";

    /// <summary>Custom EQ select / save / add / delete; brand-specific suffixes still match this prefix.</summary>
    public const string CustomEqualizer = "custom-eq";

    /// <summary>Prefix matching every toggle write.</summary>
    public const string AnyToggle = "toggle:";

    public const string PromptVolume = "prompt-volume";

    public const string PromptTone = "prompt-tone";

    public const string Gain = "gain";

    public const string GameSound = "game-sound";

    public const string BassEngine = "bass-engine";

    public const string Spatial = "spatial";

    /// <summary>A single button/gesture assignment.</summary>
    public const string KeyFunction = "key-function";

    /// <summary>Gesture writes that replace the whole gesture table.</summary>
    public const string Gesture = "gesture";

    public const string NoiseCycle = "noise-cycle";

    public const string HeadMotion = "head-motion";

    public const string MultiConnect = "multi-connect";

    public const string PreferredDevice = "preferred-device";

    public const string LeBrSwitch = "le-br-switch";

    public const string FindEarbuds = "find-earbuds";

    public const string FactoryReset = "factory-reset";

    /// <summary>Brand-specific multi-value settings written through <c>SetOptionAsync</c>.</summary>
    public static string Option(string id) => "option:" + id;

    public static string Toggle(DeviceToggle toggle) => AnyToggle + toggle;
}
