// SPDX-License-Identifier: GPL-3.0-only
// Settings, readback rules and the find-earbuds wear check ported from BudsLink
// (https://github.com/maniacx/BudsLink, GPL-3.0), redmi-buds-bar by robin-liquidium
// (https://github.com/robin-liquidium/redmi-buds-bar, GPL-3.0), mebuds
// (https://github.com/paradox-ng/mebuds, GPL-3.0-or-later) and MiBudsClient
// (https://github.com/CesurPolat/MiBudsClient, GPL-3.0); see THIRD_PARTY_NOTICES.md.
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Xiaomi;

internal sealed partial class XiaomiDeviceController
{
    /// <summary>
    /// Settings read at session start, one per request: a single unsupported id fails (or stalls)
    /// a combined read on real earbuds.
    /// </summary>
    private static readonly ushort[] FeatureIds =
    [
        XiaomiConfig.Noise, XiaomiConfig.AdaptiveAnc, XiaomiConfig.EqualizerPreset, XiaomiConfig.CustomEqualizer,
        XiaomiConfig.DualConnection, XiaomiConfig.AutoAnswer, XiaomiConfig.Gestures, XiaomiConfig.WearState,
        XiaomiConfig.Spatial, XiaomiConfig.LowLatency, XiaomiConfig.PromptVolume,
    ];

    /// <summary>Discovery reads are short: ids a model lacks either fail fast or never answer.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(800);

    /// <summary>The write acknowledgement is only a fast path; the readback decides.</summary>
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Dictionary<ushort, byte[]> _configValues = [];

    private async Task ReadFeaturesAsync(bool discover, CancellationToken ct)
    {
        foreach (ushort id in FeatureIds)
        {
            if (!discover && !_profile.HasConfig(id)) continue;
            await ReadValueAsync(id, discover ? ProbeTimeout : RequestTimeout, ct).ConfigureAwait(false);
        }
        if (discover || _profile.HasConfig(XiaomiConfig.WearDetectionSwitch))
            await ReadWearDetectionAsync(discover ? ProbeTimeout : RequestTimeout, ct).ConfigureAwait(false);
    }

    private void ApplyFeatures(XiaomiFrame frame)
    {
        if (frame.OpCode is not (XiaomiProtocol.OpGetConfig or XiaomiProtocol.OpNotifyConfig)
            || !XiaomiConfigCodec.TryParse(frame.Payload, out var values)) return;
        foreach (var (id, value) in values)
            ApplyConfig(id, value, frame.OpCode == XiaomiProtocol.OpNotifyConfig ? StateSource.DeviceNotification : StateSource.QueryResponse);
    }

    private static bool IsValid(ushort id, byte[] value) => id switch
    {
        // A lone 0xFF is how the earbuds say "not supported".
        _ when value is [0xFF] => false,
        XiaomiConfig.Noise => value.Length == 2 && value[0] <= 2 && value[1] <= 31,
        XiaomiConfig.EqualizerPreset => value.Length == 1 && value[0] <= 31,
        XiaomiConfig.DualConnection or XiaomiConfig.LowLatency or XiaomiConfig.AutoAnswer or XiaomiConfig.AdaptiveAnc => value.Length == 1 && value[0] <= 1,
        XiaomiConfig.Spatial => value.Length == 1,
        XiaomiConfig.WearState => value.Length == 1 && value[0] <= 0x0F,
        XiaomiConfig.Gestures => XiaomiGestures.TryParse(value, out _),
        XiaomiConfig.CustomEqualizer => XiaomiCustomCurve.TryParse(value, out _),
        XiaomiConfig.PromptVolume => value.Length == 4 && value[3] == 1 && value[2] == 10 && value[0] >= 1 && value[0] <= 10,
        _ => false,
    };

    private void ApplyConfig(ushort id, byte[] value, StateSource source)
    {
        if (!IsValid(id, value)) return;
        lock (_gate) { _configValues[id] = value.ToArray(); _profile = _profile.WithConfig(id); }
        var profile = _profile;
        Store.TryUpdate(DeviceId, s =>
        {
            var next = s with { Profile = profile, LastUpdateSource = source };
            return id switch
            {
                XiaomiConfig.Noise or XiaomiConfig.AdaptiveAnc => NoiseState(next, source),
                XiaomiConfig.EqualizerPreset => CustomSelection(next with { Equalizer = Observed.From(new EqualizerState($"xiaomi-{value[0]}", value[0]), Now, source) }),
                XiaomiConfig.CustomEqualizer => CustomSelection(next),
                XiaomiConfig.DualConnection => next with { Toggles = s.Toggles.SetItem(DeviceToggle.DualDeviceConnection, Observed.From(value[0] == 1, Now, source)) },
                XiaomiConfig.AutoAnswer => next with { Toggles = s.Toggles.SetItem(DeviceToggle.AutoAnswerCalls, Observed.From(value[0] == 1, Now, source)) },
                XiaomiConfig.LowLatency => next with { Toggles = s.Toggles.SetItem(DeviceToggle.GamingMode, Observed.From(value[0] == 1, Now, source)) },
                XiaomiConfig.Spatial => next with { Spatial = Observed.From((value[0] & 1) == 0 ? SpatialAudioMode.Off : (value[0] & 8) != 0 ? SpatialAudioMode.HeadTracking : SpatialAudioMode.Fixed, Now, source) },
                XiaomiConfig.PromptVolume => next with { PromptVolume = Observed.From((int)value[0], Now, source) },
                XiaomiConfig.WearState => next with { Wear = Observed.From(new WearStatus((value[0] & 8) != 0, (value[0] & 4) != 0, (value[0] & 2) != 0, (value[0] & 1) != 0, null), Now, source) },
                XiaomiConfig.Gestures when XiaomiGestures.TryParse(value, out var groups) => next with { KeyFunctions = Observed.From(XiaomiGestures.ToKeyFunctions(groups), Now, source) },
                _ => next,
            };
        });
    }

    /// <summary>Noise state from config 11 and, where confirmed, the adaptive switch (config 37).</summary>
    private DeviceState NoiseState(DeviceState state, StateSource source)
    {
        byte[]? noise, adaptive;
        lock (_gate)
        {
            _configValues.TryGetValue(XiaomiConfig.Noise, out noise);
            _configValues.TryGetValue(XiaomiConfig.AdaptiveAnc, out adaptive);
        }
        if (noise is null) return state;
        var mode = noise[0] switch { 1 => NoiseControlMode.NoiseCancellation, 2 => NoiseControlMode.Transparency, _ => NoiseControlMode.Off };
        if (mode == NoiseControlMode.NoiseCancellation && adaptive is [1] && _profile.HasAdaptiveAnc)
            return state with { NoiseControl = Observed.From(new NoiseControlState(NoiseControlMode.Adaptive), Now, source) };
        string id = $"{(mode == NoiseControlMode.NoiseCancellation ? "nc" : "ambient")}-{noise[1]}";
        string? level = _profile.NoiseControlOptions.Any(o => o.Mode == mode && o.LevelId == id) ? id : null;
        return state with { NoiseControl = Observed.From(new NoiseControlState(mode, level), Now, source) };
    }

    /// <summary>The custom curve as the single device-side custom EQ, selected while preset 10 is active.</summary>
    private DeviceState CustomSelection(DeviceState state)
    {
        byte[]? curveValue, preset;
        lock (_gate)
        {
            _configValues.TryGetValue(XiaomiConfig.CustomEqualizer, out curveValue);
            _configValues.TryGetValue(XiaomiConfig.EqualizerPreset, out preset);
        }
        if (curveValue is null || !_profile.HasCustomEqualizer || !XiaomiCustomCurve.TryParse(curveValue, out var curve)) return state;
        var custom = new CustomEqualizer(XiaomiCustomCurve.PresetId, curve.Name, curve.MinGain, curve.MaxGain, preset is [XiaomiCustomCurve.PresetId], curve.Bands);
        return state with { CustomEqualizers = Observed.From<IReadOnlyList<CustomEqualizer>>([custom], Now, StateSource.QueryResponse) };
    }

    private async Task<byte[]?> ReadValueAsync(ushort id, TimeSpan timeout, CancellationToken ct)
    {
        var request = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpGetConfig, NextSequence(), XiaomiConfigCodec.ReadPayload(id));
        var frame = await RequestAsync(request, f => f.OpCode == XiaomiProtocol.OpGetConfig, timeout, ct).ConfigureAwait(false);
        return frame is { Status: 0 } && XiaomiConfigCodec.TryParse(frame.Payload, out var values) && values.TryGetValue(id, out var value) ? value : null;
    }

    private Task<byte[]?> ReadValueAsync(ushort id, CancellationToken ct) => ReadValueAsync(id, RequestTimeout, ct);

    /// <summary>Run-info record 10 is the in-ear detection switch, inverted (0 = on).</summary>
    private void ApplyRunInfo(byte[] payload)
    {
        foreach (var (type, value) in XiaomiProtocol.ParseInfoRecords(payload))
        {
            if (type != XiaomiProtocol.RunInfoWearDetection || value is not [<= 1]) continue;
            lock (_gate) _profile = _profile.WithConfig(XiaomiConfig.WearDetectionSwitch);
            var profile = _profile;
            Store.TryUpdate(DeviceId, s => s with
            {
                Profile = profile,
                Toggles = s.Toggles.SetItem(DeviceToggle.InEarDetection, Observed.From(value[0] == 0, Now, StateSource.QueryResponse)),
                LastUpdateSource = StateSource.QueryResponse,
            });
        }
    }

    private async Task<bool?> ReadWearDetectionAsync(TimeSpan timeout, CancellationToken ct)
    {
        var request = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpRunInfo, NextSequence(), XiaomiProtocol.RunInfoWearQueryPayload.ToArray());
        var frame = await RequestAsync(request, f => f.OpCode == XiaomiProtocol.OpRunInfo, timeout, ct).ConfigureAwait(false);
        if (frame is not { Status: 0 }) return null;
        foreach (var (type, value) in XiaomiProtocol.ParseInfoRecords(frame.Payload))
            if (type == XiaomiProtocol.RunInfoWearDetection && value is [<= 1]) return value[0] == 0;
        return null;
    }

    /// <summary>
    /// Sends a write and waits briefly for its acknowledgement. A non-zero status is a refusal; a
    /// missing acknowledgement is left to the readback.
    /// </summary>
    private async Task<CommandResult?> SendWriteAsync(byte opCode, byte[] payload, CancellationToken ct)
    {
        var request = new XiaomiFrame(XiaomiProtocol.KindRequest, opCode, NextSequence(), payload);
        var ack = await RequestAsync(request, f => f.OpCode == opCode, AckTimeout, ct).ConfigureAwait(false);
        if (Closed.IsCompleted) return new CommandResult(CommandStatus.TransportError);
        return ack is { Status: not 0 } ? new CommandResult(CommandStatus.Rejected, $"The earbuds refused the setting (status {ack.Status}).") : null;
    }

    private Task<CommandResult> WriteConfirmedAsync(ushort id, byte[] value, CancellationToken ct) =>
        WriteConfirmedAsync(id, value, actual => actual.AsSpan().SequenceEqual(value), ct);

    /// <summary>Writes config <paramref name="id"/>, re-reads it and succeeds only when the readback <paramref name="matches"/>.</summary>
    private async Task<CommandResult> WriteConfirmedAsync(ushort id, byte[] value, Func<byte[], bool> matches, CancellationToken ct)
    {
        if (!_profile.HasConfig(id)) return NotSupported($"Config {id} has not been confirmed on this device.");
        var result = await SendWriteAsync(XiaomiProtocol.OpSetConfig, XiaomiConfigCodec.WritePayload(id, value), ct).ConfigureAwait(false);
        if (result is null)
        {
            var actual = await ReadValueAsync(id, ct).ConfigureAwait(false);
            if (Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
            result = actual is null ? new CommandResult(CommandStatus.Timeout, "No fresh readback after write.")
                : matches(actual) ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The device kept a different value.");
            if (actual is not null) ApplyConfig(id, actual, result.IsSuccess ? StateSource.CommandConfirmation : StateSource.QueryResponse);
        }
        Report(result);
        return result;
    }

    private void Report(CommandResult result) =>
        Store.TryUpdate(DeviceId, s => s with { LastError = result.IsSuccess ? null : new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now) });

    public override Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken) => RunCommandAsync(PendingKeys.Toggle(toggle), _operations, ct =>
        toggle switch
        {
            DeviceToggle.GamingMode when _profile.HasGamingMode => WriteConfirmedAsync(XiaomiConfig.LowLatency, [enabled ? (byte)1 : (byte)0], ct),
            DeviceToggle.DualDeviceConnection => WriteConfirmedAsync(XiaomiConfig.DualConnection, [enabled ? (byte)1 : (byte)0], ct),
            DeviceToggle.AutoAnswerCalls => WriteConfirmedAsync(XiaomiConfig.AutoAnswer, [enabled ? (byte)1 : (byte)0], ct),
            DeviceToggle.InEarDetection => SetWearDetectionCoreAsync(enabled, ct),
            _ => Task.FromResult(NotSupported(toggle.ToString())),
        }, cancellationToken);

    /// <summary>In-ear detection: set-device-info record 6 (inverted), confirmed by run-info record 10.</summary>
    private async Task<CommandResult> SetWearDetectionCoreAsync(bool enabled, CancellationToken ct)
    {
        if (!_profile.HasConfig(XiaomiConfig.WearDetectionSwitch)) return NotSupported(DeviceToggle.InEarDetection.ToString());
        var result = await SendWriteAsync(XiaomiProtocol.OpSetDeviceInfo, XiaomiProtocol.WearDetectionPayload(enabled), ct).ConfigureAwait(false);
        if (result is null)
        {
            bool? actual = await ReadWearDetectionAsync(RequestTimeout, ct).ConfigureAwait(false);
            if (Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
            result = actual is null ? new CommandResult(CommandStatus.Timeout, "No fresh readback after write.")
                : actual == enabled ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The device kept a different value.");
            if (result.IsSuccess)
                Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(DeviceToggle.InEarDetection, Observed.From(enabled, Now, StateSource.CommandConfirmation)), LastUpdateSource = StateSource.CommandConfirmation });
        }
        Report(result);
        return result;
    }

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken) => RunCommandAsync(PendingKeys.NoiseControl, _operations, async ct =>
    {
        if (!_profile.NoiseControlOptions.Any(o => o.Matches(target))) return NotSupported("This noise option is not published for the connected model.");
        var current = await ReadValueAsync(XiaomiConfig.Noise, ct).ConfigureAwait(false);
        if (current is null || current.Length != 2) return new CommandResult(CommandStatus.Timeout);
        if (target.Mode == NoiseControlMode.Adaptive)
        {
            // Adaptive is ANC with the adaptive switch on; keep the stored manual strength.
            if (current[0] != 1)
            {
                var anc = await WriteConfirmedAsync(XiaomiConfig.Noise, [1, current[1]], ct).ConfigureAwait(false);
                if (!anc.IsSuccess) return anc;
            }
            return await WriteConfirmedAsync(XiaomiConfig.AdaptiveAnc, [1], ct).ConfigureAwait(false);
        }
        byte mode = target.Mode switch { NoiseControlMode.NoiseCancellation => 1, NoiseControlMode.Transparency => 2, _ => 0 };
        byte level = 0;
        if (target.LevelId is { } id && !byte.TryParse(id[(id.LastIndexOf('-') + 1)..], out level)) return NotSupported("Unknown noise level.");
        if (target.LevelId is null)
        {
            if (current[0] == mode) level = current[1];
            else if (_profile.NoiseControlOptions.FirstOrDefault(o => o.Mode == target.Mode && o.LevelId is not null)?.LevelId is { } first)
                byte.TryParse(first[(first.LastIndexOf('-') + 1)..], out level);
            else level = current[1];
        }
        // A manual ANC choice turns adaptive noise cancellation off first, as the official page does.
        if (mode == 1 && _profile.HasAdaptiveAnc && await ReadValueAsync(XiaomiConfig.AdaptiveAnc, ct).ConfigureAwait(false) is [1])
        {
            var manual = await WriteConfirmedAsync(XiaomiConfig.AdaptiveAnc, [0], ct).ConfigureAwait(false);
            if (!manual.IsSuccess) return manual;
        }
        return await WriteConfirmedAsync(XiaomiConfig.Noise, [mode, level], ct).ConfigureAwait(false);
    }, cancellationToken);

    public override Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken) => RunCommandAsync(PendingKeys.Equalizer, _operations, ct =>
        _profile.EqualizerPresets.Any(p => p.Id == presetId) && byte.TryParse(presetId[7..], out var id)
            ? WriteConfirmedAsync(XiaomiConfig.EqualizerPreset, [id], ct) : Task.FromResult(NotSupported("EQ preset is not listed for this model.")), cancellationToken);

    public override Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) => RunCommandAsync(PendingKeys.CustomEqualizer, _operations, ct =>
        id == XiaomiCustomCurve.PresetId && _profile.HasCustomEqualizer
            ? WriteConfirmedAsync(XiaomiConfig.EqualizerPreset, [XiaomiCustomCurve.PresetId], ct)
            : Task.FromResult(NotSupported("Custom EQ")), cancellationToken);

    /// <summary>
    /// Stores new gains for the existing bands and confirms them by reading the curve back. The
    /// curve's band layout, gain range and name are the earbuds'; renaming is not supported.
    /// </summary>
    public override Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken cancellationToken = default) => RunCommandAsync(PendingKeys.CustomEqualizer, _operations, async ct =>
    {
        if (preset.Id != XiaomiCustomCurve.PresetId || !_profile.HasCustomEqualizer) return NotSupported("Custom EQ");
        var currentValue = await ReadValueAsync(XiaomiConfig.CustomEqualizer, ct).ConfigureAwait(false);
        if (currentValue is null || !XiaomiCustomCurve.TryParse(currentValue, out var current)) return new CommandResult(CommandStatus.Timeout, "The custom EQ could not be read.");
        if (preset.Name != current.Name) return NotSupported("Renaming the custom EQ");
        if (preset.Bands.Count != current.Bands.Count
            || preset.Bands.Zip(current.Bands).Any(p => p.First.FrequencyHz != p.Second.FrequencyHz)
            || preset.Bands.Any(b => b.Gain < current.MinGain || b.Gain > current.MaxGain))
            return NotSupported("This band layout or gain");
        return await WriteConfirmedAsync(XiaomiConfig.CustomEqualizer, XiaomiCustomCurve.SaveValue(preset.Bands),
            actual => XiaomiCustomCurve.TryParse(actual, out var stored) && stored.SameBands(preset.Bands), ct).ConfigureAwait(false);
    }, cancellationToken);

    public override Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default) => RunCommandAsync(PendingKeys.Spatial, _operations, async ct =>
    {
        if (mode is not (SpatialAudioMode.Off or SpatialAudioMode.Fixed or SpatialAudioMode.HeadTracking)) return NotSupported("Spatial mode");
        var current = await ReadValueAsync(XiaomiConfig.Spatial, ct).ConfigureAwait(false);
        if (current is null || current.Length != 1) return NotSupported("Spatial audio was not reported by the device.");
        // Preserve the audio preference (bits 1–2) and any reserved bits.
        byte value = (byte)(current[0] & ~9);
        if (mode != SpatialAudioMode.Off) value |= 1;
        if (mode == SpatialAudioMode.HeadTracking) value |= 8;
        return await WriteConfirmedAsync(XiaomiConfig.Spatial, [value], ct).ConfigureAwait(false);
    }, cancellationToken);

    public override Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default) => RunCommandAsync(PendingKeys.PromptVolume, _operations, async ct =>
    {
        if (level is < 1 or > 10) return NotSupported("Prompt volume must be 1–10.");
        if (!_profile.HasConfig(XiaomiConfig.PromptVolume)) return NotSupported("Prompt volume bounds are unknown.");
        return await WriteConfirmedAsync(XiaomiConfig.PromptVolume, [(byte)level], actual => actual is { Length: 4 } && actual[0] == level, ct).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>
    /// One gesture on one earbud: the write carries 0xFF for the other earbud, and only a readback
    /// showing the new function on that earbud confirms it.
    /// </summary>
    public override Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default) => RunCommandAsync(PendingKeys.KeyFunction, _operations, async ct =>
    {
        if (!_profile.HasGestures || assignment.Button != 0 || assignment.Side is not (EarSide.Left or EarSide.Right)
            || XiaomiGestures.KindOf(assignment.Action) is not byte kind
            || !XiaomiGestures.Functions(assignment.Action, _profile.HasNoiseControl).Contains(assignment.Function))
            return NotSupported("This gesture assignment");
        var current = await ReadValueAsync(XiaomiConfig.Gestures, ct).ConfigureAwait(false);
        if (current is null || !XiaomiGestures.TryParse(current, out var groups)) return new CommandResult(CommandStatus.Timeout, "The gestures could not be read.");
        if (!groups.ContainsKey(kind)) return NotSupported("This gesture is not reported by the earbuds.");
        return await WriteConfirmedAsync(XiaomiConfig.Gestures, XiaomiGestures.WriteValue(kind, assignment.Side, assignment.Function),
            actual => XiaomiGestures.TryParse(actual, out var stored) && stored.TryGetValue(kind, out var pair)
                && (assignment.Side == EarSide.Left ? pair.Left : pair.Right) == assignment.Function, ct).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>
    /// Rings both earbuds. Starting is refused unless a fresh wear-state read shows neither earbud in
    /// an ear, since the tone is loud; stopping is always allowed.
    /// </summary>
    public override Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default) => RunCommandAsync(PendingKeys.FindEarbuds, _operations, async ct =>
    {
        if (!_profile.HasFind) return NotSupported("Find earbuds");
        CommandResult? result = null;
        if (ring)
        {
            var wear = await ReadValueAsync(XiaomiConfig.WearState, ct).ConfigureAwait(false);
            if (wear is not [<= 0x0F]) result = new CommandResult(CommandStatus.Timeout, "The wear state could not be read.");
            else if ((wear[0] & 0x0C) != 0) result = new CommandResult(CommandStatus.Rejected, XiaomiProtocol.FindWornDetail);
        }
        if (result is null)
        {
            var request = new XiaomiFrame(XiaomiProtocol.KindRequest, XiaomiProtocol.OpSetConfig, NextSequence(),
                XiaomiConfigCodec.WritePayload(XiaomiConfig.FindEarbuds, [ring ? (byte)1 : (byte)0, 3]));
            var ack = await RequestAsync(request, f => f.OpCode == XiaomiProtocol.OpSetConfig, RequestTimeout, ct).ConfigureAwait(false);
            result = ack is null ? new CommandResult(Closed.IsCompleted ? CommandStatus.TransportError : CommandStatus.Timeout, "The earbuds did not acknowledge.")
                : ack.Status != 0 ? new CommandResult(CommandStatus.Rejected, $"The earbuds refused (status {ack.Status}).")
                : CommandResult.Ok;
        }
        bool active = ring && result.IsSuccess;
        if (result.IsSuccess || ring) Store.TryUpdate(DeviceId, s => s with { IsFindingEarbuds = active });
        Report(result);
        Logger.LogInformation("Xiaomi find earbuds {Ring}: {Result}", ring, result.Status);
        return result;
    }, cancellationToken);
}
