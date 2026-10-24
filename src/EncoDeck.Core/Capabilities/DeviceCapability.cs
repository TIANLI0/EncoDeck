// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.Capabilities;

/// <summary>Device features EncoDeck knows how to reason about.</summary>
public enum DeviceCapability
{
    BatteryLevel,
    ChargingStatus,
    NoiseCancellation,
    AdaptiveNoiseCancellation,
    NoiseCancellationLevel,
    Equalizer,
    CustomEqualizer,
    SpatialAudio,
    HeadTracking,
    GamingMode,
    InEarDetection,
    TouchCustomization,
    DualDeviceConnection,
    FindMyEarbuds,
    FitTest,
    PromptVolume,
    BassEngine,
    FirmwareInformation,
    Gain,
    IndicatorLight,
    PromptTone,
    HiResCodec,
    Ldac,
    Lc3,
    ChannelSwap,
}

/// <summary>
/// How confident we are that a capability works on a given model.
/// Only <see cref="Supported"/> is shown as usable by default; <see cref="Experimental"/>
/// requires the user to opt in. <see cref="Unknown"/> is never offered as a control.
/// </summary>
public enum CapabilitySupport
{
    Unknown = 0,
    Unsupported,
    Experimental,
    Supported,
}
