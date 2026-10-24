// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;

namespace EncoDeck.Core.Profiles;

/// <summary>
/// A model in the experimental <see cref="DeviceCategory.Cup"/> category: a BLE actuator driven
/// through <see cref="IActuatorCommands"/>. Such models have no headset capabilities.
/// </summary>
public interface ICupProfile : IDeviceProfile
{
    DeviceCategory IDeviceProfile.Category => DeviceCategory.Cup;

    /// <summary>Brand id, such as <c>dokidoki</c>.</summary>
    string Brand { get; }

    /// <summary>Outputs this model drives.</summary>
    IReadOnlyList<ActuatorChannel> Channels { get; }

    /// <summary>Channels whose motor sits in a removable sleeve; they do nothing without it.</summary>
    IReadOnlyList<ActuatorChannel> ChannelsNeedingSleeve => [];
}
