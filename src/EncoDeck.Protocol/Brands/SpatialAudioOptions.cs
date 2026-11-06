// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Huawei;
using EncoDeck.Protocol.Brands.Moondrop;
using EncoDeck.Protocol.Brands.Xiaomi;
using EncoDeck.Protocol.Oppo.Models;

namespace EncoDeck.Protocol.Brands;

/// <summary>
/// The spatial audio buttons a device offers. The home page row and the sound page card both use this,
/// so an entry is shown exactly when its page has something to switch.
/// </summary>
/// <param name="Modes">The selectable modes in display order; empty when spatial audio is not offered.</param>
/// <param name="LabelsFixed">
/// Whether mode 1 is labelled "fixed" (it sits beside head tracking); OPPO spatial V1 has no head tracking, so it reads "on".
/// </param>
public sealed record SpatialAudioOptions(IReadOnlyList<SpatialAudioMode> Modes, bool LabelsFixed)
{
    public static SpatialAudioOptions None { get; } = new([], false);

    public bool IsAvailable => Modes.Count > 0;

    /// <param name="profile">The connected device's profile.</param>
    /// <param name="has">Whether a capability is available (the caller's view, including experimental gating).</param>
    public static SpatialAudioOptions For(IDeviceProfile? profile, Func<DeviceCapability, bool> has)
    {
        if (profile is null || !has(DeviceCapability.SpatialAudio)) return None;
        return profile switch
        {
            // Xiaomi earbuds report off / on / head tracking in one confirmed setting (config 29).
            XiaomiDeviceProfile when has(DeviceCapability.HeadTracking) => Of(true, 0, 1, 2),
            MoondropProfile => has(DeviceCapability.HeadTracking) ? Of(true, 0, 1, 2) : Of(true, 0, 1),
            // HUAWEI: offered once the earbuds reported a spatial state, minus modes they did not confirm this connection.
            HuaweiDeviceProfile huawei => huawei.SpatialModes.Count > 0 ? new([.. huawei.SpatialModes.Order()], true) : None,
            // OPPO: the model's spatial types; head tracking needs spatial V2.
            OppoDeviceProfile { Definition: var model } => new(
                [.. model.SpatialTypes.Where(t => t is 0 or 1 || (t == 2 && model.HasSpatialAudioV2)).Distinct().Order().Select(t => (SpatialAudioMode)t)],
                model.HasSpatialAudioV2),
            _ => None,
        };
    }

    private static SpatialAudioOptions Of(bool labelsFixed, params int[] types) => new([.. types.Select(t => (SpatialAudioMode)t)], labelsFixed);
}
