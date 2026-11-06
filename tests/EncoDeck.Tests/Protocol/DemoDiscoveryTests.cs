// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;

namespace EncoDeck.Tests.Protocol;

public sealed class DemoDiscoveryTests
{
    private static IDeviceProfile After(string name, string? profileId = null)
    {
        var profiles = BuiltInProfiles.Create();
        var profile = profileId is not null ? profiles.Single(p => p.ModelId == profileId)
            : new DeviceProfileRegistry(profiles).Match(new DeviceIdentity("demo", name, new BluetoothAddress(0)))!;
        return DemoDiscovery.AfterSession(profile, name);
    }

    private static CapabilitySupport Support(IDeviceProfile profile, DeviceCapability capability) => profile.Capabilities.Get(capability).Support;

    [Fact]
    public void XiaomiUsesTheNamedModelsCatalogAnswers()
    {
        var profile = After("Redmi Buds 5 Pro");
        Assert.Equal(CapabilitySupport.Supported, Support(profile, DeviceCapability.NoiseCancellation));
        Assert.Equal(CapabilitySupport.Supported, Support(profile, DeviceCapability.Equalizer));
        Assert.NotEmpty(profile.EqualizerPresets);
        Assert.Contains(DeviceToggle.GamingMode, ((IControllableProfile)profile).Toggles);
    }

    [Fact]
    public void SonyGainsNoiseEqualizerAndEarbudWearDetection()
    {
        var earbuds = After("WF-1000XM5", "sony-wf1000xm5");
        Assert.Equal(CapabilitySupport.Supported, Support(earbuds, DeviceCapability.NoiseCancellation));
        Assert.NotEmpty(earbuds.EqualizerPresets);
        Assert.Contains(DeviceToggle.InEarDetection, ((IControllableProfile)earbuds).Toggles);
        Assert.Empty(((IControllableProfile)After("WH-1000XM5", "sony-wh1000xm5")).Toggles);
        Assert.Empty(After("AKG N400NC", "akg-n400nc").EqualizerPresets);
    }

    [Fact]
    public void HuaweiConfirmsSpatialAudio()
    {
        Assert.Equal(CapabilitySupport.Supported, Support(After("HUAWEI FreeClip 2"), DeviceCapability.SpatialAudio));
        // Models without community records open too once the feature is confirmed.
        Assert.Equal(CapabilitySupport.Supported, Support(After("HUAWEI FreeBuds Pro 3"), DeviceCapability.SpatialAudio));
    }

    [Fact]
    public void MoondropDiscoversOnlyCatalogFeatures()
    {
        var pudding = After("MOONDROP PUDDING");
        Assert.Equal(CapabilitySupport.Supported, Support(pudding, DeviceCapability.Gain));
        Assert.Equal(CapabilitySupport.Supported, Support(pudding, DeviceCapability.TouchCustomization));
        Assert.Equal(CapabilitySupport.Supported, Support(pudding, DeviceCapability.IndicatorLight));
        var edge = After("MOONDROP EDGE");
        Assert.Equal(CapabilitySupport.Supported, Support(edge, DeviceCapability.Gain));
        Assert.Equal(CapabilitySupport.Unsupported, Support(edge, DeviceCapability.TouchCustomization));
        Assert.Equal(CapabilitySupport.Experimental, Support(edge, DeviceCapability.PromptVolume));
    }

    [Fact]
    public void OtherBrandsAreUnchanged()
    {
        var profile = BuiltInProfiles.Create().Single(p => p.ModelId == "edifier-w820nb");
        Assert.Same(profile, DemoDiscovery.AfterSession(profile, "EDIFIER W820NB"));
    }
}
