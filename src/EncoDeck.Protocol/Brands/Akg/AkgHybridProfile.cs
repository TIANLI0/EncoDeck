// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Akg;

/// <summary>What the last noise-control probe of a connected N5 / N9 Hybrid found.</summary>
public enum AkgAncStatus
{
    /// <summary>Not connected yet: nothing is claimed.</summary>
    NotProbed,

    /// <summary>The experimental opt-in is off, so nothing was sent.</summary>
    NeedsOptIn,

    /// <summary>The headset answered the ANC status read.</summary>
    Ready,

    /// <summary>The read was sent but not answered usably; <see cref="AkgAncProbe.Detail"/> says how.</summary>
    Failed,
}

public sealed record AkgAncProbe(AkgAncStatus Status, bool Ambient = false, string Detail = "")
{
    public static AkgAncProbe NotProbed { get; } = new(AkgAncStatus.NotProbed);
}

public sealed class AkgHybridProfile(bool earbuds, AkgAncProbe? anc = null) : IControllableProfile
{
    private readonly AkgAncProbe _anc = anc ?? AkgAncProbe.NotProbed;

    public AkgAncProbe Anc => _anc;
    public string Brand => "akg";
    public string ModelId => earbuds ? "akg-n5hybrid" : "akg-n9hybrid";
    public string DisplayName => earbuds ? "AKG N5 Hybrid" : "AKG N9 Hybrid";
    public string ImageKey => ModelId;
    public DeviceForm Form => earbuds ? DeviceForm.Earbuds : DeviceForm.Headphones;
    public bool IsGeneric => false;
    public IDeviceCapabilities Capabilities => DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, "RACE battery 0x0CD6 / charging 0x0009 / case 0x000B, observed on a real N9 Hybrid (kalicyh/Raito); Windows validation pending.")
        .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Supported, "RACE GET_CHARGE_INFO 0x0009 per role.")
        .Set(DeviceCapability.NoiseCancellation, _anc.Status switch
        {
            AkgAncStatus.Ready => CapabilitySupport.Supported,
            AkgAncStatus.NeedsOptIn => CapabilitySupport.Experimental,
            AkgAncStatus.Failed => CapabilitySupport.Unsupported,
            _ => CapabilitySupport.Unknown,
        }, _anc.Status switch
        {
            AkgAncStatus.NeedsOptIn => "Experimental: enable the AKG Hybrid experimental features, then reconnect, to query noise control (Airoha RACE 0x0901 module 5 / 0x0E06; not yet confirmed on an AKG headset).",
            AkgAncStatus.NotProbed => "Queried once the headset is connected and the experimental features are enabled.",
            _ => _anc.Detail,
        }).Build();
    private static readonly NoiseControlOption OffOption = new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported);
    private static readonly NoiseControlOption AncOption = new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported);
    private static readonly NoiseControlOption AmbientOption = new(NoiseControlMode.Transparency, null, "Ambient sound", "NoiseMode_Transparency", CapabilitySupport.Supported);

    /// <summary>Ambient is offered only once the chip's pass-through numbering is known.</summary>
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => _anc.Status != AkgAncStatus.Ready ? []
        : _anc.Ambient ? [OffOption, AncOption, AmbientOption] : [OffOption, AncOption];
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];
    /// <summary>
    /// Classic RACE / SPP services first (the paired classic address is known; the N9 Hybrid also
    /// advertises a separate "-LE" peripheral), then RACE over BLE GATT as seen on a real N9 Hybrid.
    /// </summary>
    public IReadOnlyList<Guid> ControlServiceUuids => [AkgBleUuids.RaceSpp, AkgBleUuids.Spp, AkgBleUuids.RaceService];
    public int MatchScore(DeviceIdentity identity)
    {
        if (identity.Transport != DeviceTransport.Bluetooth) return 0;
        string actual = Normalize(identity.Name);
        string prefix = earbuds ? "akgn5" : "akgn9";
        return actual == prefix || actual == prefix + "hybrid" ? 110 : 0;
    }
    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) => new AkgHybridController(channel, context, earbuds);
}
