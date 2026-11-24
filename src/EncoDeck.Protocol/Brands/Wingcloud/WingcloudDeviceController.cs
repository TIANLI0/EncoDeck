// SPDX-License-Identifier: GPL-3.0-only
// Protocol facts from the independent Android app "我的X5s Pro" by shui-tiao
// (https://github.com/shui-tiao/my-x5s-pro, GPL-3.0), verified on a
// HONOR CHOICE Earbuds X5s Pro (Wingcloud / USFY, firmware 1.1.64.2).
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Wingcloud;

/// <summary>
/// X5s Pro controller: <c>2B xx</c> service commands for battery, noise control and the hold cycle, and
/// the <c>01 39</c> / <c>01 3A</c> property channel for EQ, LDAC, surround sound and the pinch gestures.
/// Property writes are answered with <c>7F 04 00 01 86 A0</c> whether or not they apply (the official
/// app ignores that code too), so every write is confirmed by a fresh read of the same address.
/// </summary>
internal sealed class WingcloudDeviceController(IDuplexByteChannel channel, ControllerContext context, WingcloudX5sProProfile profile)
    : RequestReplyController(channel, context)
{
    internal const ushort Battery = 0x2B39, BatteryEnvelope = 0x0127;
    internal const ushort Firmware = 0x2B0A;
    internal const ushort AncGet = 0x2B2A, AncSet = 0x2B04, AncAck = 0x2B04;
    internal const ushort CycleGet = 0x2B19, CycleSet = 0x2B18;
    internal const ushort PropertyRead = 0x013A, PropertyWrite = 0x0139;

    /// <summary>Services: 3 earphone (battery, noise level, EQ), 4 pinch, 5 pinch-and-hold, 6 settings.</summary>
    internal const int EarphoneService = 3, SettingsService = 6, PinchService = WingcloudGestures.PinchService;
    internal const int EqProperty = 5, LdacProperty = 0, SpatialProperty = 1;

    private static readonly byte[] EmptyQuery = [1, 0];

    /// <summary>The earbuds answer each handshake frame about a second later; this covers that.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The fixed acknowledgement of an unsolicited <c>2B 04</c> report (mode first).</summary>
    private static readonly byte[] AncAckPayload = [2, 1, 0];

    /// <summary>An LDAC change restarts the earbuds, so the acknowledgement may never come.</summary>
    private static readonly TimeSpan LdacRestartTimeout = TimeSpan.FromMilliseconds(700);

    private readonly WingcloudFrameCodec _codec = new();
    private WingcloudX5sProProfile _profile = profile;

    protected override IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes)
    {
        var frames = _codec.Feed(bytes);
        foreach (var frame in frames)
            Context.Trace?.OnBrandFrame(FrameDirection.Received,
                $"wingcloud {Hex([.. _codec.Encode(frame.Command, frame.Payload)])}", Now);
        return [.. frames.Select(f => new BrandPacket(f.Command, 0, f.Payload))];
    }

    private static string Hex(ReadOnlySpan<byte> data) =>
        data.IsEmpty ? "-" : string.Join(' ', Convert.ToHexString(data).Chunk(2).Select(c => new string(c)));

    protected override void HandlePacket(BrandPacket packet)
    {
        if (!WingcloudFrameCodec.TryParseTlv(packet.Payload, out var fields) || fields.ContainsKey(127)) return;
        switch (packet.Command)
        {
            // This firmware never answers 2B 39; the levels arrive in the periodic 01 27 envelope instead.
            case Battery:
            case BatteryEnvelope:
                ApplyBattery(fields);
                break;
            case Firmware:
                ApplyFirmware(fields);
                break;
            case AncGet:
                if (ParseNoise(fields) is { } noise)
                    Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(noise, Now, StateSource.QueryResponse) });
                break;
            // Unsolicited mode report (mode first); the earbuds expect an acknowledgement back.
            case AncSet:
                if (ParseNoiseReport(fields) is { } pushed)
                {
                    Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(pushed, Now, StateSource.DeviceNotification) });
                    _ = WriteAsync(_codec.Encode(AncAck, AncAckPayload), CancellationToken.None);
                }
                break;
            case CycleGet:
                if (fields.TryGetValue(1, out var right) && right is [var r] && fields.TryGetValue(2, out var left) && left is [var l]
                    && Mask(l) is { } leftMask && Mask(r) is { } rightMask)
                    Store.TryUpdate(DeviceId, s => s with { NoiseCycle = Observed.From(new NoiseCycleState(leftMask, rightMask), Now, StateSource.QueryResponse) });
                break;
        }
    }

    private static uint? Mask(byte wire) => WingcloudGestures.Cycles.FirstOrDefault(c => c.Value == wire).Mask is var mask && mask != 0
        ? mask
        : null;

    /// <summary>
    /// Information frame: TLV 2 = left / right / case level in that order. TLV 3 is always
    /// <c>64 64 00</c> on this firmware and is not a charging bit, so it stays unparsed.
    /// </summary>
    private void ApplyBattery(Dictionary<byte, byte[]> fields)
    {
        if (!fields.TryGetValue(2, out var levels) || levels.Length != 3 || levels.Any(v => v > 100)) return;
        // TLV 1 is a single byte carrying the same case level as TLV 2's third byte; TLV 2 is authoritative.
        BatteryComponent[] components = [BatteryComponent.Left, BatteryComponent.Right, BatteryComponent.Case];
        Store.TryUpdate(DeviceId, state =>
        {
            var battery = state.Battery;
            for (int i = 0; i < components.Length; i++)
            {
                // A zero case level means the earbuds did not report one, not an empty battery.
                if (components[i] == BatteryComponent.Case && levels[i] == 0) continue;
                battery = battery.With(components[i], Observed.From(new BatteryReading(levels[i], null), Now, StateSource.QueryResponse));
            }
            return state with { Battery = battery, LastUpdateSource = StateSource.QueryResponse };
        });
    }

    /// <summary>Identity frame: TLV 5 = prodId (USFY), TLV 6 = version (1.0.0), TLV 1 = serial.</summary>
    private void ApplyFirmware(Dictionary<byte, byte[]> fields)
    {
        // 2B 0A answers TLVs, not text lines: 1 serial, 2 zeros, 3 code "082", 4 "501", 5 prodId, 6 version.
        static string Text(byte[] value) =>
            System.Text.Encoding.ASCII.GetString([.. value.Select(b => b is >= 32 and < 127 ? b : (byte)' ')]).Trim();

        var extra = new Dictionary<string, string>();
        foreach ((byte tag, string key) in new (byte, string)[] { (1, "serial"), (3, "code"), (4, "sku"), (2, "flags") })
            if (fields.TryGetValue(tag, out var raw) && Text(raw) is { Length: > 0 } item)
                extra[key] = item;

        string? version = fields.TryGetValue(6, out var versionRaw) ? Text(versionRaw) : null;
        string? model = fields.TryGetValue(5, out var modelRaw) ? Text(modelRaw) : null;
        if (version is null && model is null) return;
        // 2B 0A's tag 6 is the control-protocol version (1.0.0); the firmware version comes from 01 07,
        // so this only fills in what the handshake could not supply.
        Store.TryUpdate(DeviceId, s => s with
        {
            Firmware = Observed.From(s.Firmware is { } known
                ? known.Value with { ModelCode = known.Value.ModelCode ?? model, Extra = Merge(known.Value.Extra, extra) ?? known.Value.Extra }
                : new FirmwareInfo(version, model, extra), Now, StateSource.QueryResponse),
        });
    }

    private static IReadOnlyDictionary<string, string>? Merge(IReadOnlyDictionary<string, string>? left, Dictionary<string, string> right)
    {
        if (right.Count == 0) return left;
        var merged = left is null ? [] : new Dictionary<string, string>(left);
        foreach (var (key, value) in right) merged[key] = value;
        return merged;
    }

    /// <summary>
    /// <c>2B 2A</c> answers <c>[level][mode]</c> (the write sends the reverse order): mode 0 off,
    /// 1 noise cancelling with the level, 2 transparency.
    /// </summary>
    internal static NoiseControlState? ParseNoise(Dictionary<byte, byte[]> fields) =>
        fields.TryGetValue(1, out var value) && value is [var level, var mode] ? FromWire(level, mode) : null;

    /// <summary>An unsolicited <c>2B 04</c> report: the same two bytes as the write, mode first.</summary>
    internal static NoiseControlState? ParseNoiseReport(Dictionary<byte, byte[]> fields) =>
        fields.TryGetValue(1, out var value) && value is [var mode, var level] ? FromWire(level, mode) : null;

    private static NoiseControlState? FromWire(byte level, byte mode) => mode switch
    {
        0 => new(NoiseControlMode.Off),
        1 when LevelName(level) is { } name => new(NoiseControlMode.NoiseCancellation, name),
        2 => new(NoiseControlMode.Transparency),
        _ => null,
    };

    private static string? LevelName(byte level) =>
        level switch { 0 => "dynamic", 1 => "cozy", 2 => "general", 3 => "ultra", _ => null };

    /// <summary>
    /// Sends one frame and traces it. <c>ExchangeAsync</c> is defined on the base class and calls the base
    /// <c>WriteAsync</c> internally, so tracing there would never see our frames; every send goes through here.
    /// </summary>
    private Task<BrandPacket?> Exchange(ushort command, byte[] payload, Func<BrandPacket, bool> match, CancellationToken ct)
    {
        byte[] frame = _codec.Encode(command, payload);
        Context.Trace?.OnBrandFrame(FrameDirection.Sent, $"wingcloud {Hex(frame)}", Now);
        return ExchangeAsync(frame, match, ct);
    }

    private static bool IsAck(BrandPacket packet) =>
        WingcloudFrameCodec.TryParseTlv(packet.Payload, out var fields) && fields.ContainsKey(127);

    private Task<BrandPacket?> Query(ushort command, CancellationToken ct) =>
        Exchange(command, EmptyQuery, p => p.Command == command && !IsAck(p), ct);

    /// <summary>
    /// Reads one property. This firmware answers nothing for an address it does not have, so a miss
    /// returns null instead of failing the session.
    /// </summary>
    private async Task<int?> ReadPropertyAsync(int service, int property, CancellationToken ct)
    {
        var reply = await Exchange(PropertyRead, WingcloudFrameCodec.ReadProperty(service, property),
            p => p.Command == PropertyRead && WingcloudFrameCodec.TryParseTlv(p.Payload, out var outer)
                && outer.TryGetValue(0x81, out var middle) && WingcloudFrameCodec.TryParseTlv(middle, out var nested)
                && nested.TryGetValue(0x82, out var innerRaw) && WingcloudFrameCodec.TryParseTlv(innerRaw, out var inner)
                && inner.TryGetValue(3, out var s) && s.Length == 4 && WingcloudFrameCodec.ReadInt32(s) == service
                && inner.TryGetValue(4, out var pr) && pr.Length == 4 && WingcloudFrameCodec.ReadInt32(pr) == property, ct).ConfigureAwait(false);
        return reply is not null && WingcloudFrameCodec.TryParsePropertyReply(reply.Payload, service, property, out int value) ? value : null;
    }

    /// <summary>Writes one property and confirms it by reading the same address back.</summary>
    private async Task<CommandResult> WritePropertyAsync(int service, int property, int value, CancellationToken ct)
    {
        await Exchange(PropertyWrite, WingcloudFrameCodec.WriteProperty(service, property, value),
            p => p.Command == PropertyWrite, ct).ConfigureAwait(false);
        if (Closed.IsCompleted || Store.Current.Device?.DeviceId != DeviceId) return TimedOut;
        int? actual = await ReadPropertyAsync(service, property, ct).ConfigureAwait(false);
        return actual is null
            ? new(CommandStatus.Rejected, "The earbuds did not report this setting back.")
            : actual == value ? CommandResult.Ok : new(CommandStatus.Rejected, "The earbuds reported a different value.");
    }

    /// <summary>
    /// <c>2B 04</c> payload: <c>[selector][length][mode][level]</c>, mode first (the report, and the official
    /// app's anonymous write, send the same order). The earbuds answer it with the <c>7F</c> code.
    /// </summary>
    internal static byte[] NoiseWrite(NoiseControlState target)
    {
        byte mode = target.Mode switch { NoiseControlMode.Off => 0, NoiseControlMode.NoiseCancellation => 1, _ => 2 };
        byte level = target.LevelId switch { "cozy" => 1, "general" => 2, "ultra" => 3, _ => 0 };
        return [1, 2, mode, level];
    }

    protected override bool CanRunCommands => base.CanRunCommands && Store.Current.Device?.DeviceId == DeviceId;

    private async Task<CommandResult> WriteConfirmed(ushort set, byte[] payload, ushort get, CancellationToken ct)
    {
        // The earbuds acknowledge the write with the fixed 7F code either way; only the read-back decides.
        await Exchange(set, payload, p => p.Command == set, ct).ConfigureAwait(false);
        if (Closed.IsCompleted || Store.Current.Device?.DeviceId != DeviceId) return TimedOut;
        var response = await Query(get, ct).ConfigureAwait(false);
        if (response is null) return TimedOut;
        if (!WingcloudFrameCodec.TryParseTlv(response.Payload, out var fields) || ParseNoise(fields) is not { } state)
            return new(CommandStatus.Rejected, "The earbuds did not report the new state back.");
        NoiseControlMode expected = payload[2] switch { 0 => NoiseControlMode.Off, 1 => NoiseControlMode.NoiseCancellation, _ => NoiseControlMode.Transparency };
        return state.Mode == expected ? CommandResult.Ok : TimedOut;
    }

    /// <summary>
    /// <c>2B 2A</c> answers <c>[level][mode]</c> (the write sends the reverse order); mode 0 off,
    /// 1 noise cancelling with the level, 2 transparency. The <c>2B 04</c> write acknowledgement
    /// (<c>2B 04 02 01 00</c>) carries no state and must never be read as one.
    /// </summary>
    private void ConfirmNoise(NoiseControlState state) =>
        Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(state, Now, StateSource.CommandConfirmation) });

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        // The earbuds ignore every 2B xx command until the session is established, and they answer each
        // handshake frame about a second later, so the frames are sent one at a time and each answer is
        // awaited before the next goes out. Without 01 3E (devType) the firmware silently drops most
        // 2B xx settings; without 01 27 at all it drops the queries as well.
        bool answered = false;
        byte[]? identity = null;
        foreach ((ushort command, byte[] frame) in WingcloudHandshake.Frames)
        {
            Context.Trace?.OnBrandFrame(FrameDirection.Sent, $"wingcloud {Hex(frame)}", Now);
            var reply = await ExchangeAsync(frame, p => p.Command == command, cancellationToken, HandshakeTimeout).ConfigureAwait(false);
            answered |= command == BatteryEnvelope && reply is not null;
            if (command == 0x0107) identity = reply?.Payload;
        }

        // The earbuds never answer 2B 39 on this firmware; the levels arrive in the 01 27 envelope above,
        // which is also what proves the session is up.
        if (!answered) return false;
        // The real firmware version (1.1.64.2) only appears in the 01 07 answer; 2B 0A reports 1.0.0.
        if (identity is not null) ApplyIdentity(WingcloudHandshake.ParseIdentity(identity));
        await Query(Firmware, cancellationToken).ConfigureAwait(false);
        await RefreshSettings(cancellationToken).ConfigureAwait(false);
        StartPolling();
        return true;
    }

    private void ApplyIdentity((string? Version, string? Model, Dictionary<string, string> Extra)? identity)
    {
        if (identity is not { } value) return;
        Store.TryUpdate(DeviceId, s => s with
        {
            Firmware = Observed.From(new FirmwareInfo(value.Version, value.Model, value.Extra.Count > 0 ? value.Extra : new Dictionary<string, string>()), Now, StateSource.QueryResponse),
        });
    }

    /// <summary>
    /// Re-reads every setting and opens the ones this connection actually answered. Unanswered
    /// addresses stay unknown, so nothing is offered that these earbuds did not confirm.
    /// </summary>
    private async Task RefreshSettings(CancellationToken ct)
    {
        if (await Query(AncGet, ct).ConfigureAwait(false) is not null) _profile = _profile.With(noise: true);
        int? eq = await ReadPropertyAsync(EarphoneService, EqProperty, ct).ConfigureAwait(false);
        if (eq is not null && WingcloudX5sProProfile.EqNames.Any(p => p.Value == eq))
            Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState(eq.Value.ToString(), eq), Now, StateSource.QueryResponse) });
        bool eqConfirmed = eq is not null;
        bool qualityConfirmed = false;
        foreach ((int property, DeviceToggle toggle) in new[] { (LdacProperty, DeviceToggle.Ldac), (SpatialProperty, DeviceToggle.SpatialAudio) })
        {
            int? value = await ReadPropertyAsync(SettingsService, property, ct).ConfigureAwait(false);
            if (value is null || value is not (0 or 1)) continue;
            qualityConfirmed = true;
            Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(toggle, Observed.From(value == 1, Now, StateSource.QueryResponse)) });
        }
        bool gesturesConfirmed = false;
        foreach (var slot in WingcloudGestures.Slots.Where(s => s.Choices.Count > 1))
            gesturesConfirmed |= await ReadGestureAsync(slot, ct).ConfigureAwait(false) is not null;
        bool cycleConfirmed = false;
        if (await Query(CycleGet, ct).ConfigureAwait(false) is not null) cycleConfirmed = true;
        _profile = _profile.With(eq: eqConfirmed, quality: qualityConfirmed, gestures: gesturesConfirmed, cycle: cycleConfirmed);
        Store.TryUpdate(DeviceId, s => s with { Profile = _profile });
    }

    /// <summary>Reads one gesture slot and stores it when the value is one the slot offers.</summary>
    private async Task<int?> ReadGestureAsync(BrandGestureSlot slot, CancellationToken ct)
    {
        if (WingcloudGestures.Property(slot) is not { } property) return null;
        int? value = await ReadPropertyAsync(WingcloudGestures.Service(slot), property, ct).ConfigureAwait(false);
        return value is { } v && slot.Choices.Any(c => c.Function == v) ? StoreGesture(slot, v) : null;
    }

    private int StoreGesture(BrandGestureSlot slot, int value)
    {
        Store.TryUpdate(DeviceId, s => s with
        {
            KeyFunctions = Observed.From<IReadOnlyList<KeyFunction>>(
                [.. (s.KeyFunctions?.Value ?? []).Where(k => !slot.Matches(k)).Append(new KeyFunction(slot.Side, slot.Button, slot.Action, value))],
                Now, StateSource.QueryResponse),
        });
        return value;
    }

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => RunExclusiveAsync(Operations, async ct =>
    {
        // 2B 39 is never answered on this firmware; the battery arrives in the 01 27 envelope instead,
        // so a refresh re-reads the settings and the noise cycle, which do answer.
        await RefreshSettings(ct).ConfigureAwait(false);
        return CommandResult.Ok;
    }, cancellationToken);

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!_profile.NoiseConfirmed) return Task.FromResult(NotSupported("Noise control"));
        // Picking a mode without a level ("noise cancelling") means the first level that mode offers;
        // the pinching cycle needs a concrete level, so it cannot be left implicit on the wire.
        var offered = _profile.NoiseControlOptions;
        var option = target.LevelId is null
            ? offered.FirstOrDefault(o => o.Mode == target.Mode && o.LevelId is not null) ?? offered.FirstOrDefault(o => o.Matches(target))
            : offered.FirstOrDefault(o => o.Matches(target));
        if (option is null) return Task.FromResult(NotSupported("Noise control"));
        var requested = option.ToState();
        return RunCommandAsync(PendingKeys.NoiseControl, Operations, async ct =>
        {
            var result = await WriteConfirmed(AncSet, NoiseWrite(requested), AncGet, ct).ConfigureAwait(false);
            if (result.IsSuccess) ConfirmNoise(requested);
            return result;
        }, cancellationToken);
    }

    public override Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        if (!_profile.EqualizerPresets.Any(p => p.Id == presetId) || !int.TryParse(presetId, out int value))
            return Task.FromResult(NotSupported("EQ preset"));
        return RunCommandAsync(PendingKeys.Equalizer, Operations, async ct =>
        {
            var result = await WritePropertyAsync(EarphoneService, EqProperty, value, ct).ConfigureAwait(false);
            if (result.IsSuccess)
                Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState(presetId, value), Now, StateSource.CommandConfirmation) });
            return result;
        }, cancellationToken);
    }

    public override Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        int property = toggle switch
        {
            DeviceToggle.Ldac => LdacProperty,
            DeviceToggle.SpatialAudio => SpatialProperty,
            _ => -1,
        };
        if (property < 0 || !_profile.QualityConfirmed)
            return Task.FromResult(NotSupported(toggle.ToString()));
        return RunCommandAsync(PendingKeys.Toggle(toggle), Operations, async ct =>
        {
            if (toggle == DeviceToggle.Ldac) return await SetLdacAsync(enabled, ct).ConfigureAwait(false);
            var result = await WritePropertyAsync(SettingsService, property, enabled ? 1 : 0, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return result;
            Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(toggle, Observed.From(enabled, Now, StateSource.CommandConfirmation)) });
            // The earbuds drop LDAC when the surround effect goes on; turning LDAC on clears the surround
            // switch in SetLdacAsync instead, because the restart leaves nothing to read back.
            if (enabled && await ReadPropertyAsync(SettingsService, LdacProperty, ct).ConfigureAwait(false) is 0)
                Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(DeviceToggle.Ldac, Observed.From(false, Now, StateSource.QueryResponse)) });
            return result;
        }, cancellationToken);
    }

    /// <summary>
    /// The firmware restarts its radio on an LDAC change, so the link drops before any read-back can arrive:
    /// the write is trusted as the official app trusts it, and the surround switch is cleared because the
    /// earbuds always turn it off when LDAC goes on. A later session read replaces these values with the truth.
    /// </summary>
    private async Task<CommandResult> SetLdacAsync(bool enabled, CancellationToken ct)
    {
        byte[] frame = _codec.Encode(PropertyWrite, WingcloudFrameCodec.WriteProperty(SettingsService, LdacProperty, enabled ? 1 : 0));
        Context.Trace?.OnBrandFrame(FrameDirection.Sent, $"wingcloud {Hex(frame)}", Now);
        // The acknowledgement only arrives when the restart is delayed; losing it means nothing here.
        await ExchangeAsync(frame, p => p.Command == PropertyWrite, ct, LdacRestartTimeout).ConfigureAwait(false);
        Store.TryUpdate(DeviceId, s => s with
        {
            Toggles = (enabled
                ? s.Toggles.SetItem(DeviceToggle.Ldac, Observed.From(true, Now, StateSource.CommandConfirmation))
                    .SetItem(DeviceToggle.SpatialAudio, Observed.From(false, Now, StateSource.CommandConfirmation))
                : s.Toggles.SetItem(DeviceToggle.Ldac, Observed.From(false, Now, StateSource.CommandConfirmation))),
            // The restart ends this session the same way an unplugged cable does; reconnect instead of
            // reporting that the setting failed.
            LinkRestarting = s.LinkRestarting || enabled,
        });
        return CommandResult.Ok;
    }

    public override Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken)
    {
        var slot = _profile.GestureSlots.FirstOrDefault(s => s.Matches(assignment));
        int property = slot is null ? -1 : WingcloudGestures.Property(slot) ?? -1;
        var choice = slot?.Choices.FirstOrDefault(c => c.Function == assignment.Function);
        if (slot is null || choice is null || property < 0
            || (choice.Requires is { } capability && !_profile.Capabilities.IsAvailable(capability, allowExperimental: false)))
            return Task.FromResult(NotSupported("Gesture assignment"));
        return RunCommandAsync(PendingKeys.KeyFunction, Operations, async ct =>
        {
            var result = await WritePropertyAsync(WingcloudGestures.Service(slot), property, assignment.Function, ct).ConfigureAwait(false);
            if (result.IsSuccess) StoreGesture(slot, assignment.Function);
            return result;
        }, cancellationToken);
    }

    public override Task<CommandResult> SetNoiseCycleAsync(EarSide side, uint modeMask, CancellationToken cancellationToken)
    {
        var cycle = WingcloudGestures.Cycles.FirstOrDefault(c => c.Mask == modeMask && modeMask != 0);
        if (!_profile.CycleConfirmed || cycle.Mask == 0 || side is not (EarSide.Left or EarSide.Right or EarSide.Both))
            return Task.FromResult(NotSupported("Noise cycle"));
        return RunCommandAsync(PendingKeys.NoiseCycle, Operations, async ct =>
        {
            // Only the left field takes effect and both earbuds follow it, so both fields carry the value.
            var ack = await Exchange(CycleSet, [1, 1, (byte)cycle.Value, 2, 1, (byte)cycle.Value], p => p.Command == CycleSet, ct).ConfigureAwait(false);
            if (Closed.IsCompleted || Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
            if (ack is null) return TimedOut;
            var response = await Query(CycleGet, ct).ConfigureAwait(false);
            if (Closed.IsCompleted || Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
            // 2B 19 answers tag 1 = right, tag 2 = left, the reverse of the write.
            if (response is null || !WingcloudFrameCodec.TryParseTlv(response.Payload, out var fields)
                || !fields.TryGetValue(1, out var right) || right is not [var r] || r != cycle.Value
                || !fields.TryGetValue(2, out var left) || left is not [var l] || l != cycle.Value) return TimedOut;
            Store.TryUpdate(DeviceId, s => s with
            {
                NoiseCycle = Observed.From(new NoiseCycleState(modeMask, modeMask), Now, StateSource.CommandConfirmation),
            });
            return CommandResult.Ok;
        }, cancellationToken);
    }
}
