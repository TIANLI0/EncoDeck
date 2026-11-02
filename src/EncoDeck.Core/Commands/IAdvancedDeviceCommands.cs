// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;

namespace EncoDeck.Core.Commands;

public enum MultiConnectOperation
{
    Connect = 1,
    Disconnect = 2,
    /// <summary>Remove (unpair) a device that is not connected.</summary>
    Remove = 3,
}

/// <summary>
/// Device-changing commands beyond the basics. Every implementation confirms with the earbuds and
/// re-reads the affected state, the same way the official client does.
/// </summary>
public interface IAdvancedDeviceCommands
{
    Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default);

    Task<CommandResult> SetGameSoundAsync(int type, CancellationToken cancellationToken = default);

    /// <summary>Re-reads game mode, low latency and the game-sound type (read only).</summary>
    Task<CommandResult> RefreshGameModeAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default);

    /// <summary>Sets device gain by ascending level (0 = low, 1 = medium, 2 = high).</summary>
    Task<CommandResult> SetGainAsync(int level, CancellationToken cancellationToken = default);

    Task<CommandResult> SetBassEngineLevelAsync(int level, CancellationToken cancellationToken = default);

    Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default);

    Task<CommandResult> SetNoiseCycleAsync(EarSide side, uint modeMask, CancellationToken cancellationToken = default);

    Task<CommandResult> SetHeadMotionTypeAsync(int type, CancellationToken cancellationToken = default);

    Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default);

    /// <summary>Plays the fit-test tone and waits for the result (stored in <see cref="DeviceState.FitTest"/>).</summary>
    Task<CommandResult> RunFitTestAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> MultiConnectAsync(MultiConnectOperation operation, byte[] address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the earbuds' LE/BR mode switch (<c>0x0433</c>). The official client sets it on when
    /// switching to LE Audio and off when restoring Classic; the firmware syncs it to both earbuds
    /// and persists it in NV.
    /// </summary>
    Task<CommandResult> SetLeBrSwitchAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells earbuds without the LE/BR switch that the host now uses LE Audio (OPPO feature <c>0x1F</c>);
    /// the firmware then shares this host's LE bond with the other earbud. <see cref="CommandStatus.NotConnected"/>
    /// while control does not run over LE.
    /// </summary>
    Task<CommandResult> ReportLeAudioTransportAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CommandResult.Unsupported("LE Audio transport report is not supported on this device."));

    /// <summary>Sets the preferred source device; null means automatic selection.</summary>
    Task<CommandResult> SetPreferredDeviceAsync(byte[]? address, CancellationToken cancellationToken = default);

    Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken cancellationToken = default);

    Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken cancellationToken = default);

    Task<CommandResult> AddCustomEqualizerAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> DeleteCustomEqualizerAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a device-reported <see cref="DeviceOption"/> and confirms it by readback. Brands without
    /// such options keep this default.
    /// </summary>
    Task<CommandResult> SetOptionAsync(string id, int value, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommandResult.Unsupported($"Option {id} is not supported on this device."));

    /// <summary>
    /// Restores the earbuds' factory settings, which also clears their paired devices; Windows then has to pair
    /// them again. Success means the earbuds took the command: they restart and drop the link, so nothing is read back.
    /// </summary>
    Task<CommandResult> FactoryResetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CommandResult.Unsupported("Factory reset is not supported on this device."));
}
