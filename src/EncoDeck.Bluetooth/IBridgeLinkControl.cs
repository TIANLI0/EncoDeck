// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Privacy;

namespace EncoDeck.Bluetooth;

/// <summary>
/// Connects and disconnects earbuds through BlueCaster: starts its connection profile for them, or stops its stream to
/// them. Used by <see cref="DeviceManager.ConnectDeviceAsync"/> and <see cref="DeviceManager.DisconnectDeviceAsync"/>
/// for earbuds BlueCaster carries.
/// </summary>
public interface IBridgeLinkControl
{
    /// <summary>Starts BlueCaster's connection profile for these earbuds (starting the core first when it can).</summary>
    Task<CommandResult> ConnectAsync(BluetoothAddress address, CancellationToken cancellationToken = default);

    /// <summary>Stops BlueCaster's stream to these earbuds; Ok when it does not stream to them.</summary>
    Task<CommandResult> DisconnectAsync(BluetoothAddress address, CancellationToken cancellationToken = default);
}
