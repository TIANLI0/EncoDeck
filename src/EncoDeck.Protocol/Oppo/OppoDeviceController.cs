// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Protocol.Oppo;

/// <summary>
/// Drives one open <see cref="OppoSession"/> for one device: runs the start-up sequence, turns
/// device events into <see cref="IDeviceStateStore"/> updates, and executes commands with
/// device confirmation (and rollback of optimistic UI state on failure).
/// </summary>
public sealed partial class OppoDeviceController : IDeviceController
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private const int HandshakeAttempts = 3;
    private static readonly TimeSpan ReadBackDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Extra waits before re-reading when the earbuds acknowledged a write but still report the
    /// old value: some models (e.g. Enco Free4's game mode) apply a switch a moment after the
    /// acknowledgement. The official client trusts the acknowledgement and shows later reports.
    /// </summary>
    private static readonly TimeSpan[] SettleDelays = [TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1200)];

    /// <summary>
    /// Connecting a source device: the earbuds acknowledge at once, then page the phone, which takes several seconds
    /// (about 9 s in all before the connection counts as refused).
    /// </summary>
    private static readonly TimeSpan[] ConnectSettleDelays = [.. SettleDelays, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5)];

    private static readonly TimeSpan LivenessUpdateInterval = TimeSpan.FromSeconds(30);

    private readonly OppoSession _session;
    private readonly DeviceIdentity _identity;
    private readonly IDeviceStateStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<bool> _allowExperimental;
    private OppoCapabilityBitmap? _remoteCapabilities;
    private OppoModelDefinition _baseModel;
    private int? _firmware;
    private int _unknownFrames;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _keepAliveStop = new();
    private Task? _keepAliveTask;

    public OppoDeviceController(
        OppoSession session,
        OppoDeviceProfile profile,
        DeviceIdentity identity,
        IDeviceStateStore store,
        Func<bool>? allowExperimental = null,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        _session = session;
        Profile = profile;
        _baseModel = profile.Definition.Base ?? profile.Definition;
        _identity = identity;
        _store = store;
        _allowExperimental = allowExperimental ?? (() => false);
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _session.UnsolicitedFrame += OnUnsolicitedFrame;
        _session.Closed += OnSessionClosed;
        if (!_session.IsOpen) _closed.TrySetResult();
        // What these earbuds reported last time decides the settings until they report again.
        if (store.Current.Device?.DeviceId == identity.DeviceId) _firmware = store.Current.BudFirmware;
    }

    public OppoDeviceProfile Profile { get; private set; }

    /// <summary>When set, <see cref="DisposeAsync"/> also disposes the session (and its channel).</summary>
    internal bool OwnsSession { get; init; }

    /// <summary>When set, the battery is queried this often (see <see cref="Transport.IDuplexByteChannel.KeepAliveInterval"/>).</summary>
    internal TimeSpan? KeepAliveInterval { get; init; }

    /// <summary>The session runs over the LE Audio link (see <see cref="Transport.IDuplexByteChannel.IsLeAudioTransport"/>).</summary>
    internal bool IsLeAudioTransport { get; init; }

    /// <summary>Feature switch 0x1F: the host's audio transport is LE (1) or Classic (0).</summary>
    internal const byte LeAudioTransportFeature = 0x1F;

    public Task Closed => _closed.Task;

    private void OnSessionClosed(object? sender, SessionClosedEventArgs e) => _closed.TrySetResult();

    public uint? ProductId { get; private set; }

    public OppoCapabilityBitmap? RemoteCapabilities => _remoteCapabilities;

    public int UnknownFrameCount => Volatile.Read(ref _unknownFrames);

    private OppoModelDefinition Model => Profile.Definition;

    /// <summary>
    /// Mirrors the official client's session start: handshake (capability bitmap), event
    /// registration, then the GET sequence in the official order. Returns false when the device
    /// does not answer the handshake.
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        RequestResult? handshake = null;
        for (int attempt = 0; attempt < HandshakeAttempts; attempt++)
        {
            handshake = await _session.RequestAsync(new OppoRequest(OppoCommand.Handshake, []), HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            if (handshake.Status != RequestStatus.Timeout) break;
        }
        if (handshake?.Response is not { } hs || !OppoCapabilityBitmap.TryParse(hs, out _remoteCapabilities))
        {
            _logger.LogWarning("Handshake failed: {Status}", handshake?.Status);
            return false;
        }
        byte[] capabilityBytes = _remoteCapabilities!.ToArray();
        _store.TryUpdate(_identity.DeviceId, s => s with { ControlCapabilities = capabilityBytes });
        UpdateProfile(_baseModel);

        await SubscribeAsync(cancellationToken).ConfigureAwait(false);
        await ReadStateAsync(initial: true, cancellationToken).ConfigureAwait(false);

        // HeyMelody advertises its translation support here; a PC has none.
        await RequestIfAllowedAsync(new OppoRequest(OppoCommand.SetHostCapabilities, [0x00], SequenceMode.FixedSet), cancellationToken).ConfigureAwait(false);
        if (IsLeAudioTransport) await ReportLeAudioTransportAsync(cancellationToken).ConfigureAwait(false);
        if (KeepAliveInterval is { } interval)
            _keepAliveTask ??= Task.Run(() => KeepAliveAsync(interval, _keepAliveStop.Token), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// ColorOS tells the earbuds over 0x0403 [0x1F, 1] that the host's transport is now LE. Report this before
    /// secondary pairing on firmware without 0x0433 (Enco X3); an ACK does not verify peer bonding. Sent on every LE
    /// connection like the phone does, and not at all on Classic or on firmware with 0x0433 (X4, verified
    /// without it). The feature is never in the capability list, so the bitmap is not consulted.
    /// Not connected over LE yet is <see cref="CommandStatus.NotConnected"/>; firmware with 0x0433 is unsupported.
    /// </summary>
    public async Task<CommandResult> ReportLeAudioTransportAsync(CancellationToken cancellationToken = default)
    {
        if (!IsLeAudioTransport || _remoteCapabilities is not { } caps) return CommandResult.NotConnected;
        if (caps.Allows(OppoCommand.SetLeBrSwitch)) return CommandResult.Unsupported("These earbuds use the LE/BR switch (0x0433).");
        var result = await _session.RequestAsync(new OppoRequest(OppoCommand.SetFeature, [LeAudioTransportFeature, 1], SequenceMode.FixedSet), CommandTimeout, cancellationToken).ConfigureAwait(false);
        byte status = 0xFF;
        bool acknowledged = result.Response is { } ack && OppoParsers.TryParseAck(ack, OppoCommand.SetFeature, out status) && status == OppoParsers.StatusOk;
        _logger.LogInformation("LE Audio transport reported (0x0403 [1F 01]): {Status} {Ack}", result.Status, acknowledged ? "ok" : $"status 0x{status:X2}");
        if (acknowledged) return CommandResult.Ok;
        return result.Response is null ? ToCommandResult(result.Status, "LE Audio transport")
            : new CommandResult(CommandStatus.Rejected, $"LE Audio transport: status 0x{status:X2}");
    }

    /// <summary>Queries the battery periodically, so a silent control earbud is noticed while idle.</summary>
    private async Task KeepAliveAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(interval, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false) && _session.IsOpen)
            {
                if (_store.Current.PendingCommands.Count > 0) continue;
                await QueryAsync(new OppoRequest(OppoCommand.QueryBattery, [], SequenceMode.FixedSet), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OPPO keep-alive stopped");
        }
    }

    /// <summary>
    /// Sends a query and applies the answer; an unanswered query is sent once more. The GATT channel
    /// takes that second write to a silent earbud as a lost control role and closes, so the
    /// supervisor reconnects through the other earbud.
    /// </summary>
    private async Task<RequestResult?> QueryAsync(OppoRequest request, CancellationToken cancellationToken)
    {
        RequestResult? result = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            result = await RequestIfAllowedAsync(request, cancellationToken).ConfigureAwait(false);
            if (result?.Response is { } frame) ApplyResponse(frame, StateSource.QueryResponse);
            if (result?.Status != RequestStatus.Timeout) break;
        }
        return result;
    }

    /// <summary>
    /// Re-reads the wear state before a command is refused for it: a missed event (e.g. from a GATT
    /// earbud that lost the control role) leaves the cached state stale. Returns a transport failure.
    /// </summary>
    private async Task<RequestStatus?> RefreshWearAsync(CancellationToken cancellationToken)
    {
        var result = await QueryAsync(new OppoRequest(OppoCommand.QueryWearState, []), cancellationToken).ConfigureAwait(false);
        return result?.Status is RequestStatus.TransportError or RequestStatus.Closed ? result.Status : null;
    }

    /// <summary>Re-reads all state the UI shows.</summary>
    public async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var status = await ReadStateAsync(initial: false, cancellationToken).ConfigureAwait(false);
        return ToCommandResult(status, "Refresh");
    }

    /// <summary>
    /// GET sequence of the official client (session batch order), skipping requests that are
    /// phone-centric or private (e.g. the account key 0x0125). Identity queries run only initially.
    /// </summary>
    private async Task<RequestStatus> ReadStateAsync(bool initial, CancellationToken cancellationToken)
    {
        var requests = new List<OppoRequest>();
        if (initial)
        {
            requests.Add(new OppoRequest(OppoCommand.QueryVid, [0xFF, 0xFF]));
        }
        requests.Add(new OppoRequest(OppoCommand.QueryBattery, [], SequenceMode.FixedSet));
        if (initial)
        {
            requests.Add(new OppoRequest(OppoCommand.QueryColor, []));
            requests.Add(new OppoRequest(OppoCommand.QueryProductId, []));
            requests.Add(new OppoRequest(OppoCommand.QueryMtu, [0x00, 0x02]));
            requests.Add(new OppoRequest(OppoCommand.QueryFirmware, []));
            requests.Add(new OppoRequest(OppoCommand.QueryUpgradeCapability, []));
        }

        // The product id answered above may have switched the model (e.g. earbuds renamed in
        // Windows were first matched by the generic profile): build the model's queries after it.
        var status = await RunQueriesAsync(requests, cancellationToken).ConfigureAwait(false);
        if (status is RequestStatus.TransportError or RequestStatus.Closed) return status;
        var modelStatus = await RunQueriesAsync(ModelQueries(), cancellationToken).ConfigureAwait(false);
        return modelStatus is RequestStatus.Success ? status : modelStatus;
    }

    /// <summary>The model-specific part of the official GET sequence.</summary>
    private List<OppoRequest> ModelQueries()
    {
        var requests = new List<OppoRequest>();
        if (!Model.IsGeneric)
        {
            if (Model.GestureRows.Count > 0) requests.Add(new OppoRequest(OppoCommand.QueryTouchControls, [0x02, 0x03, 0x01]));
            if (Model.NoiseModes.Count > 0)
            {
                if (Model.NoiseCycleIndices.Count > 0 && Model.NoiseCycleShared)
                {
                    requests.Add(new OppoRequest(OppoCommand.QueryNoiseControl, [0x02, 0x01]));
                }
                else if (Model.NoiseCycleIndices.Count > 0)
                {
                    requests.Add(new OppoRequest(OppoCommand.QueryNoiseControl, [0x02, 0x03]));
                    requests.Add(new OppoRequest(OppoCommand.QueryNoiseControl, [0x02, 0x04]));
                }
                requests.Add(NoiseQuery);
                requests.Add(new OppoRequest(OppoCommand.QueryNoiseControl, [0x03, 0x01]));
                requests.Add(new OppoRequest(OppoCommand.QueryNoiseControl, [0x04, 0x01]));
            }
            if (Model.EqualizerPresets.Count > 0) requests.Add(new OppoRequest(OppoCommand.QueryEqualizer, []));
            if (Model.FeatureStatusIds.Count > 0)
            {
                byte[] ids = [.. Model.FeatureStatusIds];
                requests.Add(new OppoRequest(OppoCommand.QueryFeatureStatus, [(byte)ids.Length, .. ids], SequenceMode.FixedQuery));
            }
            if (Model.HasSpatialAudioV2) requests.Add(new OppoRequest(OppoCommand.QuerySpatialType, []));
            if (Model.GameSoundTypes.Count > 0) requests.Add(new OppoRequest(OppoCommand.QueryGameSound, []));
            // The official request has an empty payload.
            if (Model.CustomEqualizer is not null) requests.Add(new OppoRequest(OppoCommand.QueryEqualizerList, []));
            if (IsAvailable(DeviceCapability.PromptVolume)) requests.Add(new OppoRequest(OppoCommand.QueryPromptVolume, []));
            if (IsAvailable(DeviceCapability.BassEngine)) requests.Add(new OppoRequest(OppoCommand.QueryBassEngine, []));
            if (Model.FeatureSwitches.Any(f => f.Toggle == DeviceToggle.DualDeviceConnection))
            {
                requests.Add(new OppoRequest(OppoCommand.QueryConnectedDevices, []));
                requests.Add(new OppoRequest(OppoCommand.QueryPriorityDevice, [0x02]));
            }
            if (Model.FeatureSwitches.Any(f => f.Toggle == DeviceToggle.HeadGestures)) requests.Add(new OppoRequest(OppoCommand.QueryHeadMotionType, []));
            // Read-only: an answer tells the UI the firmware can run LE Audio (skipped unless the bitmap allows it).
            requests.Add(new OppoRequest(OppoCommand.QueryLeBrSwitch, []));
            requests.Add(new OppoRequest(OppoCommand.QueryWearState, []));
        }
        return requests;
    }

    private async Task<RequestStatus> RunQueriesAsync(List<OppoRequest> requests, CancellationToken cancellationToken)
    {
        var worst = RequestStatus.Success;
        foreach (var request in requests)
        {
            var result = await RequestIfAllowedAsync(request, cancellationToken).ConfigureAwait(false);
            if (result is null) continue;
            if (result.Response is { } frame)
            {
                ApplyResponse(frame, StateSource.QueryResponse);
            }
            else if (result.Status is RequestStatus.TransportError or RequestStatus.Closed)
            {
                return result.Status;
            }
            else if (request.Command is OppoCommand.QueryBattery or OppoCommand.QueryNoiseControl && worst == RequestStatus.Success)
            {
                worst = result.Status;
            }
        }
        return worst;
    }

    /// <summary>Routes any response to the state it describes (the official client handles responses the same way).</summary>
    private void ApplyResponse(OppoFrame frame, StateSource source)
    {
        switch (frame.Command & 0x7FFF)
        {
            case OppoCommand.QueryBattery:
                ApplyBattery(frame, source);
                break;
            case OppoCommand.QueryWearState:
                ApplyWear(frame, source);
                break;
            case OppoCommand.QueryNoiseControl:
                ApplyNoise(frame, source);
                break;
            case OppoCommand.QueryEqualizer when OppoParsers.TryParseEqualizer(frame, out byte preset):
                ApplyEqualizer(preset, source);
                break;
            case OppoCommand.QueryFeatureStatus when OppoParsers.TryParseFeatureStatus(frame, out var values):
                ApplyFeatures(values, source);
                break;
            case OppoCommand.QueryProductId when OppoParsers.TryParseProductId(frame, out uint productId):
                ProductId = productId;
                ApplyProductId(productId);
                break;
            case OppoCommand.QueryColor when OppoParsers.IsResponseOk(frame) && frame.Payload.Length >= 2:
                int colorId = frame.Payload[1];
                _store.TryUpdate(_identity.DeviceId, s => s with { ColorId = colorId });
                break;
            case OppoCommand.QueryFirmware:
                ApplyFirmware(frame);
                break;
            default:
                ApplyFeatureResponse(frame, source);
                break;
        }
    }

    public async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!IsAvailable(DeviceCapability.NoiseCancellation)) return CommandResult.Unsupported("Noise control is not available on this model.");
        // Official pre-check: noise control needs at least one earbud in an ear.
        if (_store.Current.Wear?.Value is { LeftInEar: false, RightInEar: false })
        {
            if (await RefreshWearAsync(cancellationToken).ConfigureAwait(false) is { } failure) return ToCommandResult(failure, "Noise control");
            if (_store.Current.Wear?.Value is { LeftInEar: false, RightInEar: false }) return new CommandResult(CommandStatus.Rejected, WearRequiredDetail);
        }

        int setIndex;
        if (target.LevelId is null && Model.NoiseModeParents.TryGetValue(target.Mode, out var parent) && !IsAllowed(parent.Support) && parent.DefaultLevelId is { } fallback)
        {
            // Parent index not verified: select the verified default level instead.
            target = target with { LevelId = fallback };
        }

        if (target.LevelId is null && Model.NoiseModeParents.TryGetValue(target.Mode, out parent) && IsAllowed(parent.Support))
        {
            setIndex = parent.Index;
        }
        else if (target.LevelId is null && Model.NoiseModes.FirstOrDefault(m => m.Option.Mode == target.Mode && m.Option.LevelId is null) is { } single && IsAllowed(single.Option.Support))
        {
            setIndex = single.SetIndex;
        }
        else if (Model.FindNoiseMode(target) is { } mode && IsAllowed(mode.Option.Support))
        {
            setIndex = mode.SetIndex;
        }
        else
        {
            return CommandResult.Unsupported($"{target.Mode}/{target.LevelId} is not a verified option for {Model.DisplayName}.");
        }

        var request = new OppoRequest(OppoCommand.SetNoiseControl, [0x01, 0x01, .. MaskBytes(setIndex)], SequenceMode.FixedSet);
        return await ExecuteConfirmedAsync(
            "noise-control",
            state => state with { NoiseControl = Observed.From(target, _time.GetUtcNow(), StateSource.Optimistic) },
            request,
            async ct =>
            {
                var readBack = await _session.RequestAsync(NoiseQuery, CommandTimeout, ct).ConfigureAwait(false);
                if (readBack.Response is not { } frame) return (false, readBack.Status);
                var actual = ApplyNoise(frame, StateSource.CommandConfirmation);
                bool matches = actual is not null && actual.Mode == target.Mode && (target.LevelId is null || actual.LevelId == target.LevelId);
                return (matches, RequestStatus.Success);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Oppo exposes no per-band write command, so band editing stays unavailable.</summary>
    public Task<CommandResult> SetEqualizerBandsAsync(IReadOnlyList<int> bands, CancellationToken cancellationToken) =>
        Task.FromResult(CommandResult.Unsupported("Editable equalizer bands."));

    public async Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        if (!IsAvailable(DeviceCapability.Equalizer)) return CommandResult.Unsupported("Device EQ is not available on this model.");
        var entry = Model.EqualizerPresets.FirstOrDefault(kv => kv.Value.Id == presetId);
        if (entry.Value is null || !IsAllowed(entry.Value.Support)) return CommandResult.Unsupported($"EQ preset {presetId} is not verified.");

        byte index = entry.Key;
        var request = new OppoRequest(OppoCommand.SetEqualizer, [index]);
        return await ExecuteConfirmedAsync(
            "equalizer",
            state => DeselectCustomEqualizer(state with { Equalizer = Observed.From(new EqualizerState(presetId, index), _time.GetUtcNow(), StateSource.Optimistic) }, _time.GetUtcNow(), StateSource.Optimistic),
            request,
            async ct =>
            {
                var readBack = await _session.RequestAsync(new OppoRequest(OppoCommand.QueryEqualizer, []), CommandTimeout, ct).ConfigureAwait(false);
                if (readBack.Response is not { } frame || !OppoParsers.TryParseEqualizer(frame, out byte actual)) return (false, readBack.Status);
                ApplyEqualizer(actual, StateSource.CommandConfirmation);
                if (actual == index)
                    _store.TryUpdate(_identity.DeviceId, s => DeselectCustomEqualizer(s, _time.GetUtcNow(), StateSource.CommandConfirmation));
                return (actual == index, RequestStatus.Success);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        if (toggle == DeviceToggle.DualDeviceConnection && enabled && _store.Current.IsLeAudioBonded)
            return new(CommandStatus.Rejected, "le-audio-multipoint");
        var feature = Model.FeatureSwitches.FirstOrDefault(f => f.Toggle == toggle);
        if (feature is null || !IsAllowed(feature.Support)) return CommandResult.Unsupported($"{toggle} is not available on this model.");
        // The model flag alone does not prove that this firmware implements smart volume.
        // Require a device-reported status before offering a write, as the UI does.
        if (toggle == DeviceToggle.AutoVolume && !_store.Current.Toggles.ContainsKey(toggle))
            return CommandResult.Unsupported("Smart volume has not been reported by the earbuds.");

        var request = new OppoRequest(OppoCommand.SetFeature, [feature.FeatureId, enabled ? (byte)1 : (byte)0], SequenceMode.FixedSet);
        return await ExecuteConfirmedAsync(
            "toggle:" + toggle,
            state => state with { Toggles = state.Toggles.SetItem(toggle, Observed.From(enabled, _time.GetUtcNow(), StateSource.Optimistic)) },
            request,
            async ct =>
            {
                var readBack = await _session.RequestAsync(new OppoRequest(OppoCommand.QueryFeatureStatus, [1, feature.FeatureId], SequenceMode.FixedQuery), CommandTimeout, ct).ConfigureAwait(false);
                if (readBack.Response is not { } frame || !OppoParsers.TryParseFeatureStatus(frame, out var values)) return (false, readBack.Status);
                ApplyFeatures(values, StateSource.CommandConfirmation);
                var value = values.FirstOrDefault(v => v.FeatureId == feature.FeatureId);
                return (values.Any(v => v.FeatureId == feature.FeatureId) && (value.Value != 0) == enabled, RequestStatus.Success);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared command pipeline: capability gate → optimistic state + pending marker → write →
    /// acknowledgement → read-back. Any failure restores the pre-command value (or the device's
    /// actual value when the read-back provided one).
    /// </summary>
    private async Task<CommandResult> ExecuteConfirmedAsync(
        string name,
        Func<DeviceState, DeviceState> applyOptimistic,
        OppoRequest request,
        Func<CancellationToken, Task<(bool Matches, RequestStatus Status)>> readBack,
        CancellationToken cancellationToken,
        IReadOnlyList<TimeSpan>? settleDelays = null)
    {
        if (_remoteCapabilities is { } caps && !caps.Allows(request.Command))
        {
            return CommandResult.Unsupported($"Device capability bitmap does not allow 0x{request.Command:X4}.");
        }
        if (!_session.IsOpen) return CommandResult.NotConnected;

        var pending = new PendingCommand(Guid.NewGuid(), name, _time.GetUtcNow());
        DeviceState? before = null;
        _store.TryUpdate(_identity.DeviceId, state =>
        {
            before = state;
            return applyOptimistic(state) with { PendingCommands = state.PendingCommands.Add(pending), LastError = null };
        });

        CommandResult result;
        try
        {
            var response = await _session.RequestAsync(request, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (response.Response is { } ack)
            {
                if (!OppoParsers.TryParseAck(ack, request.Command, out byte status) || status != OppoParsers.StatusOk)
                {
                    result = OppoParsers.TryParseAck(ack, request.Command, out byte parsed) ? MapAckStatus(parsed) : new CommandResult(CommandStatus.Rejected, "Malformed acknowledgement.");
                }
                else
                {
                    await Task.Delay(ReadBackDelay, _time, cancellationToken).ConfigureAwait(false);
                    var (matches, readStatus) = await readBack(cancellationToken).ConfigureAwait(false);
                    foreach (var settle in settleDelays ?? SettleDelays)
                    {
                        if (matches || readStatus != RequestStatus.Success) break;
                        // Keep showing the requested value while the earbuds settle (no flicker back).
                        _store.TryUpdate(_identity.DeviceId, applyOptimistic);
                        await Task.Delay(settle, _time, cancellationToken).ConfigureAwait(false);
                        (matches, readStatus) = await readBack(cancellationToken).ConfigureAwait(false);
                    }
                    result = readStatus != RequestStatus.Success
                        ? ToCommandResult(readStatus, "Read-back")
                        : matches ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "Device acknowledged but reports a different value.");
                }
            }
            else if (response.Status == RequestStatus.Timeout)
            {
                // The write may still have been applied: trust a read-back if one succeeds.
                var (matches, readStatus) = await readBack(cancellationToken).ConfigureAwait(false);
                result = readStatus == RequestStatus.Success && matches ? CommandResult.Ok : ToCommandResult(RequestStatus.Timeout, name);
            }
            else
            {
                result = ToCommandResult(response.Status, name);
            }
        }
        catch (OperationCanceledException)
        {
            result = new CommandResult(CommandStatus.Cancelled);
        }
        catch (Exception ex)
        {
            // E.g. the session being torn down mid-command. The pending marker must still go: a
            // marker left behind keeps the control disabled until the app restarts.
            _logger.LogWarning(ex, "Command {Name} failed", name);
            result = new CommandResult(CommandStatus.TransportError, ex.Message);
        }

        _store.TryUpdate(_identity.DeviceId, state =>
        {
            var next = state with { PendingCommands = state.PendingCommands.RemoveAll(p => p.Id == pending.Id) };
            if (result.IsSuccess) return next;

            // Roll back only values that are still optimistic; confirmed device values win.
            if (before is not null)
            {
                if (next.NoiseControl?.Source == StateSource.Optimistic) next = next with { NoiseControl = before.NoiseControl };
                if (next.Equalizer?.Source == StateSource.Optimistic) next = next with { Equalizer = before.Equalizer };
                foreach (var (toggle, value) in next.Toggles.Where(t => t.Value.Source == StateSource.Optimistic).ToList())
                {
                    next = next with
                    {
                        Toggles = before.Toggles.TryGetValue(toggle, out var old) ? next.Toggles.SetItem(toggle, old) : next.Toggles.Remove(toggle),
                    };
                }
            }
            return next with { LastError = new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, _time.GetUtcNow()) };
        });

        _logger.LogInformation("Command {Name} -> {Result}", name, result);
        return result;
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        var codes = await RequestIfAllowedAsync(
            new OppoRequest(OppoCommand.QueryEventCodes, [], ResponseCommands: [OppoCommand.ResponseOf(OppoCommand.QueryEventCodes), OppoCommand.EventCodesAltResponse]),
            cancellationToken).ConfigureAwait(false);
        if (codes?.Response is not { } frame || !OppoParsers.TryParseEventCodes(frame, out var offered)) return;

        // Like the official client: register every offered event (EncoDeck answers F2/F4 as it does),
        // ordered as signed bytes (F1 F2 F3 01 02 …). Models may restrict the list.
        var wanted = (Model.SubscribedEvents is { } restricted ? offered.Where(restricted.Contains) : offered)
            .OrderBy(code => (sbyte)code)
            .ToArray();
        if (wanted.Length == 0) return;
        if (_remoteCapabilities?.Allows(OppoCommand.SubscribeEvents) == false) return;
        await _session.RequestAsync(new OppoRequest(OppoCommand.SubscribeEvents, [(byte)wanted.Length, .. wanted]), CommandTimeout, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyFirmware(OppoFrame frame)
    {
        if (!OppoParsers.TryParseVersions(frame, out var versions)) return;

        var extra = new Dictionary<string, string>();
        foreach (var v in versions)
        {
            string key = v.Component switch { 1 => "left", 2 => "right", 3 => "case", _ => $"component{v.Component}" };
            extra[key] = v.Version;
        }
        string? budVersion = versions.Where(v => v.Component is 1 or 2).Select(v => v.Version).FirstOrDefault();
        var info = new FirmwareInfo(budVersion, ProductId is { } pid ? pid.ToString("X6") : null, extra);
        _store.TryUpdate(_identity.DeviceId, s => s with { Firmware = Observed.From(info, _time.GetUtcNow(), StateSource.QueryResponse) });

        // EQ presets gated by firmware compare the lower of the two earbuds' running versions
        // (the non-zero one when only one earbud reports).
        var buds = versions.Where(v => v.Component is 1 or 2).Select(v => int.TryParse(v.Version, out int n) ? n : 0).Where(n => n > 0).ToList();
        if (buds.Count > 0)
        {
            int firmware = buds.Min();
            _firmware = firmware;
            _store.TryUpdate(_identity.DeviceId, s => s with { BudFirmware = firmware });
            UpdateProfile(_baseModel);
        }
    }

    /// <summary>Adapts <paramref name="baseModel"/> to what these earbuds report and publishes it when it changed.</summary>
    private void UpdateProfile(OppoModelDefinition baseModel)
    {
        _baseModel = baseModel;
        var adapted = baseModel.Adapt(_remoteCapabilities, _firmware);
        // Same model and the same settings (e.g. as remembered from last time): keep the UI as it is.
        if (ReferenceEquals(Model.Base ?? Model, baseModel) && adapted.ProfileKey == Model.ProfileKey) return;
        var next = new OppoDeviceProfile(adapted);
        Profile = next;
        _store.TryUpdate(_identity.DeviceId, s => s with { Profile = next });
    }

    /// <summary>
    /// Switches to the definition matching the product id. A device whose id does not match its
    /// name-matched definition is demoted to the read-only generic profile: we never send model
    /// specific writes to hardware that has not been verified.
    /// </summary>
    private void ApplyProductId(uint productId)
    {
        var byId = OppoModels.FindByProductId(productId);
        OppoModelDefinition next;
        if (byId is not null)
        {
            next = byId;
            if (byId != _baseModel) _logger.LogInformation("Product id {ProductId:X6}: using {Model}", productId, byId.ModelId);
        }
        else if (_baseModel.IsGeneric || _baseModel.ProductIds.Count == 0)
        {
            next = _baseModel;
        }
        else
        {
            _logger.LogWarning("Product id {ProductId:X6} does not match {Model}; using the read-only generic profile", productId, _baseModel.ModelId);
            next = OppoModels.GenericOppo;
        }

        _store.TryUpdate(_identity.DeviceId, s => s with { ProductId = productId });
        UpdateProfile(next);
    }

    private void OnUnsolicitedFrame(object? sender, OppoFrame frame)
    {
        var now = _time.GetUtcNow();
        // Liveness timestamp only; throttled so periodic device chatter does not wake the UI.
        if (_store.Current.LastResponseAt is not { } last || now - last > LivenessUpdateInterval)
        {
            _store.TryUpdate(_identity.DeviceId, s => s with { LastResponseAt = now });
        }

        if (frame.IsResponse)
        {
            // A late answer to a request that already timed out still carries valid state.
            ApplyResponse(frame, StateSource.QueryResponse);
            return;
        }

        if (frame.Command == OppoCommand.DeviceEvent && frame.Payload.Length > 0)
        {
            switch (frame.Payload[0])
            {
                case OppoEventCode.Battery:
                    ApplyBattery(frame, StateSource.DeviceNotification);
                    return;
                case OppoEventCode.WearState:
                    ApplyWear(frame, StateSource.DeviceNotification);
                    return;
                case OppoEventCode.NoiseControl:
                    ApplyNoise(frame, StateSource.DeviceNotification);
                    return;
                case OppoEventCode.FitTest:
                    ApplyFitTest(frame);
                    return;
                case OppoEventCode.ConnectedDevices:
                    ApplySourceDevices(frame);
                    return;
                case OppoEventCode.ConnectionHistory:
                    // The earbuds wait for an acknowledgement (official reply: [00][F2]).
                    _ = ReplyAsync(frame, [0x00, OppoEventCode.ConnectionHistory]);
                    return;
                case OppoEventCode.Telemetry:
                    _ = ReplyAsync(frame, TelemetryReply(frame.Payload));
                    return;
            }
        }

        switch (frame.Command)
        {
            case OppoCommand.EqualizerChangedNotification when OppoParsers.TryParseEqualizerChanged(frame, out byte preset):
                ApplyEqualizer(preset, StateSource.DeviceNotification);
                return;
            case OppoCommand.FeatureChangedNotification when OppoParsers.TryParseFeatureChanged(frame, out var features):
                ApplyFeatures(features, StateSource.DeviceNotification);
                return;
            case OppoCommand.CustomEqualizerChangedNotification:
            case OppoCommand.BassEngineChangedNotification:
                ApplyFeatureResponse(frame, StateSource.DeviceNotification);
                return;
            case OppoCommand.TimeRequest:
                _ = ReplyAsync(frame, TimeReply());
                return;
            case OppoCommand.AppCapabilitiesRequest:
                _ = ReplyAsync(frame, [0x00, 0x00, 0x00]);
                return;
            case OppoCommand.PhoneSpatialRequest:
                // The host does not apply spatial audio itself.
                _ = ReplyAsync(frame, [0x01]);
                return;
            case OppoCommand.GameEqRequest:
                _ = ReplyAsync(frame, [0x00, 0x01, 0x00]);
                return;
            case OppoCommand.FreeMusicRequest:
                _ = ReplyAsync(frame, [0x00]);
                return;
        }

        if (frame.Group is not (0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x0F))
        {
            // The official client answers packets of unknown groups with status 1.
            _ = ReplyAsync(frame, [0x01]);
        }

        Interlocked.Increment(ref _unknownFrames);
        _logger.LogDebug("Unhandled frame {Command} seq={Sequence:X2} payload={Payload}", OppoCommand.Describe(frame.Command), frame.Sequence, OppoRedaction.PayloadHex(frame));
    }

    /// <summary>Answers a device-initiated packet with the same command (response flag set) and sequence.</summary>
    private async Task ReplyAsync(OppoFrame request, byte[] payload)
    {
        try
        {
            await _session.SendFrameAsync(new OppoFrame(OppoCommand.ResponseOf(request.Command), request.Sequence, payload)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            _logger.LogDebug("Could not reply to {Command}: {Message}", OppoCommand.Describe(request.Command), ex.Message);
        }
    }

    /// <summary><c>[00][unix seconds u32 LE]</c> plus the UTC offset in seconds (i32 LE, including DST) when the model wants it.</summary>
    private byte[] TimeReply()
    {
        var now = _time.GetUtcNow();
        uint seconds = (uint)now.ToUnixTimeSeconds();
        var reply = new List<byte> { 0x00 };
        reply.AddRange(BitConverter.GetBytes(seconds));
        if (Model.TimeReplyIncludesOffset)
        {
            int offset = (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalSeconds;
            reply.AddRange(BitConverter.GetBytes(offset));
        }
        return [.. reply];
    }

    /// <summary><c>[00][F4]{"cmd":"…"}</c> echoing the request's <c>cmd</c> field.</summary>
    private static byte[] TelemetryReply(byte[] payload)
    {
        string cmd = string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload.AsMemory(1));
            if (doc.RootElement.TryGetProperty("cmd", out var value)) cmd = value.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
        }
        byte[] json = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["cmd"] = cmd }));
        return [0x00, OppoEventCode.Telemetry, .. json];
    }

    private void ApplyBattery(OppoFrame frame, StateSource source)
    {
        if (!OppoParsers.TryParseBattery(frame, out var entries)) return;
        var now = _time.GetUtcNow();
        _store.TryUpdate(_identity.DeviceId, state =>
        {
            var battery = state.Battery;
            foreach (var entry in entries)
            {
                BatteryComponent? component = entry.Component switch
                {
                    1 => BatteryComponent.Left,
                    2 => BatteryComponent.Right,
                    3 => BatteryComponent.Case,
                    _ => null,
                };
                if (component is null) continue;
                battery = battery.With(component.Value, Observed.From(new BatteryReading(entry.Level, entry.IsCharging), now, source));
            }
            // Components missing from the report keep their previous (aging) value: the case, for
            // instance, is only reported while a bud is docked. The UI shows those as "last read".
            return state with { Battery = battery, LastUpdateSource = source, LastResponseAt = now };
        });
    }

    /// <summary>
    /// Raised when the case lid opens while at least one earbud is docked — the official client's
    /// second pop-up trigger.
    /// </summary>
    public event EventHandler? CaseLidOpened;

    private void ApplyWear(OppoFrame frame, StateSource source)
    {
        if (!OppoParsers.TryParseWearState(frame, out var entries)) return;
        var now = _time.GetUtcNow();
        bool? lidBefore = _store.Current.Wear?.Value.CaseLidOpen;
        WearStatus? applied = null;
        _store.TryUpdate(_identity.DeviceId, state =>
        {
            var wear = state.Wear?.Value ?? WearStatus.Unknown;
            foreach (var e in entries)
            {
                wear = e.Component switch
                {
                    1 => wear with { LeftInEar = e.IsInEar, LeftInCase = !e.IsOutOfCase },
                    2 => wear with { RightInEar = e.IsInEar, RightInCase = !e.IsOutOfCase },
                    3 => wear with { CaseLidOpen = e.IsLidOpen },
                    _ => wear,
                };
            }
            applied = wear;
            return state with { Wear = Observed.From(wear, now, source), LastUpdateSource = source };
        });

        if (applied is null) return;
        StopFindingIfDocked(applied);
        if (source == StateSource.DeviceNotification && lidBefore == false && applied.CaseLidOpen == true && (applied.LeftInCase == true || applied.RightInCase == true))
        {
            CaseLidOpened?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Applies a noise-control report: subtype 1 current mode, 2 long-press cycle (type 3 left /
    /// 4 right), 4 smart (auto) level. Returns the decoded current mode, or null.
    /// </summary>
    private NoiseControlState? ApplyNoise(OppoFrame frame, StateSource source)
    {
        if (!OppoParsers.TryParseNoiseReport(frame, out var report)) return null;
        var now = _time.GetUtcNow();
        switch (report.Subtype)
        {
            case 0x02:
                _store.TryUpdate(_identity.DeviceId, state =>
                {
                    var cycle = state.NoiseCycle?.Value ?? new NoiseCycleState(0, 0);
                    cycle = report.Type switch
                    {
                        3 => cycle with { LeftMask = report.Bitmap },
                        4 => cycle with { RightMask = report.Bitmap },
                        _ => cycle with { LeftMask = report.Bitmap, RightMask = report.Bitmap },
                    };
                    return state with { NoiseCycle = Observed.From(cycle, now, source) };
                });
                return null;
            case 0x04:
                string? level = Model.FindNoiseModeByReportedBitmap(report.Bitmap)?.Option.LevelId;
                _store.TryUpdate(_identity.DeviceId, state => state with { SmartLevelId = level });
                return null;
            case 0x01:
                break;
            default:
                return null;
        }

        var mode = Model.FindNoiseModeByReportedBitmap(report.Bitmap);
        if (mode is null)
        {
            _logger.LogInformation("Unrecognized noise-control bitmap 0x{Bitmap:X} for {Model}", report.Bitmap, Model.ModelId);
            return null;
        }
        var value = mode.Option.ToState();
        _store.TryUpdate(_identity.DeviceId, state => state with { NoiseControl = Observed.From(value, now, source), LastUpdateSource = source });
        return value;
    }

    private void ApplyEqualizer(byte index, StateSource source)
    {
        string? id = Model.EqualizerPresets.TryGetValue(index, out var preset) ? preset.Id : null;
        var now = _time.GetUtcNow();
        _store.TryUpdate(_identity.DeviceId, state => state with { Equalizer = Observed.From(new EqualizerState(id, index), now, source), LastUpdateSource = source });
    }

    /// <summary>A built-in preset replaces the active custom one; the device does not resend the custom list.</summary>
    private static DeviceState DeselectCustomEqualizer(DeviceState state, DateTimeOffset now, StateSource source) =>
        state.CustomEqualizers is { } list && list.Value.Any(p => p.IsSelected)
            ? state with { CustomEqualizers = Observed.From<IReadOnlyList<CustomEqualizer>>(list.Value.Select(p => p with { IsSelected = false }).ToArray(), now, source) }
            : state;

    private void ApplyFeatures(IReadOnlyList<OppoFeatureValue> values, StateSource source)
    {
        var now = _time.GetUtcNow();
        _store.TryUpdate(_identity.DeviceId, state =>
        {
            var toggles = state.Toggles;
            foreach (var value in values)
            {
                foreach (var feature in Model.FeatureSwitches.Where(f => f.FeatureId == value.FeatureId))
                {
                    toggles = toggles.SetItem(feature.Toggle, Observed.From(value.Value != 0, now, source));
                }
            }
            // Spatial V1 is feature 27: show it as off / fixed like V2.
            if (!Model.HasSpatialAudioV2 && Model.FeatureSwitches.FirstOrDefault(f => f.Toggle == DeviceToggle.SpatialAudio) is { } v1
                && values.FirstOrDefault(v => v.FeatureId == v1.FeatureId) is { } reported && values.Any(v => v.FeatureId == v1.FeatureId))
            {
                state = state with { Spatial = Observed.From(reported.Value != 0 ? SpatialAudioMode.Fixed : SpatialAudioMode.Off, now, source) };
            }
            return state with { Toggles = toggles, LastUpdateSource = source };
        });
    }

    private Task<RequestResult?> RequestIfAllowedAsync(OppoRequest request, CancellationToken cancellationToken)
    {
        if (_remoteCapabilities is { } caps && !caps.Allows(request.Command))
        {
            _logger.LogDebug("Skipping {Request}: not in remote capability bitmap", request);
            return Task.FromResult<RequestResult?>(null);
        }
        return RequestAsync(request, cancellationToken);
    }

    private async Task<RequestResult?> RequestAsync(OppoRequest request, CancellationToken cancellationToken) =>
        await _session.RequestAsync(request, CommandTimeout, cancellationToken).ConfigureAwait(false);

    private bool IsAvailable(DeviceCapability capability) => Model.Capabilities.IsAvailable(capability, _allowExperimental());

    private bool IsAllowed(CapabilitySupport support) =>
        support == CapabilitySupport.Supported || (support == CapabilitySupport.Experimental && _allowExperimental());

    private static readonly OppoRequest NoiseQuery = new(OppoCommand.QueryNoiseControl, [0x01, 0x01]);

    internal static byte[] MaskBytes(int index)
    {
        var mask = new byte[index / 8 + 1];
        mask[index / 8] = (byte)(1 << (index % 8));
        return mask;
    }

    private static CommandResult ToCommandResult(RequestStatus status, string what) => status switch
    {
        RequestStatus.Success => CommandResult.Ok,
        RequestStatus.Timeout => new CommandResult(CommandStatus.Timeout, $"{what}: no response from the device."),
        RequestStatus.TransportError => new CommandResult(CommandStatus.TransportError, $"{what}: control channel error."),
        RequestStatus.Cancelled => new CommandResult(CommandStatus.Cancelled),
        _ => CommandResult.NotConnected,
    };

    public async ValueTask DisposeAsync()
    {
        _session.UnsolicitedFrame -= OnUnsolicitedFrame;
        _session.Closed -= OnSessionClosed;
        await _keepAliveStop.CancelAsync().ConfigureAwait(false);
        foreach (var task in new[] { _keepAliveTask })
        {
            if (task is null) continue;
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        _keepAliveStop.Dispose();
        if (OwnsSession) await _session.DisposeAsync().ConfigureAwait(false);
    }
}
