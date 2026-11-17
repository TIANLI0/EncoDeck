// SPDX-License-Identifier: GPL-3.0-only
// Gesture layout from the independent Android app "我的X5s Pro" by shui-tiao
// (https://github.com/shui-tiao/my-x5s-pro, GPL-3.0), verified on
// HONOR CHOICE Earbuds X5s Pro (USFY). Every gesture lives in the 01 39 / 01 3A property channel.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Wingcloud;

/// <summary>
/// Pinch gestures of the X5s Pro: service 4 holds the pinch assignments (left and right separately),
/// service 5 the hold ones. <see cref="BrandGestureSlot.Button"/> is the property index.
/// </summary>
public static class WingcloudGestures
{
    public const int PinchService = 4;
    public const int HoldService = 5;

    public static readonly string[] Names =
    [
        "HONOR CHOICE Earbuds X5s Pro",
        "HONOR CHOICE X5s Pro",
        "荣耀亲选耳机X5s Pro",
        "荣耀亲选耳机 X5s Pro",
        "Earbuds X5s Pro",
    ];

    /// <summary>Pinch twice: next track, voice assistant, or nothing.</summary>
    private static readonly BrandGestureChoice[] Double =
    [
        new(0, ["Function_6"]),
        new(1, ["Function_3"]),
        new(2, ["Function_0"]),
    ];

    /// <summary>Pinch three times: previous track, game mode, or nothing.</summary>
    private static readonly BrandGestureChoice[] Triple =
    [
        new(0, ["Function_5"]),
        new(1, ["Function_17"]),
        new(2, ["Function_0"]),
    ];

    /// <summary>
    /// Hold = the noise-cycle combination. Property 5/1 stores the same firmware enum as <c>2B 18</c>
    /// (1 = NC+off, 2 = all three, 3 = NC+transparency, 4 = off+transparency), so both paths agree.
    /// </summary>
    private static readonly BrandGestureChoice[] Hold =
    [
        new(1, ["Wingcloud_Cycle_AncOff"], DeviceCapability.NoiseCancellation),
        new(2, ["Wingcloud_Cycle_All"], DeviceCapability.NoiseCancellation),
        new(3, ["Wingcloud_Cycle_AncTransparency"], DeviceCapability.NoiseCancellation),
        new(4, ["Wingcloud_Cycle_OffTransparency"], DeviceCapability.NoiseCancellation),
    ];

    /// <summary>
    /// The five addresses this firmware answers. Pinch-once (4/7), pinch-once during a call (4/0) and
    /// hold during a call (5/0) have no property on the X5s Pro and stay absent.
    /// </summary>
    public static IReadOnlyList<BrandGestureSlot> Slots { get; } =
    [
        new(EarSide.Left, 2, GestureAction.DoubleSqueeze, "Gesture_Tap2", Double),
        new(EarSide.Right, 3, GestureAction.DoubleSqueeze, "Gesture_Tap2", Double),
        new(EarSide.Left, 5, GestureAction.TripleSqueeze, "Gesture_Tap3", Triple),
        new(EarSide.Right, 6, GestureAction.TripleSqueeze, "Gesture_Tap3", Triple),
        new(EarSide.Both, 1, GestureAction.SqueezeAndHold, "Gesture_Hold", Hold),
    ];

    /// <summary>The property index a slot is stored at (the slot's button field).</summary>
    public static int? Property(BrandGestureSlot slot) => slot.Button;

    /// <summary>
    /// Hold lives on the pinch service too (4/1); service 5 is not exposed by this firmware.
    /// </summary>
    public static int Service(BrandGestureSlot slot) => PinchService;

    /// <summary>
    /// Hold-cycle combinations of <c>2B 18</c>. The wire value is a firmware enum, not a count; the
    /// phone app enumerates all four and only the left earbud's field takes effect.
    /// </summary>
    public static readonly (int Value, string Name, string Key, uint Mask)[] Cycles =
    [
        (2, "All three", "Wingcloud_Cycle_All", NoiseCycleModes.Off | NoiseCycleModes.NoiseCancellation | NoiseCycleModes.Transparency),
        (1, "Noise cancelling + off", "Wingcloud_Cycle_AncOff", NoiseCycleModes.Off | NoiseCycleModes.NoiseCancellation),
        (3, "Noise cancelling + transparency", "Wingcloud_Cycle_AncTransparency", NoiseCycleModes.NoiseCancellation | NoiseCycleModes.Transparency),
        (4, "Off + transparency", "Wingcloud_Cycle_OffTransparency", NoiseCycleModes.Off | NoiseCycleModes.Transparency),
    ];
}

/// <summary>Bits used in the long-hold noise cycle masks.</summary>
public static class NoiseCycleModes
{
    public const uint Off = 1;
    public const uint NoiseCancellation = 2;
    public const uint Transparency = 4;
}
