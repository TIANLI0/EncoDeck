// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;

namespace EncoDeck.Core.State;

/// <summary>
/// Immutable snapshot of everything EncoDeck knows about the active device.
/// Every surface (main window, tray panel, connection popup) renders from this single source.
/// </summary>
public sealed record DeviceState
{
    public static DeviceState Empty { get; } = new();

    public DeviceIdentity? Device { get; init; }

    public IDeviceProfile? Profile { get; init; }

    public DeviceConnectionState Connection { get; init; } = DeviceConnectionState.NotDetected;

    /// <summary>At least one bonded LE earbud has an ACL connection; codec/streaming is not verified.</summary>
    public bool IsLeAudio { get; init; }

    public LeAudioLinkState LeAudioLinks { get; init; } = LeAudioLinkState.Empty;

    /// <summary>The earbuds were switched to LE Audio on this PC, whether or not that link is up now.</summary>
    public bool IsLeAudioBonded { get; init; }

    /// <summary>A2DP Windows Bridge is connecting or streaming to this device over its own adapter; null otherwise.</summary>
    public BridgeAudioLink? Bridge { get; init; }

    /// <summary>Only A2DP Windows Bridge's adapter is linked to the earbuds (Windows is not): control runs through it.</summary>
    public bool LinkViaBridge { get; init; }

    public BatteryStatus Battery { get; init; } = BatteryStatus.Unknown;

    public Observed<WearStatus>? Wear { get; init; }

    public Observed<NoiseControlState>? NoiseControl { get; init; }

    public Observed<EqualizerState>? Equalizer { get; init; }

    public ImmutableDictionary<DeviceToggle, Observed<bool>> Toggles { get; init; } = ImmutableDictionary<DeviceToggle, Observed<bool>>.Empty;

    /// <summary>Multi-value settings reported by the device, keyed by <see cref="DeviceOption.Id"/>.</summary>
    public ImmutableDictionary<string, Observed<DeviceOption>> Options { get; init; } = ImmutableDictionary<string, Observed<DeviceOption>>.Empty;

    public Observed<FirmwareInfo>? Firmware { get; init; }

    /// <summary>Accepted A2DP codec reported by the earbuds; not independent proof of the PC's negotiated stream.</summary>
    public Observed<AudioCodec>? Codec { get; init; }

    /// <summary>Raw OPPO codec id from a successful query, including values rejected for this model.</summary>
    public Observed<byte>? ReportedCodecId { get; init; }

    /// <summary>
    /// Windows logs its A2DP codec on this PC (one-time authorization): <see cref="WindowsCodec"/> is shown, never the
    /// earbuds' own <see cref="Codec"/> report.
    /// </summary>
    public bool WindowsCodecLogged { get; init; }

    /// <summary>The codec Windows' A2DP driver used for this device's latest stream ("AAC", "LDAC"); null when none was logged.</summary>
    public string? WindowsCodec { get; init; }

    /// <summary>24-bit product id reported by the device (e.g. 0x06EC10 for Enco X4).</summary>
    public uint? ProductId { get; init; }

    /// <summary>Colour variant reported by the device; selects colour-specific product images.</summary>
    public int? ColorId { get; init; }

    /// <summary>
    /// Raw control-capability bitmap the earbuds last reported. Remembered across sessions so the
    /// settings offered (which depend on it) are the same from the start of every connection.
    /// </summary>
    public byte[]? ControlCapabilities { get; init; }

    /// <summary>Lower of the two earbuds' running firmware versions, as last reported (remembered like <see cref="ControlCapabilities"/>).</summary>
    public int? BudFirmware { get; init; }

    public Observed<SpatialAudioMode>? Spatial { get; init; }

    public Observed<GameSoundState>? GameSound { get; init; }

    /// <summary>Prompt (alert sound) volume in the model's native range (e.g. 1–10 or 0–100).</summary>
    public Observed<int>? PromptVolume { get; init; }

    /// <summary>Device gain level in ascending order: 0 = low, 1 = medium, 2 = high.</summary>
    public Observed<int>? Gain { get; init; }

    public Observed<BassEngineState>? BassEngine { get; init; }

    public Observed<IReadOnlyList<KeyFunction>>? KeyFunctions { get; init; }

    public Observed<NoiseCycleState>? NoiseCycle { get; init; }

    /// <summary>Level chosen by smart (auto) noise cancelling right now, e.g. "deep".</summary>
    public string? SmartLevelId { get; init; }

    public Observed<IReadOnlyList<SourceDevice>>? SourceDevices { get; init; }

    public Observed<PreferredDeviceState>? PreferredDevice { get; init; }

    public Observed<IReadOnlyList<CustomEqualizer>>? CustomEqualizers { get; init; }

    /// <summary>Head gestures for calls: 0 = nod answers / shake declines, 1 = the reverse.</summary>
    public Observed<int>? HeadMotionType { get; init; }

    /// <summary>Present when the earbuds answer the LE/BR switch query, i.e. their firmware can run LE Audio.</summary>
    public Observed<LeAudioSwitchState>? LeAudioSwitch { get; init; }

    public FitTestResult? FitTest { get; init; }

    public bool IsFitTestRunning { get; init; }

    public bool IsFindingEarbuds { get; init; }

    public ImmutableList<PendingCommand> PendingCommands { get; init; } = [];

    /// <summary>When the device last sent us anything on the control channel.</summary>
    public DateTimeOffset? LastResponseAt { get; init; }

    public StateSource? LastUpdateSource { get; init; }

    public DeviceError? LastError { get; init; }

    /// <summary>Monotonic revision, incremented by the store on every change.</summary>
    public long Revision { get; init; }

    public bool IsPending(string descriptionPrefix) =>
        PendingCommands.Any(p => p.Description.StartsWith(descriptionPrefix, StringComparison.Ordinal));
}
