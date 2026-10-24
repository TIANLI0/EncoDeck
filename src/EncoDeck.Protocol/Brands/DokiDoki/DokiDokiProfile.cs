// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.DokiDoki;

/// <summary>
/// Cup › DokiDoki › model. Deliberately not part of <see cref="BuiltInProfiles"/>: these devices
/// are BLE-only and never appear among paired classic devices, and they are not headsets. Every
/// headset capability is <see cref="CapabilitySupport.Unsupported"/>.
/// </summary>
public sealed class DokiDokiProfile : IControllableProfile, ICupProfile
{
    public const string BrandId = "dokidoki";

    /// <summary>Every model this brand knows, for a manual pick when the name does not identify one.</summary>
    public static IReadOnlyList<DokiDokiProfile> Models { get; } = [new(DokiDokiFamily.Meta2), new(DokiDokiFamily.Bh2Pro)];

    private static readonly IDeviceCapabilities NoHeadsetCapabilities = CreateCapabilities();

    public DokiDokiProfile(DokiDokiFamily family)
    {
        if (!Enum.IsDefined(family)) throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown DokiDoki family.");
        Family = family;
        Channels = DokiDokiCommands.SupportedChannels(family).Select(DokiDokiChannels.ToActuator).ToArray();
    }

    public DokiDokiFamily Family { get; }

    public IReadOnlyList<ActuatorChannel> Channels { get; }

    /// <summary>BH2PRO: the vibration motor is in the sleeve; without it vibration does nothing (confirmed on a device).</summary>
    public IReadOnlyList<ActuatorChannel> ChannelsNeedingSleeve => Family == DokiDokiFamily.Bh2Pro ? [ActuatorChannel.Vibration] : [];

    public string Brand => BrandId;

    public bool IsGeneric => false;

    public string ModelId => Family switch
    {
        DokiDokiFamily.Bh2Pro => "dokidoki-bh2pro",
        _ => "dokidoki-meta2",
    };

    public string DisplayName => Family switch
    {
        DokiDokiFamily.Bh2Pro => DokiDokiConstants.Bh2ProDeviceName,
        _ => DokiDokiConstants.Meta2DeviceName,
    };

    public string ImageKey => ModelId;

    public IDeviceCapabilities Capabilities => NoHeadsetCapabilities;

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => [];

    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];

    public IReadOnlyList<Guid> ControlServiceUuids => [DokiDokiConstants.ServiceUuid];

    /// <summary>
    /// Matches only names that identify this family. Names that carry a DokiDoki prefix but no
    /// known family (e.g. <c>TRYFUN-…</c>) match nothing; the user must pick the family explicitly.
    /// </summary>
    public int MatchScore(DeviceIdentity identity) => DokiDokiNames.DetectFamily(identity.Name) == Family ? 100 : 0;

    /// <summary>Profile for an advertised name, or null when the name does not identify a family.</summary>
    public static DokiDokiProfile? ForName(string? deviceName) =>
        DokiDokiNames.DetectFamily(deviceName) is { } family ? new DokiDokiProfile(family) : null;

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new DokiDokiDeviceController(channel, context, Family);

    private static IDeviceCapabilities CreateCapabilities()
    {
        var builder = DeviceCapabilities.CreateBuilder();
        foreach (var capability in Enum.GetValues<DeviceCapability>())
            builder.Set(capability, CapabilitySupport.Unsupported, "DokiDoki is an actuator device, not a headset.");
        return builder.Build();
    }
}

/// <summary>Maps between DokiDoki protocol channels and the Core actuator channels.</summary>
public static class DokiDokiChannels
{
    public static ActuatorChannel ToActuator(DokiDokiChannel channel) => channel switch
    {
        DokiDokiChannel.Vibration => ActuatorChannel.Vibration,
        DokiDokiChannel.Linear => ActuatorChannel.Linear,
        DokiDokiChannel.Rotary => ActuatorChannel.Rotary,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };

    public static DokiDokiChannel? FromActuator(ActuatorChannel channel) => channel switch
    {
        ActuatorChannel.Vibration => DokiDokiChannel.Vibration,
        ActuatorChannel.Linear => DokiDokiChannel.Linear,
        ActuatorChannel.Rotary => DokiDokiChannel.Rotary,
        _ => null,
    };
}
