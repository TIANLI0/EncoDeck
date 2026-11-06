// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Huawei;
using EncoDeck.Protocol.Oppo.Models;

namespace EncoDeck.Tests.Protocol;

/// <summary>The home row and the sound card share these options, so a visible row always has buttons behind it.</summary>
public class SpatialAudioOptionsTests
{
    private static HuaweiModel Huawei(string id) => HuaweiModels.All.Single(m => m.ModelId == id);

    private static SpatialAudioOptions For(HuaweiDeviceProfile profile) =>
        SpatialAudioOptions.For(profile, c => profile.Capabilities.IsAvailable(c, false));

    [Theory]
    [InlineData("huawei-freeclip-2")]
    [InlineData("huawei-freebuds-7")]
    [InlineData("huawei-freebuds-7i")]
    [InlineData("huawei-freebuds-pro-5")]
    public void Huawei_OffersCapturedModesOnlyAfterReadBack(string id)
    {
        Assert.Same(SpatialAudioOptions.None, For(new HuaweiDeviceProfile(Huawei(id))));
        var options = For(new HuaweiDeviceProfile(Huawei(id), spatialConfirmed: true));
        Assert.True(options.IsAvailable);
        Assert.True(options.LabelsFixed);
        Assert.Equal([SpatialAudioMode.Off, SpatialAudioMode.Fixed, SpatialAudioMode.HeadTracking], options.Modes);
    }

    [Fact]
    public void Huawei_EveryModelOffersAllModesOnceConfirmedAndNothingBefore()
    {
        foreach (var model in HuaweiModels.All.Append(HuaweiModels.Generic))
        {
            Assert.False(For(new HuaweiDeviceProfile(model)).IsAvailable);
            var options = For(new HuaweiDeviceProfile(model, spatialConfirmed: true));
            Assert.Equal([SpatialAudioMode.Off, SpatialAudioMode.Fixed, SpatialAudioMode.HeadTracking], options.Modes);
            Assert.True(options.LabelsFixed);
        }
    }

    [Fact]
    public void Huawei_HomeRowAndSoundCardAgree()
    {
        // Wherever the capability opens, there are buttons to show.
        foreach (var model in HuaweiModels.All)
        {
            var profile = new HuaweiDeviceProfile(model, spatialConfirmed: true);
            Assert.Equal(profile.Capabilities.IsAvailable(DeviceCapability.SpatialAudio, false), For(profile).IsAvailable);
        }
    }

    [Fact]
    public void Oppo_FollowsModelTypesAndHidesHeadTrackingWithoutV2()
    {
        var model = OppoModels.FindByProductId(0x068C10u)!;
        var v2 = SpatialAudioOptions.For(new OppoDeviceProfile(model with { SpatialTypes = [0, 1, 2], HasSpatialAudioV2 = true }), _ => true);
        Assert.Equal([SpatialAudioMode.Off, SpatialAudioMode.Fixed, SpatialAudioMode.HeadTracking], v2.Modes);
        Assert.True(v2.LabelsFixed);
        var v1 = SpatialAudioOptions.For(new OppoDeviceProfile(model with { SpatialTypes = [0, 1, 2], HasSpatialAudioV2 = false }), _ => true);
        Assert.Equal([SpatialAudioMode.Off, SpatialAudioMode.Fixed], v1.Modes);
        Assert.False(v1.LabelsFixed);
        Assert.False(SpatialAudioOptions.For(new OppoDeviceProfile(model with { SpatialTypes = [] }), _ => true).IsAvailable);
        Assert.False(SpatialAudioOptions.For(new OppoDeviceProfile(model), _ => false).IsAvailable);
    }

    [Fact]
    public void NoProfile_OffersNothing() => Assert.False(SpatialAudioOptions.For(null, _ => true).IsAvailable);
}
