// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Oppo;

/// <summary>
/// Feature commands, each following the official client's write path (SET, acknowledgement,
/// then the official re-read). See docs/PROTOCOL_FINDINGS.md for the bytes.
/// </summary>
public sealed partial class OppoDeviceController : IAdvancedDeviceCommands
{
    /// <summary>Detail used when a command needs the earbuds to be worn (official status 14 / pre-check).</summary>
    public const string WearRequiredDetail = "wear-required";

    private static readonly TimeSpan FitTestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FindAutoStop = TimeSpan.FromSeconds(50);

    private TaskCompletionSource<FitTestResult>? _fitTest;
    private CancellationTokenSource? _findAutoStop;

    // ---- responses -----------------------------------------------------------------------------

    private void ApplyFeatureResponse(OppoFrame frame, StateSource source)
    {
        var now = _time.GetUtcNow();
        if (OppoFeatureParsers.TryParseSpatialType(frame, out var spatial))
        {
            _store.TryUpdate(_identity.DeviceId, s => s with { Spatial = Observed.From(spatial, now, source) });
        }
        else if (OppoFeatureParsers.TryParseGameSound(frame, out var gameSound))
        {
            _store.TryUpdate(_identity.DeviceId, s => s with { GameSound = Observed.From(gameSound, now, source) });
        }
        else if (OppoFeatureParsers.TryParsePromptVolume(frame, out int volume))
        {
            _store.TryUpdate(_identity.DeviceId, s => s with { PromptVolume = Observed.From(volume, now, source) });
        }
        else if (OppoFeatureParsers.TryParseBassEngine(frame, out var bass))
        {
            _store.TryUpdate(_identity.DeviceId, s => s with { BassEngine = Observed.From(bass, now, source) });
        }
        else if (OppoFeatureParsers.TryParseKeyFunctions(frame, out var functions))
        {
            _store.TryUpdate(_identity.DeviceId, s => s with { KeyFunctions = Observed.From(functions, now, source) });
        }
        else if (OppoFeatureParsers.TryParseCustomEqualizers(frame, out var presets))
        {
            _store.TryUpdate(_identity.DeviceId, s => s with { CustomEqualizers = Observed.From(presets, now, source) });
        }
        else if (frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryConnectedDevices))
        {
            ApplySourceDevices(frame);
        }
        else if (OppoFeatureParsers.TryParsePreferredDevice(frame, out var preferred))
        {
            _store.TryUpdate(_identity.DeviceId, s => s with { PreferredDevice = Observed.From(preferred, now, source) });
        }
        else if (OppoFeatureParsers.TryParseHeadMotionType(frame, out int headType))
        {
            _store.TryUpdate(_identity.DeviceId, s => s with { HeadMotionType = Observed.From(headType, now, source) });
        }
        else if (OppoFeatureParsers.TryParseLeAudioSwitch(frame, out var leAudio))
        {
            _store.TryUpdate(_identity.DeviceId, s => s with { LeAudioSwitch = Observed.From(leAudio, now, source) });
        }
    }

    private void ApplySourceDevices(OppoFrame frame)
    {
        if (!OppoFeatureParsers.TryParseSourceDevices(frame, out var devices)) return;
        // Empty notifications can be transient; an explicit query is authoritative, including zero devices.
        bool query = frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryConnectedDevices);
        if (query || devices.Count > 0 || _store.Current.SourceDevices is null)
        {
            var now = _time.GetUtcNow();
            _store.TryUpdate(_identity.DeviceId, s => s with { SourceDevices = Observed.From(devices, now, query ? StateSource.QueryResponse : StateSource.DeviceNotification) });
        }
        _logger.LogDebug("Source-device list: {Count} entr(ies) (names/addresses not logged)", devices.Count);
    }

    private void ApplyFitTest(OppoFrame frame)
    {
        if (!OppoFeatureParsers.TryParseFitTest(frame, out var result)) return;
        _store.TryUpdate(_identity.DeviceId, s => s with { FitTest = result, IsFitTestRunning = false });
        _fitTest?.TrySetResult(result);
    }

    // ---- commands ------------------------------------------------------------------------------

    public Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable(DeviceCapability.SpatialAudio) || !Model.SpatialTypes.Contains((int)mode)) return Task.FromResult(CommandResult.Unsupported("Spatial audio is not available on this model."));
        return SpatialCoreAsync(mode, cancellationToken);
    }

    private async Task<CommandResult> SpatialCoreAsync(SpatialAudioMode mode, CancellationToken cancellationToken)
    {
        if (Model.SpatialHiResMutex && mode != SpatialAudioMode.Off
            && Model.FeatureSwitches.Any(f => f.Toggle == DeviceToggle.HiRes))
        {
            var prerequisite = await SetToggleAsync(DeviceToggle.HiRes, false, cancellationToken).ConfigureAwait(false);
            if (!prerequisite.IsSuccess) return prerequisite;
        }
        CommandResult result;
        if (Model.HasSpatialAudioV2)
        {
            result = await ExecuteConfirmedAsync(
                PendingKeys.Spatial,
                s => s,
                new OppoRequest(OppoCommand.SetSpatialAudio, [(byte)mode], SequenceMode.FixedSet),
                ct => ReadBackAsync(new OppoRequest(OppoCommand.QuerySpatialType, []), s => s.Spatial?.Value == mode, ct),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Spatial V1 (earbuds without 0x0422): feature 27 on/off; there is no head tracking.
            if (mode == SpatialAudioMode.HeadTracking) return CommandResult.Unsupported("Head tracking needs spatial audio V2.");
            result = await SetToggleAsync(DeviceToggle.SpatialAudio, mode != SpatialAudioMode.Off, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<CommandResult> SetLeBrSwitchAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteConfirmedAsync(
            PendingKeys.LeBrSwitch,
            s => s,
            new OppoRequest(OppoCommand.SetLeBrSwitch, [(byte)(enabled ? 1 : 0)], SequenceMode.FixedSet),
            async ct =>
            {
                var readBack = await _session.RequestAsync(new OppoRequest(OppoCommand.QueryLeBrSwitch, []), CommandTimeout, ct).ConfigureAwait(false);
                if (readBack.Response is not { } frame) return (false, readBack.Status);
                if (!OppoFeatureParsers.TryParseLeAudioSwitch(frame, out var state)) return (false, RequestStatus.Success);
                ApplyFeatureResponse(frame, StateSource.CommandConfirmation);
                return (state.Enabled == enabled, RequestStatus.Success);
            },
            cancellationToken).ConfigureAwait(false);
        // The generic "Command le-br-switch -> …" line omits the requested value; LE Audio switch
        // reports need it to tell an enable from a disable attempt.
        _logger.LogInformation("LE/BR switch 0x{Command:X4} [{Enabled}] -> {Status}", OppoCommand.SetLeBrSwitch, enabled, result.Status);
        return result;
    }

    public Task<CommandResult> SetGameSoundAsync(int type, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable(DeviceCapability.GamingMode) || !Model.GameSoundTypes.Contains(type)) return Task.FromResult(CommandResult.Unsupported("Game sound type not offered."));
        return ExecuteConfirmedAsync(
            PendingKeys.GameSound,
            s => s,
            new OppoRequest(OppoCommand.SetGameSound, [(byte)type, 0x01], SequenceMode.FixedSet),
            ct => ReadBackAsync(new OppoRequest(OppoCommand.QueryGameSound, []), s => s.GameSound?.Value.SelectedType == type, ct),
            cancellationToken);
    }

    /// <summary>
    /// The game-sound type is only reported when asked (no event), and game mode can change on the
    /// phone or with a gesture: the mutex dialog reads both fresh before deciding.
    /// </summary>
    public async Task<CommandResult> RefreshGameModeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable(DeviceCapability.GamingMode)) return CommandResult.Unsupported("Game mode is not available on this model.");
        if (!_session.IsOpen) return CommandResult.NotConnected;
        byte[] ids = [.. Model.FeatureSwitches.Where(f => f.Toggle is DeviceToggle.GamingMode or DeviceToggle.LowLatency).Select(f => f.FeatureId)];
        var requests = new List<OppoRequest>();
        if (ids.Length > 0) requests.Add(new OppoRequest(OppoCommand.QueryFeatureStatus, [(byte)ids.Length, .. ids], SequenceMode.FixedQuery));
        if (Model.GameSoundTypes.Count > 0) requests.Add(new OppoRequest(OppoCommand.QueryGameSound, []));
        var status = await RunQueriesAsync(requests, cancellationToken).ConfigureAwait(false);
        return ToCommandResult(status, "Game mode");
    }

    public Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable(DeviceCapability.PromptVolume)) return Task.FromResult(CommandResult.Unsupported("Prompt volume is not available on this model."));
        var (min, max) = Model.PromptVolumeRange;
        if (level < min || level > max) return Task.FromResult(CommandResult.Unsupported($"Prompt volume must be {min}–{max}."));
        return ExecuteConfirmedAsync(
            PendingKeys.PromptVolume,
            s => s,
            new OppoRequest(OppoCommand.SetPromptVolume, [(byte)level], SequenceMode.FixedSet),
            ct => ReadBackAsync(new OppoRequest(OppoCommand.QueryPromptVolume, []), s => s.PromptVolume?.Value == level, ct),
            cancellationToken);
    }

    public Task<CommandResult> SetGainAsync(int level, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommandResult.Unsupported("Gain is not available on this model."));

    public Task<CommandResult> SetBassEngineLevelAsync(int level, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable(DeviceCapability.BassEngine)) return Task.FromResult(CommandResult.Unsupported("Bass engine is not available on this model."));
        if (_store.Current.BassEngine?.Value is not { } bass) return Task.FromResult(CommandResult.Unsupported("Bass engine range unknown."));
        int value = Math.Clamp(level, bass.Min, bass.Max);
        return ExecuteConfirmedAsync(
            PendingKeys.BassEngine,
            s => s,
            new OppoRequest(OppoCommand.SetBassEngine, [unchecked((byte)(sbyte)bass.Min), unchecked((byte)(sbyte)bass.Max), unchecked((byte)(sbyte)value)], SequenceMode.FixedSet),
            ct => ReadBackAsync(new OppoRequest(OppoCommand.QueryBassEngine, []), s => s.BassEngine?.Value.Current == value, ct),
            cancellationToken);
    }

    public Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable(DeviceCapability.TouchCustomization)) return Task.FromResult(CommandResult.Unsupported("Gestures are not available on this model."));
        var row = Model.GestureRows.FirstOrDefault(r => r.Action == assignment.Action && r.Button == assignment.Button);
        if (row is null || !row.AllowedFunctions.Contains(assignment.Function)) return Task.FromResult(CommandResult.Unsupported("Function not offered for this gesture."));
        // Call rows and both-earbud rows are written once (devType 4); the X4 then reports call rows per earbud.
        bool once = row.IsCallControl || row.BothEars;
        var wireSide = once ? EarSide.Both : assignment.Side;
        byte[] record = [(byte)wireSide, (byte)row.Button, (byte)assignment.Action, (byte)assignment.Function];
        return ExecuteConfirmedAsync(
            PendingKeys.Gesture,
            s => s,
            new OppoRequest(OppoCommand.SetKeyFunction, [0x01, .. record], SequenceMode.FixedSet),
            ct => ReadBackAsync(
                new OppoRequest(OppoCommand.QueryTouchControls, [0x02, 0x03, 0x01]),
                s => s.KeyFunctions?.Value.Any(k =>
                    (k.Side == wireSide || once) && k.Button == row.Button && k.Action == assignment.Action && k.Function == assignment.Function) == true,
                ct),
            cancellationToken);
    }

    public Task<CommandResult> SetNoiseCycleAsync(EarSide side, uint modeMask, CancellationToken cancellationToken = default)
    {
        if (Model.NoiseCycleIndices.Count == 0) return Task.FromResult(CommandResult.Unsupported("The long-press cycle is not available on this model."));
        if (!Model.NoiseCycleShared && side is not (EarSide.Left or EarSide.Right)) return Task.FromResult(CommandResult.Unsupported("Choose the left or right earbud."));
        uint allowed = Model.NoiseCycleIndices.Aggregate(0u, (acc, i) => acc | (1u << i));
        uint mask = modeMask & allowed;
        if (System.Numerics.BitOperations.PopCount(mask) < Model.NoiseCycleMinimum) return Task.FromResult(new CommandResult(CommandStatus.Rejected, Model.NoiseCycleMinimum >= 2 ? "at-least-two" : "at-least-one"));
        // Shared cycle: type 1 for both earbuds; otherwise 3 left / 4 right.
        byte type = Model.NoiseCycleShared ? (byte)0x01 : side == EarSide.Left ? (byte)0x03 : (byte)0x04;
        return ExecuteConfirmedAsync(
            PendingKeys.NoiseCycle,
            s => s,
            new OppoRequest(OppoCommand.SetNoiseControl, [0x02, type, .. MinimalMask(mask)], SequenceMode.FixedSet),
            ct => ReadBackAsync(
                new OppoRequest(OppoCommand.QueryNoiseControl, [0x02, type]),
                s => (side == EarSide.Right && !Model.NoiseCycleShared ? s.NoiseCycle?.Value.RightMask : s.NoiseCycle?.Value.LeftMask) == mask,
                ct),
            cancellationToken);
    }

    public Task<CommandResult> SetHeadMotionTypeAsync(int type, CancellationToken cancellationToken = default)
    {
        if (type is not (0 or 1)) return Task.FromResult(CommandResult.Unsupported("Unknown head gesture assignment."));
        return ExecuteConfirmedAsync(
            PendingKeys.HeadMotion,
            s => s,
            new OppoRequest(OppoCommand.SetHeadMotionType, [(byte)type], SequenceMode.FixedSet),
            ct => ReadBackAsync(new OppoRequest(OppoCommand.QueryHeadMotionType, []), s => s.HeadMotionType?.Value == type, ct),
            cancellationToken);
    }

    public async Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable(DeviceCapability.FindMyEarbuds)) return CommandResult.Unsupported("Find my earbuds is not available on this model.");
        var result = await SendAckedAsync(new OppoRequest(OppoCommand.FindEarbuds, [ring ? (byte)0x01 : (byte)0x00], SequenceMode.FixedSet), cancellationToken).ConfigureAwait(false);
        if (ring && !result.IsSuccess)
        {
            // Official: a timed-out start is followed by an explicit stop.
            await SendAckedAsync(new OppoRequest(OppoCommand.FindEarbuds, [0x00], SequenceMode.FixedSet), CancellationToken.None).ConfigureAwait(false);
        }
        bool active = ring && result.IsSuccess;
        _store.TryUpdate(_identity.DeviceId, s => s with { IsFindingEarbuds = active });

        _findAutoStop?.Cancel();
        if (active)
        {
            var cts = new CancellationTokenSource();
            _findAutoStop = cts;
            _ = Task.Delay(FindAutoStop, _time, cts.Token).ContinueWith(
                t => { if (!t.IsCanceled) _ = FindEarbudsAsync(false); },
                TaskScheduler.Default);
        }
        return result;
    }

    public async Task<CommandResult> RunFitTestAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable(DeviceCapability.FitTest)) return CommandResult.Unsupported("Fit test is not available on this model.");
        static bool NotWorn(WearStatus? wear) => wear is not null && (wear.LeftInEar != true || wear.RightInEar != true);
        if (NotWorn(_store.Current.Wear?.Value))
        {
            if (await RefreshWearAsync(cancellationToken).ConfigureAwait(false) is { } failure) return ToCommandResult(failure, "Fit test");
            if (NotWorn(_store.Current.Wear?.Value)) return new CommandResult(CommandStatus.Rejected, WearRequiredDetail);
        }

        _fitTest = new TaskCompletionSource<FitTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.TryUpdate(_identity.DeviceId, s => s with { IsFitTestRunning = true, FitTest = null });
        var start = await SendAckedAsync(new OppoRequest(OppoCommand.FitTest, [0x01], SequenceMode.FixedSet), cancellationToken).ConfigureAwait(false);
        if (!start.IsSuccess)
        {
            _store.TryUpdate(_identity.DeviceId, s => s with { IsFitTestRunning = false });
            return start;
        }

        try
        {
            await _fitTest.Task.WaitAsync(FitTestTimeout, _time, cancellationToken).ConfigureAwait(false);
            return CommandResult.Ok;
        }
        catch (TimeoutException)
        {
            await SendAckedAsync(new OppoRequest(OppoCommand.FitTest, [0x00], SequenceMode.FixedSet), CancellationToken.None).ConfigureAwait(false);
            _store.TryUpdate(_identity.DeviceId, s => s with { IsFitTestRunning = false, FitTest = new FitTestResult(FitQuality.Failed, FitQuality.Failed) });
            return new CommandResult(CommandStatus.Timeout, "Fit test did not report a result.");
        }
        catch (OperationCanceledException)
        {
            await SendAckedAsync(new OppoRequest(OppoCommand.FitTest, [0x00], SequenceMode.FixedSet), CancellationToken.None).ConfigureAwait(false);
            _store.TryUpdate(_identity.DeviceId, s => s with { IsFitTestRunning = false });
            return new CommandResult(CommandStatus.Cancelled);
        }
    }

    public async Task<CommandResult> MultiConnectAsync(MultiConnectOperation operation, byte[] address, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable(DeviceCapability.DualDeviceConnection)) return CommandResult.Unsupported("Dual connection is not available on this model.");
        if (!Model.SupportsMultiConnectOperations) return CommandResult.Unsupported("Remote device management is not advertised by this firmware.");
        if (address.Length != 6) return CommandResult.Unsupported("Invalid device address.");
        if (!Enum.IsDefined(operation)) return CommandResult.Unsupported("Invalid device operation.");
        var device = _store.Current.SourceDevices?.Value.FirstOrDefault(d => d.Address.AsSpan().SequenceEqual(address));
        if (device is null) return CommandResult.Unsupported("Device is not in the current paired-device list.");
        if (operation == MultiConnectOperation.Disconnect && device?.IsThisDevice == true)
        {
            return CommandResult.Unsupported("Disconnect this PC from Windows Bluetooth settings.");
        }
        if (operation == MultiConnectOperation.Remove && device?.IsConnected == true)
        {
            return new CommandResult(CommandStatus.Rejected, "remove-connected");
        }

        // An ACK or an unrelated list notification does not prove the operation completed.
        // Confirm the targeted device, allowing the shared pipeline to retry while it settles.
        return await ExecuteConfirmedAsync(
            PendingKeys.MultiConnect,
            s => s,
            new OppoRequest(OppoCommand.OperateMultiConnect, [(byte)operation, .. address], SequenceMode.FixedSet),
            async ct =>
            {
                var read = await _session.RequestAsync(new OppoRequest(OppoCommand.QueryConnectedDevices, []), CommandTimeout, ct).ConfigureAwait(false);
                if (read.Response is not { } frame) return (false, read.Status);
                if (!OppoFeatureParsers.TryParseSourceDevices(frame, out var devices)) return (false, RequestStatus.Success);
                ApplySourceDevices(frame);
                var actual = devices.FirstOrDefault(d => d.Address.AsSpan().SequenceEqual(address));
                return (operation switch
                {
                    MultiConnectOperation.Connect => actual?.IsConnected == true,
                    MultiConnectOperation.Disconnect => actual is { State: 0 or 3 },
                    MultiConnectOperation.Remove => actual is null,
                    _ => false,
                }, RequestStatus.Success);
            },
            cancellationToken,
            operation == MultiConnectOperation.Connect ? ConnectSettleDelays : null).ConfigureAwait(false);
    }

    public async Task<CommandResult> SetPreferredDeviceAsync(byte[]? address, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable(DeviceCapability.DualDeviceConnection)) return CommandResult.Unsupported("Dual connection is not available on this model.");
        if (!Model.SupportsMultiConnectOperations) return CommandResult.Unsupported("Remote device management is not advertised by this firmware.");
        if (address is not null && address.Length != 6) return CommandResult.Unsupported("Invalid device address.");
        byte[] payload = address is { Length: 6 } ? [0x04, 0x01, .. address] : [0x04, 0x00];
        return await ExecuteConfirmedAsync(
            PendingKeys.PreferredDevice,
            s => s,
            new OppoRequest(OppoCommand.OperateMultiConnect, payload, SequenceMode.FixedSet),
            ct => ReadBackAsync(
                new OppoRequest(OppoCommand.QueryPriorityDevice, [0x02]),
                s => address is null ? s.PreferredDevice?.Value.Address is null : s.PreferredDevice?.Value.Address?.AsSpan().SequenceEqual(address) == true,
                ct),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken cancellationToken = default)
    {
        var preset = _store.Current.CustomEqualizers?.Value.FirstOrDefault(p => p.Id == id);
        if (preset is null) return Task.FromResult(CommandResult.Unsupported("Unknown custom preset."));
        // Selecting a custom preset is action 2 with the full record (not 0x0406).
        return CustomEqualizerCoreAsync("custom-eq-select", 0x02, preset, s => s.CustomEqualizers?.Value.Any(p => p.Id == id && p.IsSelected) == true, cancellationToken);
    }

    public Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken cancellationToken = default)
    {
        if (Model.CustomEqualizer is not { } spec) return Task.FromResult(CommandResult.Unsupported("Custom EQ is not available on this model."));
        string name = preset.Name.Trim();
        if (name.Length is 0 or > 42) return Task.FromResult(new CommandResult(CommandStatus.Rejected, "invalid-name"));
        var normalized = preset with
        {
            Name = name,
            MinGain = spec.MinGain,
            MaxGain = spec.MaxGain,
            Bands = preset.Bands.Select(b => b with { Gain = Math.Clamp(b.Gain, spec.MinGain, spec.MaxGain) }).ToArray(),
        };
        return CustomEqualizerCoreAsync(
            "custom-eq-save",
            0x02,
            normalized,
            s => s.CustomEqualizers?.Value.Any(p => p.Id == preset.Id && p.Name == name && p.Bands.Select(b => b.Gain).SequenceEqual(normalized.Bands.Select(b => b.Gain))) == true,
            cancellationToken);
    }

    public Task<CommandResult> AddCustomEqualizerAsync(CancellationToken cancellationToken = default)
    {
        if (Model.CustomEqualizer is not { } spec) return Task.FromResult(CommandResult.Unsupported("Custom EQ is not available on this model."));
        var existing = _store.Current.CustomEqualizers?.Value ?? [];
        if (existing.Count >= spec.MaxPresets) return Task.FromResult(new CommandResult(CommandStatus.Rejected, "max-presets"));
        // Official default name: 自定义 + (largest numeric suffix + 1); the device assigns the id.
        int next = existing
            .Select(p => p.Name.StartsWith("自定义", StringComparison.Ordinal) && int.TryParse(p.Name.AsSpan(3), out int n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var preset = new CustomEqualizer(0, "自定义" + next, spec.MinGain, spec.MaxGain, false, spec.BandFrequencies.Select(f => new EqualizerBand(f, 0)).ToArray());
        int before = existing.Count;
        return CustomEqualizerCoreAsync("custom-eq-add", 0x01, preset, s => (s.CustomEqualizers?.Value.Count ?? 0) > before, cancellationToken);
    }

    public Task<CommandResult> DeleteCustomEqualizerAsync(int id, CancellationToken cancellationToken = default)
    {
        var preset = _store.Current.CustomEqualizers?.Value.FirstOrDefault(p => p.Id == id);
        if (preset is null) return Task.FromResult(CommandResult.Unsupported("Unknown custom preset."));
        return CustomEqualizerCoreAsync("custom-eq-delete", 0x03, preset, s => s.CustomEqualizers?.Value.All(p => p.Id != id) != false, cancellationToken);
    }

    private Task<CommandResult> CustomEqualizerCoreAsync(string name, byte action, CustomEqualizer preset, Func<DeviceState, bool> confirmed, CancellationToken cancellationToken)
    {
        if (!IsAvailable(DeviceCapability.CustomEqualizer)) return Task.FromResult(CommandResult.Unsupported("Custom EQ is not available on this model."));
        return ExecuteConfirmedAsync(
            name,
            s => s,
            new OppoRequest(OppoCommand.SetCustomEqualizer, OppoFeatureParsers.EncodeCustomEqualizer(action, preset)),
            async ct =>
            {
                // Always re-read the list after 0x8418, then the active preset (official behaviour).
                var read = await ReadBackAsync(new OppoRequest(OppoCommand.QueryEqualizerList, []), confirmed, ct).ConfigureAwait(false);
                var eq = await _session.RequestAsync(new OppoRequest(OppoCommand.QueryEqualizer, []), CommandTimeout, ct).ConfigureAwait(false);
                if (eq.Response is { } frame) ApplyResponse(frame, StateSource.CommandConfirmation);
                return read;
            },
            cancellationToken);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Re-reads with <paramref name="query"/> and evaluates <paramref name="confirmed"/> on the resulting state.</summary>
    private async Task<(bool Matches, RequestStatus Status)> ReadBackAsync(OppoRequest query, Func<DeviceState, bool> confirmed, CancellationToken cancellationToken)
    {
        var result = await _session.RequestAsync(query, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (result.Response is not { } frame) return (false, result.Status);
        ApplyResponse(frame, StateSource.CommandConfirmation);
        return (confirmed(_store.Current), RequestStatus.Success);
    }

    /// <summary>Sends a SET and maps only its acknowledgement (for commands without a state to read back).</summary>
    private async Task<CommandResult> SendAckedAsync(OppoRequest request, CancellationToken cancellationToken)
    {
        if (_remoteCapabilities is { } caps && !caps.Allows(request.Command)) return CommandResult.Unsupported($"0x{request.Command:X4} not advertised by the device.");
        if (!_session.IsOpen) return CommandResult.NotConnected;
        var response = await _session.RequestAsync(request, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (response.Response is not { } ack) return ToCommandResult(response.Status, OppoCommand.Describe(request.Command));
        if (!OppoParsers.TryParseAck(ack, request.Command, out byte status)) return new CommandResult(CommandStatus.Rejected, "Malformed acknowledgement.");
        return MapAckStatus(status);
    }

    /// <summary>Status codes seen in the official client: 14 = earbuds must be worn, 15/16/9 = busy/low battery.</summary>
    internal static CommandResult MapAckStatus(byte status) => status switch
    {
        0x00 => CommandResult.Ok,
        0x0E => new CommandResult(CommandStatus.Rejected, WearRequiredDetail),
        0x0F => new CommandResult(CommandStatus.Rejected, "busy-other-device"),
        0x10 => new CommandResult(CommandStatus.Rejected, "battery-low"),
        0x09 => new CommandResult(CommandStatus.Rejected, "busy-other-device"),
        _ => new CommandResult(CommandStatus.Rejected, $"Device answered status 0x{status:X2}."),
    };

    private static byte[] MinimalMask(uint mask)
    {
        int bytes = mask <= 0xFF ? 1 : mask <= 0xFFFF ? 2 : mask <= 0xFFFFFF ? 3 : 4;
        var result = new byte[bytes];
        for (int i = 0; i < bytes; i++) result[i] = (byte)(mask >> (8 * i));
        return result;
    }

    /// <summary>Stops a running "find earbuds" tone when the earbuds go back into the case (official stop rule).</summary>
    private void StopFindingIfDocked(WearStatus wear)
    {
        if (_store.Current.IsFindingEarbuds && wear.BothInCase)
        {
            _ = FindEarbudsAsync(false);
        }
    }
}
