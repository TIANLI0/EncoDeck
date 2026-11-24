// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Real-device fixes from OpenFreebuds (https://github.com/melianmiko/OpenFreebuds, MelianMiko and
// contributors, GPL-3.0; issues/PRs #116, #133, #138, #142) and HuaweiPods
// (https://github.com/Nshpiter/HuaweiPods, Nshpiter and contributors, GPL-3.0).
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Huawei;

/// <summary>
/// Drives a HUAWEI control channel. Writes are acknowledged with result code 100000 at best (and
/// some, like noise control on FreeBuds Pro 5, not at all), so every write is confirmed by reading
/// the value back; any other result code on the written command rejects it. Unconfirmed optimistic
/// values are rolled back.
/// </summary>
internal sealed partial class HuaweiDeviceController : BrandControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ReadBackDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan[] SettleDelays = [TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300)];

    private static readonly byte[] BatteryQuery = [0x01, 0x00, 0x02, 0x00, 0x03, 0x00];
    private static readonly byte[] AncQuery = [0x01, 0x00];
    private static readonly byte[] WearQuery = [0x01, 0x00];
    private static readonly byte[] EqQuery = [0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00, 0x05, 0x00, 0x06, 0x00, 0x07, 0x00, 0x08, 0x00];
    private static readonly byte[] LowLatencyQuery = [0x02, 0x00];
    private static readonly byte[] DualQuery = [0x01, 0x00];
    private static readonly byte[] BigDataReply = [0x02, 0x01, 0x00];

    private readonly IHuaweiFrameCodec _codec;
    private readonly Lock _gate = new();
    private readonly Dictionary<ushort, List<Waiter>> _waiters = [];
    private volatile bool _responded;

    private sealed record Waiter(TaskCompletionSource<HuaweiFrame> Source, bool AcceptsAck);

    public HuaweiDeviceController(IDuplexByteChannel channel, ControllerContext context, HuaweiDeviceProfile profile, IHuaweiFrameCodec codec)
        : base(channel, context)
    {
        Profile = profile;
        _codec = codec;
    }

    public HuaweiDeviceProfile Profile { get; private set; }

    private HuaweiModel Model => Profile.Model;

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting HUAWEI session for {Model} ({Protocol} framing)", Model.DisplayName, _codec.Protocol);
        if (Model.PreferredSppChannel != 0)
        {
            Logger.LogDebug("Reference pins RFCOMM channel {Channel}; EncoDeck opens the SPP service via SDP", Model.PreferredSppChannel);
        }

        // Battery doubles as the protocol probe: the SPP UUID is generic, so only an answer proves it's HUAWEI.
        await QueryAsync(HuaweiCommands.Battery, BatteryQuery, cancellationToken).ConfigureAwait(false);
        if (!_responded && Model.NoiseControlUsable)
        {
            await QueryAsync(HuaweiCommands.QueryAnc, AncQuery, cancellationToken).ConfigureAwait(false);
        }
        if (!_responded)
        {
            Logger.LogInformation("No HUAWEI protocol response on the control channel");
            return false;
        }
        await RefreshAllAsync(skipBattery: true, cancellationToken).ConfigureAwait(false);
        // Read-only feature probes; unsupported firmware simply leaves the control unavailable.
        await QueryAsync(HuaweiCommands.FeatureState, SpatialQuery, cancellationToken).ConfigureAwait(false);
        await ProbeFeaturesAsync(confirmedOnly: false, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => RunExclusiveAsync(_operations, RefreshCoreAsync, cancellationToken);

    private async Task<CommandResult> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (Closed.IsCompleted) return CommandResult.NotConnected;
        var status = await RefreshAllAsync(skipBattery: false, cancellationToken).ConfigureAwait(false);
        if (Profile.SpatialConfirmed) await QueryAsync(HuaweiCommands.FeatureState, SpatialQuery, cancellationToken).ConfigureAwait(false);
        await ProbeFeaturesAsync(confirmedOnly: true, cancellationToken).ConfigureAwait(false);
        return status == CommandStatus.Success ? CommandResult.Ok : new CommandResult(status, "Refresh: no response from the device.");
    }

    private async Task<CommandStatus> RefreshAllAsync(bool skipBattery, CancellationToken ct)
    {
        var worst = CommandStatus.Success;
        void Track((HuaweiFrame? Frame, CommandStatus Status) r)
        {
            if (r.Status != CommandStatus.Success && worst == CommandStatus.Success) worst = r.Status;
        }

        if (!skipBattery && Model.RfcommBattery) Track(await QueryAsync(HuaweiCommands.Battery, BatteryQuery, ct).ConfigureAwait(false));
        if (Model.NoiseControlUsable) Track(await QueryAsync(HuaweiCommands.QueryAnc, AncQuery, ct).ConfigureAwait(false));
        if (Model.WearDetection) Track(await QueryAsync(HuaweiCommands.QueryWearDetection, WearQuery, ct).ConfigureAwait(false));
        if (Model.EqPresets.Count > 0) Track(await QueryAsync(HuaweiCommands.QueryEqualizer, EqQuery, ct).ConfigureAwait(false));
        if (Model.LowLatency) Track(await QueryAsync(HuaweiCommands.LowLatency, LowLatencyQuery, ct).ConfigureAwait(false));
        if (Model.DualConnect) Track(await QueryAsync(HuaweiCommands.QueryDualConnect, DualQuery, ct).ConfigureAwait(false));
        return worst;
    }

    // ---- Receive path ----

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        IReadOnlyList<HuaweiFrame> frames;
        lock (_gate) frames = _codec.Feed(data);
        foreach (var frame in frames)
        {
            if (!HuaweiWire.TryParseTlv(frame.Payload, out var fields)) continue;
            bool ack = HuaweiWire.IsSuccessAck(fields);
            if (!ack && !fields.ContainsKey(HuaweiCommands.ErrorTlv) && IsDeviceProbeResponse(frame.Command, fields)) _responded = true;
            Touch();
            if (frame.Command == HuaweiCommands.BigDataCapability && fields.ContainsKey(0x01))
            {
                // FreeBuds 5i disconnects unless the host answers this capability request.
                _ = WriteAsync(_codec.Encode(HuaweiCommands.BigDataCapability, BigDataReply), CancellationToken.None);
                continue;
            }
            List<Waiter>? waiters = null;
            lock (_gate)
            {
                // A bare acknowledgement completes only write waiters: a read on the same command
                // (low latency, feature switches) keeps waiting for the state report.
                if (_waiters.TryGetValue(frame.Command, out var list))
                {
                    waiters = list.FindAll(w => !ack || w.AcceptsAck);
                    list.RemoveAll(w => !ack || w.AcceptsAck);
                    if (list.Count == 0) _waiters.Remove(frame.Command);
                }
            }
            try
            {
                if (!ack) Apply(frame, waiters is { Count: > 0 } ? StateSource.QueryResponse : StateSource.DeviceNotification);
            }
            finally
            {
                if (waiters is not null)
                {
                    foreach (var w in waiters) w.Source.TrySetResult(frame);
                }
            }
        }
    }

    private void Apply(HuaweiFrame frame, StateSource source)
    {
        var fields = HuaweiWire.ParseTlv(frame.Payload);
        if (fields.ContainsKey(HuaweiCommands.ErrorTlv)) return;
        switch (frame.Command)
        {
            case HuaweiCommands.Battery or HuaweiCommands.BatteryAlt:
                ApplyBattery(fields, source);
                break;
            case HuaweiCommands.AncChanged when Model.NoiseControlUsable && fields.TryGetValue(0x01, out var changed) && changed is [<= 2]:
                // Changed on the earbuds: the push only names the mode, so read the full state back.
                _ = WriteAsync(_codec.Encode(HuaweiCommands.QueryAnc, AncQuery), CancellationToken.None);
                break;
            case HuaweiCommands.QueryAnc:
                if (ParseNoise(fields) is { } noise) Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(noise, Now, source), LastUpdateSource = source });
                break;
            case HuaweiCommands.QueryWearDetection:
                ApplyToggle(DeviceToggle.InEarDetection, ParseBool(fields, 0x01), source);
                break;
            case HuaweiCommands.WearState:
                ApplyWearState(fields, source);
                break;
            case HuaweiCommands.QueryEqualizer:
                ApplyEqualizer(fields, source);
                break;
            case HuaweiCommands.LowLatency:
                ApplyToggle(DeviceToggle.GamingMode, ParseBool(fields, 0x02), source);
                break;
            case HuaweiCommands.QueryDualConnect:
                ApplyToggle(DeviceToggle.DualDeviceConnection, ParseBool(fields, 0x01), source);
                break;
            case HuaweiCommands.DualConnectDevices when Model.DualConnect:
                ApplySourceDevice(fields, source);
                break;
            case HuaweiCommands.DualConnectChanged when Model.DualConnect:
                _ = WriteAsync(_codec.Encode(HuaweiCommands.DualConnectDevices, DeviceListQuery), CancellationToken.None);
                break;
            case HuaweiCommands.FeatureState:
                ApplySpatial(frame.Payload, source);
                ApplySmartFeature(frame.Payload, source);
                break;
        }
    }

    private void ApplyBattery(Dictionary<byte, byte[]> fields, StateSource source)
    {
        byte[] levels;
        bool perPart = fields.TryGetValue(0x02, out var parts) && parts.Length > 0;
        if (perPart) levels = parts!;
        else if (fields.TryGetValue(0x01, out var overall) && overall.Length > 0) levels = [overall[0]];
        else return;
        byte[] charging = fields.TryGetValue(0x03, out var c) ? c : [];

        Store.TryUpdate(DeviceId, s =>
        {
            var battery = s.Battery;
            // Single-value reports (headphones, neckbands, glasses) land in Left.
            BatteryComponent[] components = [BatteryComponent.Left, BatteryComponent.Right, BatteryComponent.Case];
            for (int i = 0; i < Math.Min(levels.Length, components.Length); i++)
            {
                if (levels[i] > 100) continue;
                // In the per-part list 0 means "not reported" (e.g. an earbud in the closed case), not 0 % (#142).
                if (perPart && levels[i] == 0)
                {
                    battery = battery.With(components[i], null);
                    continue;
                }
                bool? isCharging = charging.Length > i ? charging[i] != 0 : null;
                battery = battery.With(components[i], Observed.From(new BatteryReading(levels[i], isCharging), Now, source));
            }
            return s with { Battery = battery, LastUpdateSource = source };
        });
    }

    private void ApplyToggle(DeviceToggle toggle, bool? value, StateSource source)
    {
        if (value is not { } on) return;
        Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(toggle, Observed.From(on, Now, source)), LastUpdateSource = source });
    }

    private static bool? ParseBool(Dictionary<byte, byte[]> fields, byte type) =>
        fields.TryGetValue(type, out var v) && v.Length > 0 ? v[0] switch { 0 => false, 1 => true, _ => null } : null;

    /// <summary>
    /// Report TLV 1 is <c>[sub-mode, mode]</c> while the write is <c>[mode, sub-mode]</c>. A sub-mode the
    /// model does not list keeps the mode with an unknown (null) level.
    /// </summary>
    internal NoiseControlState? ParseNoise(Dictionary<byte, byte[]> fields)
    {
        if (!fields.TryGetValue(0x01, out var v) || v.Length < 2) return null;
        byte sub = v[0];
        return v[1] switch
        {
            HuaweiCommands.AncModeOff => new NoiseControlState(NoiseControlMode.Off),
            HuaweiCommands.AncModeNoiseCancellation => new NoiseControlState(NoiseControlMode.NoiseCancellation, Model.AncLevels.FirstOrDefault(l => l.SubMode == sub)?.LevelId),
            HuaweiCommands.AncModeTransparency => new NoiseControlState(NoiseControlMode.Transparency, Model.TransparencyLevels.FirstOrDefault(l => l.SubMode == sub)?.LevelId),
            _ => null,
        };
    }

    /// <summary>
    /// TLV 2 is the selected preset (an unknown or custom id is kept as the raw value); TLV 3, when
    /// present, lists the built-in presets the earbuds offer and narrows the profile.
    /// </summary>
    private void ApplyEqualizer(Dictionary<byte, byte[]> fields, StateSource source)
    {
        if (!fields.TryGetValue(0x02, out var v) || v.Length < 1) return;
        if (fields.TryGetValue(0x03, out var offered) && offered.Length > 0 && Profile.WithReportedEqualizer(offered) is var narrowed && !ReferenceEquals(narrowed, Profile))
        {
            Profile = narrowed;
            Store.TryUpdate(DeviceId, s => s with { Profile = Profile });
        }
        byte id = v[0];
        var preset = Model.EqPresets.FirstOrDefault(p => p.ProtocolId == id);
        string? presetId = preset is not null && Profile.EqualizerPresets.Any(p => p.Id == preset.PresetId) ? preset.PresetId : null;
        var eq = new EqualizerState(presetId, id);
        Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(eq, Now, source), LastUpdateSource = source });
    }

    // ---- Requests ----

    private async Task<(HuaweiFrame? Frame, CommandStatus Status)> QueryAsync(ushort command, byte[] payload, CancellationToken ct, TimeSpan? timeout = null)
    {
        var waiter = Register(command, acceptsAck: false);
        try
        {
            if (!await WriteAsync(_codec.Encode(command, payload), ct).ConfigureAwait(false)) return (null, CommandStatus.TransportError);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(timeout ?? RequestTimeout, Context.Time, timeoutSource.Token);
            var done = await Task.WhenAny(waiter.Task, delay, Closed).ConfigureAwait(false);
            timeoutSource.Cancel();
            ct.ThrowIfCancellationRequested();
            if (done == waiter.Task) return (waiter.Task.Result,
                HuaweiWire.IsError(HuaweiWire.ParseTlv(waiter.Task.Result.Payload)) ? CommandStatus.Rejected : CommandStatus.Success);
            return (null, done == Closed ? CommandStatus.TransportError : CommandStatus.Timeout);
        }
        finally
        {
            Unregister(command, waiter);
        }
    }

    private TaskCompletionSource<HuaweiFrame> Register(ushort command, bool acceptsAck)
    {
        var tcs = new TaskCompletionSource<HuaweiFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_waiters.TryGetValue(command, out var list)) _waiters[command] = list = [];
            list.Add(new Waiter(tcs, acceptsAck));
        }
        return tcs;
    }

    private void Unregister(ushort command, TaskCompletionSource<HuaweiFrame> tcs)
    {
        lock (_gate)
        {
            if (_waiters.TryGetValue(command, out var list) && list.RemoveAll(w => w.Source == tcs) > 0 && list.Count == 0) _waiters.Remove(command);
        }
    }

    // ---- Commands ----

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!Model.NoiseControlUsable) return Task.FromResult(NotSupported("Noise control"));
        // A mode without a level (the mode button) is allowed whenever the mode itself is offered.
        bool offered = Profile.NoiseControlOptions.Any(o => o.Matches(target) || (target.LevelId is null && o.Mode == target.Mode));
        if (!offered) return Task.FromResult(CommandResult.Unsupported($"{target.Mode}/{target.LevelId} is not an option on {Model.DisplayName}."));

        byte mode;
        byte sub;
        switch (target.Mode)
        {
            case NoiseControlMode.Off:
                (mode, sub) = (HuaweiCommands.AncModeOff, 0x00);
                break;
            case NoiseControlMode.NoiseCancellation:
                // 0xFF keeps the level the earbuds last used (#142).
                mode = HuaweiCommands.AncModeNoiseCancellation;
                sub = Model.AncLevels.FirstOrDefault(l => l.LevelId == target.LevelId)?.SubMode ?? HuaweiCommands.AncSubModeDefault;
                break;
            case NoiseControlMode.Transparency:
                mode = HuaweiCommands.AncModeTransparency;
                sub = Model.TransparencyLevels.Count == 0 ? Model.TransparencySubMode
                    : Model.TransparencyLevels.FirstOrDefault(l => l.LevelId == target.LevelId)?.SubMode ?? HuaweiCommands.AncSubModeDefault;
                break;
            default:
                return Task.FromResult(NotSupported(target.Mode.ToString()));
        }

        return ExecuteConfirmedAsync(
            PendingKeys.NoiseControl,
            s => s with { NoiseControl = Observed.From(target, Now, StateSource.Optimistic) },
            HuaweiCommands.SetAnc,
            [0x01, 0x02, mode, sub],
            HuaweiCommands.QueryAnc,
            AncQuery,
            s => s.NoiseControl is { Source: not StateSource.Optimistic } n && n.Value.Mode == target.Mode && (target.LevelId is null || n.Value.LevelId == target.LevelId),
            cancellationToken);
    }

    public override Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        var preset = Profile.EqualizerPresets.Any(p => p.Id == presetId) ? Model.EqPresets.FirstOrDefault(p => p.PresetId == presetId) : null;
        if (preset is null) return Task.FromResult(Model.EqPresets.Count == 0 ? NotSupported("Equalizer") : CommandResult.Unsupported($"EQ preset {presetId} is not available."));
        return ExecuteConfirmedAsync(
            PendingKeys.Equalizer,
            s => s with { Equalizer = Observed.From(new EqualizerState(presetId, preset.ProtocolId), Now, StateSource.Optimistic) },
            HuaweiCommands.SetEqualizer,
            [0x01, 0x01, preset.ProtocolId],
            HuaweiCommands.QueryEqualizer,
            EqQuery,
            s => s.Equalizer is { Source: not StateSource.Optimistic } e && e.Value.RawPresetValue == preset.ProtocolId,
            cancellationToken);
    }

    public override Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        byte value = enabled ? (byte)1 : (byte)0;
        (bool supported, ushort set, byte[] setPayload, ushort query, byte[] queryPayload) = toggle switch
        {
            DeviceToggle.InEarDetection => (Model.WearDetection, HuaweiCommands.SetWearDetection, new byte[] { 0x01, 0x01, value }, HuaweiCommands.QueryWearDetection, WearQuery),
            DeviceToggle.GamingMode => (Model.LowLatency, HuaweiCommands.LowLatency, [0x01, 0x01, value], HuaweiCommands.LowLatency, LowLatencyQuery),
            DeviceToggle.DualDeviceConnection => (Model.DualConnect, HuaweiCommands.SetDualConnect, [0x01, 0x01, value], HuaweiCommands.QueryDualConnect, DualQuery),
            DeviceToggle.AutoVolume or DeviceToggle.HeadGestures when FeatureFor(toggle) is { } feature => (Profile.Toggles.Contains(toggle),
                HuaweiCommands.FeatureState, FeatureWrite(feature.Id, 0x02, value), HuaweiCommands.FeatureState, feature.Query),
            _ => (false, (ushort)0, Array.Empty<byte>(), (ushort)0, Array.Empty<byte>()),
        };
        if (!supported) return Task.FromResult(NotSupported(toggle.ToString()));
        return ExecuteConfirmedAsync(
            PendingKeys.Toggle(toggle),
            s => s with { Toggles = s.Toggles.SetItem(toggle, Observed.From(enabled, Now, StateSource.Optimistic)) },
            set,
            setPayload,
            query,
            queryPayload,
            s => s.Toggles.TryGetValue(toggle, out var v) && v.Source != StateSource.Optimistic && v.Value == enabled,
            cancellationToken);
    }

    /// <summary>
    /// Optimistic value + pending marker → write → (error result code ⇒ Rejected) → read-back until the
    /// store holds the requested value as a confirmed reading. Failures roll back optimistic values.
    /// </summary>
    private Task<CommandResult> ExecuteConfirmedAsync(
        string name, Func<DeviceState, DeviceState> applyOptimistic, ushort setCommand, byte[] setPayload,
        ushort queryCommand, byte[] queryPayload, Func<DeviceState, bool> confirmed, CancellationToken cancellationToken) =>
        RunCommandAsync(name, _operations,
            ct => WriteConfirmedAsync(name, applyOptimistic, setCommand, setPayload, queryCommand, queryPayload, confirmed, ct),
            cancellationToken, applyOptimistic);

    /// <summary><see cref="ExecuteConfirmedAsync"/> for a step of a sequence that already holds the command gate.</summary>
    private Task<CommandResult> ExecuteConfirmedCoreAsync(
        string name, Func<DeviceState, DeviceState> applyOptimistic, ushort setCommand, byte[] setPayload,
        ushort queryCommand, byte[] queryPayload, Func<DeviceState, bool> confirmed, CancellationToken cancellationToken) =>
        RunCommandAsync(name, null,
            ct => WriteConfirmedAsync(name, applyOptimistic, setCommand, setPayload, queryCommand, queryPayload, confirmed, ct),
            cancellationToken, applyOptimistic);

    private async Task<CommandResult> WriteConfirmedAsync(
        string name,
        Func<DeviceState, DeviceState> applyOptimistic,
        ushort setCommand,
        byte[] setPayload,
        ushort queryCommand,
        byte[] queryPayload,
        Func<DeviceState, bool> confirmed,
        CancellationToken cancellationToken)
    {
        // When the write and the query share a command (low latency, feature switches), an error reply
        // fails the read-back and a bare acknowledgement is skipped by the read.
        var errorWaiter = setCommand != queryCommand ? Register(setCommand, acceptsAck: true) : null;
        try
        {
            if (!await WriteAsync(_codec.Encode(setCommand, setPayload), cancellationToken).ConfigureAwait(false))
            {
                return new CommandResult(CommandStatus.TransportError, $"{name}: control channel error.");
            }
            await Task.Delay(ReadBackDelay, Context.Time, cancellationToken).ConfigureAwait(false);
            if (errorWaiter is { Task.IsCompletedSuccessfully: true }
                && HuaweiWire.ParseTlv(errorWaiter.Task.Result.Payload) is var answer && HuaweiWire.IsError(answer))
            {
                return new CommandResult(CommandStatus.Rejected, $"Device rejected {name} (error {Convert.ToHexString(answer[HuaweiCommands.ErrorTlv])}).");
            }
            var (_, status) = await QueryAsync(queryCommand, queryPayload, cancellationToken).ConfigureAwait(false);
            bool matches = status == CommandStatus.Success && confirmed(Store.Current);
            foreach (var settle in SettleDelays)
            {
                if (matches || status != CommandStatus.Success) break;
                // Keep showing the requested value while the earbuds settle (a delayed report may still be stale).
                Store.TryUpdate(DeviceId, applyOptimistic);
                await Task.Delay(settle, Context.Time, cancellationToken).ConfigureAwait(false);
                (_, status) = await QueryAsync(queryCommand, queryPayload, cancellationToken).ConfigureAwait(false);
                matches = status == CommandStatus.Success && confirmed(Store.Current);
            }
            return status != CommandStatus.Success
                ? new CommandResult(status, $"{name}: no read-back from the device.")
                : matches ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The device reports a different value.");
        }
        finally
        {
            if (errorWaiter is not null) Unregister(setCommand, errorWaiter);
        }
    }
}
