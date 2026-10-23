// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.Commands;

/// <summary>Motor outputs an actuator device can drive.</summary>
public enum ActuatorChannel
{
    Vibration,
    Linear,
    Rotary,
}

/// <summary>
/// Commands for body-contact actuator devices, kept apart from the headset commands on
/// <see cref="IDeviceController"/> (which such devices answer with <see cref="CommandStatus.Unsupported"/>).
/// The devices do not acknowledge writes: <see cref="CommandStatus.Success"/> only means the
/// frame was written, never that the device confirmed it.
/// </summary>
public interface IActuatorCommands
{
    IReadOnlyList<ActuatorChannel> Channels { get; }

    /// <summary>
    /// Sets one channel to <paramref name="strength"/> (clamped to 0–100). Nonzero values require
    /// experimental features; rapid calls are rate limited and only the latest value is sent.
    /// </summary>
    Task<CommandResult> SetIntensityAsync(ActuatorChannel channel, int strength, CancellationToken cancellationToken = default);

    /// <summary>
    /// Zeroes every channel. Never gated or rate limited, supersedes pending intensity changes, and
    /// is deliberately not cancellable by the caller (it is bounded by an internal timeout instead).
    /// </summary>
    Task<CommandResult> StopAllAsync();
}
