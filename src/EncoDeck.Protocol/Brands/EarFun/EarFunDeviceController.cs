// SPDX-License-Identifier: GPL-3.0-only
// EarKit a77acd18890a5f28746c2303bfff0e23fff8d8ca (MIT), adapted to confirmed state and serialized transactions.
using System.Collections.Concurrent;
using System.Text;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.EarFun;

internal sealed class EarFunDeviceController(IDuplexByteChannel channel, ControllerContext context, EarFunProfile profile)
    : RequestReplyController(channel, context)
{
    private readonly EarFunFrameCodec _codec = new(profile.Model.Gaia);
    private readonly ConcurrentDictionary<ushort, byte[]> _values = new();
    private EarFunProfile _profile = new(profile.Model);
    private EarFunModel Model => _profile.Model;

    protected override IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes) =>
        [.. _codec.Feed(bytes).Select(p => new BrandPacket(p.Command, p.Status, p.Payload))];

    protected override void HandlePacket(BrandPacket packet)
    {
        if (!Closed.IsCompleted && packet.Type == 0 && Apply(packet, StateSource.DeviceNotification))
        {
            _values[packet.Command] = packet.Payload;
            _profile = new(Model, _values.Keys.ToHashSet());
            Store.TryUpdate(DeviceId, s => s with { Profile = _profile, LastResponseAt = Now });
        }
    }

    private IEnumerable<EarFunSetting> Settings()
    {
        yield return EarFunSetting.Firmware;
        yield return EarFunSetting.Battery;
        if (!Model.Headphones)
        {
            if (Model.Gaia) yield return EarFunSetting.RightBattery;
            yield return EarFunSetting.Case;
            if (!Model.Gaia) { yield return EarFunSetting.Charging; yield return EarFunSetting.CaseCharging; }
        }
        yield return EarFunSetting.Codec;
        foreach (var (feature, setting) in new[] { ("noise", EarFunSetting.Noise), ("game", EarFunSetting.Game),
            ("dual", EarFunSetting.Dual), ("ear", EarFunSetting.Wear), ("prompt", EarFunSetting.Prompt),
            ("find", EarFunSetting.Find), ("spatial", EarFunSetting.Spatial), ("sub", EarFunSetting.Sub),
            ("ambient", EarFunSetting.Ambient), ("autooff", EarFunSetting.AutoOff) })
            if (Model.Has(feature)) yield return setting;
    }

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        Store.TryUpdate(DeviceId, s => s with { Profile = _profile, Battery = BatteryStatus.Unknown,
            NoiseControl = null, Toggles = s.Toggles.Clear(), Options = s.Options.Clear(), PromptVolume = null, Spatial = null,
            Firmware = null, Codec = null, KeyFunctions = null, IsFindingEarbuds = false });
        if (Model.Gaia && Model.Variant is not ("tw600" or "tw700"))
        {
            // Legacy QCC initialization/subscriptions, exactly as EarKit. TW600/TW700 must skip these.
            byte[][] bodies = [[0,10,3,0], [0,0x1D,0,1], [0,0x1D,0,0x0D,7,0,0,0,4],
                [0,0x1D,0,7,0x0D], [0,0x1D,0,7,5], [0,0x1D,0,7,8], [0,0x1D,0,7,1],
                [0,0x1D,0,7,6], [0,0x1D,0,7,7], [0,0x1D,0,7,7]];
            foreach (var body in bodies)
            {
                if (!await WriteAsync(new byte[] { 0xFF,4,0,(byte)(body.Length - 4) }.Concat(body).ToArray(), cancellationToken).ConfigureAwait(false)) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(40), Context.Time, cancellationToken).ConfigureAwait(false);
            }
        }
        if (await Read(EarFunSetting.Firmware, cancellationToken).ConfigureAwait(false) is null
            || await Read(EarFunSetting.Battery, cancellationToken).ConfigureAwait(false) is null) return false;
        // Optional discovery is not a transaction: unsupported features may stay silent. Only valid
        // reports open controls. Subsequent transactions never reuse a timed-out session.
        foreach (var setting in Settings().Where(s => s is not (EarFunSetting.Firmware or EarFunSetting.Battery)))
        {
            if (!await WriteAsync(EarFunFrameCodec.Encode(Model.Gaia, EarFunSettings.Command(Model, setting).Read, []), cancellationToken).ConfigureAwait(false)) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(40), Context.Time, cancellationToken).ConfigureAwait(false);
        }
        if (Model.Has("keys") && !await WriteAsync(EarFunFrameCodec.Encode(Model.Gaia, EarFunGestures.Read(Model), []), cancellationToken).ConfigureAwait(false)) return false;
        StartPolling();
        return true;
    }

    private bool Apply(BrandPacket packet, StateSource source)
    {
        if (packet.Command == EarFunGestures.Read(Model) && Model.Has("keys"))
        {
            if (EarFunGestures.Decode(Model, packet.Payload) is not { } keys) return false;
            Store.TryUpdate(DeviceId, s => s with { KeyFunctions = Observed.From(keys, Now, source) });
            return true;
        }
        var pair = Settings().FirstOrDefault(s => EarFunSettings.Command(Model, s).Read == packet.Command);
        if (EarFunSettings.Command(Model, pair).Read != packet.Command) return false;
        byte[] p = packet.Payload;
        if (pair == EarFunSetting.Firmware)
        {
            if (p.Length is < 1 or > 128) return false;
            string version;
            if (!Model.Gaia && Model.Variant is not ("tw230" or "cw100" or "cw200" or "head") && p.Length == 3 && p.Any(c => c < 32)) version = $"{p[0]}.{p[1]}.{p[2]}";
            else
            {
                if (p.Any(c => c != 0 && (c < 32 || c > 126))) return false;
                version = Encoding.ASCII.GetString(p).TrimEnd('\0');
                if (version.Length == 0) return false;
            }
            Store.TryUpdate(DeviceId, s => s with { Firmware = Observed.From(new FirmwareInfo(version, Model.Id, new Dictionary<string, string>()), Now, source) });
            return true;
        }
        if (pair == EarFunSetting.Battery && !Model.Gaia)
        {
            if (p.Length != (Model.Headphones ? 1 : 2) || p.Any(v => v > 100 && v != 255)) return false;
            Battery(BatteryComponent.Left, p[0], null, source);
            if (!Model.Headphones) Battery(BatteryComponent.Right, p[1], null, source);
            return true;
        }
        if (pair == EarFunSetting.Find && !Model.Gaia)
        {
            if (p.Length != 2 || p.Any(v => v > 1)) return false;
            Store.TryUpdate(DeviceId, s => s with { IsFindingEarbuds = p[0] != 0 || p[1] != 0 });
            return true;
        }
        if (p.Length != 1) return false;
        byte value = p[0];
        switch (pair)
        {
            case EarFunSetting.Battery or EarFunSetting.RightBattery or EarFunSetting.Case:
                bool packed = pair == EarFunSetting.Case && Model.Tw700;
                int level = packed && value != 255 ? value & 127 : value;
                if (level > 100 && level != 255) return false;
                Battery(pair == EarFunSetting.Battery ? BatteryComponent.Left : pair == EarFunSetting.RightBattery ? BatteryComponent.Right : BatteryComponent.Case,
                    (byte)level, packed && value != 255 ? (value & 128) != 0 : null, source);
                return true;
            case EarFunSetting.Charging:
                if ((value & ~10) != 0) return false;
                Charge(BatteryComponent.Left, (value & 2) != 0, source); Charge(BatteryComponent.Right, (value & 8) != 0, source); return true;
            case EarFunSetting.CaseCharging:
                if (value > 1) return false;
                Charge(BatteryComponent.Case, value != 0, source); return true;
            case EarFunSetting.Noise:
                var noise = EarFunSettings.NoiseModes(Model).FirstOrDefault(n => n.Value == value);
                if (noise.State is null) return false;
                Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(noise.State, Now, source) }); return true;
            case EarFunSetting.Game or EarFunSetting.Dual or EarFunSetting.Wear or EarFunSetting.Spatial:
                bool enabled;
                if (pair == EarFunSetting.Wear)
                {
                    if (Model.Tw700 ? value > 15 : value > 1) return false;
                    enabled = Model.Tw700 ? (value & 15) != 15 : value != 1;
                }
                else { if (Model.Gaia ? value > 1 : value is not (1 or 2)) return false; enabled = value == (Model.Gaia ? 1 : 2); }
                if (pair == EarFunSetting.Spatial) Store.TryUpdate(DeviceId, s => s with { Spatial = Observed.From(enabled ? SpatialAudioMode.Fixed : SpatialAudioMode.Off, Now, source) });
                else Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(pair == EarFunSetting.Game ? DeviceToggle.GamingMode : pair == EarFunSetting.Dual ? DeviceToggle.DualDeviceConnection : DeviceToggle.InEarDetection, Observed.From(enabled, Now, source)) });
                return true;
            case EarFunSetting.Prompt:
                if (Model.Gaia ? value > 4 : value is < 1 or > 5) return false;
                Store.TryUpdate(DeviceId, s => s with { PromptVolume = Observed.From(Model.Gaia ? 100 - 25 * value : 25 * (value - 1), Now, source) }); return true;
            case EarFunSetting.Find:
                if (value > 3) return false;
                Store.TryUpdate(DeviceId, s => s with { IsFindingEarbuds = value != 0 }); return true;
            case EarFunSetting.Sub or EarFunSetting.Ambient or EarFunSetting.AutoOff:
                if (EarFunSettings.Option(Model, pair, value) is not { } option) return false;
                Store.TryUpdate(DeviceId, s => s with { Options = s.Options.SetItem(option.Id, Observed.From(option, Now, source)) }); return true;
            case EarFunSetting.Codec:
                // Core has no aptX Lossless enum yet. Clear an older codec instead of keeping it as the current report.
                if (value == 6) { Store.TryUpdate(DeviceId, s => s with { Codec = null }); return true; }
                AudioCodec? codec = value switch { 1 => AudioCodec.Sbc, 2 => AudioCodec.Aac, 3 => AudioCodec.AptX, 4 => AudioCodec.AptXHd, 5 => AudioCodec.AptXAdaptive, 7 => AudioCodec.Ldac, _ => null };
                if (codec is null) return false;
                Store.TryUpdate(DeviceId, s => s with { Codec = Observed.From(codec.Value, Now, source) }); return true;
            default: return false;
        }
    }

    private void Battery(BatteryComponent component, byte value, bool? charging, StateSource source) => Store.TryUpdate(DeviceId, s => s with
    {
        Battery = s.Battery.With(component, value == 255 ? null : Observed.From(new BatteryReading(value, charging), Now, source)),
        LastUpdateSource = source,
    });
    private void Charge(BatteryComponent component, bool charging, StateSource source) => Store.TryUpdate(DeviceId, s => s with
    { Battery = s.Battery.With(component, Observed.From(new BatteryReading(s.Battery.Get(component)?.Value.Level, charging), Now, source)) });

    private async Task<BrandPacket?> Request(ushort command, byte[] payload, CancellationToken ct)
    {
        try
        {
            var reply = await ExchangeAsync(EarFunFrameCodec.Encode(Model.Gaia, command, payload), p => p.Command == command, ct).ConfigureAwait(false);
            if (reply is null) EndSession(); // No sequence ids: a late reply must never confirm another transaction.
            return reply;
        }
        catch (OperationCanceledException) { EndSession(); throw; }
    }
    private async Task<byte[]?> Read(EarFunSetting setting, CancellationToken ct)
    {
        var reply = await Request(EarFunSettings.Command(Model, setting).Read, [], ct).ConfigureAwait(false);
        return reply is { Type: 0 } && Apply(reply, StateSource.QueryResponse) ? reply.Payload : null;
    }
    private Task<CommandResult> Serialized(Func<CancellationToken, Task<CommandResult>> action, CancellationToken ct) => Run(action, ct);
    private async Task<CommandResult> Run(Func<CancellationToken, Task<CommandResult>> action, CancellationToken ct)
    {
        bool entered = false;
        try
        {
            await Operations.WaitAsync(ct).ConfigureAwait(false); entered = true;
            if (Closed.IsCompleted || Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
            return await action(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(CommandStatus.Cancelled); }
        finally { if (entered) Operations.Release(); }
    }

    private Task<CommandResult> Change(EarFunSetting setting, byte[] payload, Func<byte[], bool> matches, CancellationToken ct, bool stopping = false) => Serialized(async token =>
    {
        var commands = EarFunSettings.Command(Model, setting);
        if (commands.Write == 0 || (!stopping && (!Context.AllowExperimental() || !_profile.Confirmed(commands.Read)))) return NotSupported("Unconfirmed or experimental EarFun setting");
        if (await Request(commands.Write, payload, token).ConfigureAwait(false) is not { } ack) return TimedOut;
        if (ack.Type != 0 || (!Model.Gaia && ack.Payload.Length != 0 && !ack.Payload.SequenceEqual(new byte[] { 0 })))
            return new(CommandStatus.Rejected, "EarFun rejected the setting.");
        var readback = await Read(setting, token).ConfigureAwait(false);
        return readback is null ? TimedOut : matches(readback) ? CommandResult.Ok : new(CommandStatus.Rejected, "EarFun did not confirm the requested value.");
    }, ct);

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => Serialized(async ct =>
    {
        foreach (var setting in Settings().Where(s => s is EarFunSetting.Firmware or EarFunSetting.Battery || _profile.Confirmed(EarFunSettings.Command(Model, s).Read)))
            if (await Read(setting, ct).ConfigureAwait(false) is null)
                return Closed.IsCompleted ? TimedOut : new(CommandStatus.Rejected, "EarFun returned an invalid or rejected refresh response.");
        return CommandResult.Ok;
    }, cancellationToken);

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        var option = EarFunSettings.NoiseModes(Model).FirstOrDefault(p => p.State == target);
        return option.State is null ? Task.FromResult(NotSupported("Noise mode")) : Change(EarFunSetting.Noise, [option.Value], p => p.SequenceEqual(new[] { option.Value }), cancellationToken);
    }
    public override Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        EarFunSetting? setting = toggle switch { DeviceToggle.GamingMode => EarFunSetting.Game, DeviceToggle.DualDeviceConnection => EarFunSetting.Dual, DeviceToggle.InEarDetection => EarFunSetting.Wear, _ => null };
        if (setting is null) return Task.FromResult(NotSupported("Toggle"));
        byte value = setting == EarFunSetting.Wear ? (byte)(enabled ? 0 : Model.Tw700 ? 15 : 1) : (byte)(enabled ? Model.Gaia ? 1 : 2 : Model.Gaia ? 0 : 1);
        return Change(setting.Value, [value], p => setting == EarFunSetting.Wear && Model.Tw700 ? p.Length == 1 && ((p[0] & 15) != 15) == enabled : p.SequenceEqual(new[] { value }), cancellationToken);
    }
    public override Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default) =>
        mode is not (SpatialAudioMode.Off or SpatialAudioMode.Fixed) ? Task.FromResult(NotSupported("Head tracking")) : Change(EarFunSetting.Spatial, [mode == SpatialAudioMode.Off ? (byte)1 : (byte)2], p => p.SequenceEqual(new byte[] { mode == SpatialAudioMode.Off ? (byte)1 : (byte)2 }), cancellationToken);
    public override Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default)
    {
        if (level is < 0 or > 100 || level % 25 != 0) return Task.FromResult(NotSupported("Prompt volume"));
        byte value = (byte)(Model.Gaia ? (100 - level) / 25 : 1 + level / 25);
        return Change(EarFunSetting.Prompt, [value], p => p.SequenceEqual(new[] { value }), cancellationToken);
    }
    public override Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default)
    {
        if (!Model.Has("find")) return Task.FromResult(NotSupported("Find earbuds"));
        byte[] value = Model.Gaia ? [ring ? (byte)3 : (byte)0] : [ring ? (byte)1 : (byte)0, ring ? (byte)1 : (byte)0];
        return Change(EarFunSetting.Find, value, p => p.SequenceEqual(value), cancellationToken, stopping: !ring);
    }
    public override Task<CommandResult> SetOptionAsync(string id, int value, CancellationToken cancellationToken = default)
    {
        if (!Store.Current.Options.TryGetValue(id, out var option) || !option.Value.Accepts(value)) return Task.FromResult(NotSupported("Option value"));
        var setting = new[] { EarFunSetting.Sub, EarFunSetting.Ambient, EarFunSetting.AutoOff }.Cast<EarFunSetting?>().FirstOrDefault(s => id == "earfun-" + s!.Value.ToString().ToLowerInvariant());
        return setting is null ? Task.FromResult(NotSupported("Option")) : Change(setting.Value, [(byte)value], p => p.SequenceEqual(new[] { (byte)value }), cancellationToken);
    }

    public override Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default) => Serialized(async ct =>
    {
        if (!Context.AllowExperimental() || !_profile.Confirmed(EarFunGestures.Read(Model))) return NotSupported("Unconfirmed or experimental gestures");
        var slots = EarFunGestures.Slots(Model);
        int index = Enumerable.Range(0, slots.Count).FirstOrDefault(i => slots[i].Matches(assignment), -1);
        if (index < 0 || slots[index].Choices.FirstOrDefault(c => c.Function == assignment.Function) is not { } choice
            || (choice.Requires is { } capability && !_profile.Capabilities.IsAvailable(capability, Context.AllowExperimental()))) return NotSupported("Gesture assignment");
        var current = await Request(EarFunGestures.Read(Model), [], ct).ConfigureAwait(false);
        if (current is not { Type: 0 } || EarFunGestures.Decode(Model, current.Payload) is not { } old) return TimedOut;
        byte[] payload;
        if (Model.Gaia)
        {
            byte type = assignment.Action == GestureAction.SqueezeAndHold ? (byte)17 : (byte)assignment.Action;
            payload = assignment.Side == EarSide.Left ? [type, (byte)assignment.Function, 0, 0] : [0, 0, type, (byte)assignment.Function];
        }
        else { payload = current.Payload.ToArray(); payload[index] = (byte)assignment.Function; }
        var ack = await Request(EarFunGestures.Write(Model), payload, ct).ConfigureAwait(false);
        if (ack is null) return TimedOut;
        if (ack.Type != 0 || (!Model.Gaia && ack.Payload.Length != 0 && !ack.Payload.SequenceEqual(new byte[] { 0 }))) return new(CommandStatus.Rejected);
        var reply = await Request(EarFunGestures.Read(Model), [], ct).ConfigureAwait(false);
        if (reply is not { Type: 0 } || EarFunGestures.Decode(Model, reply.Payload) is not { } keys) return TimedOut;
        return keys.Select((key, i) => key == (i == index ? assignment : old[i])).All(v => v) ? CommandResult.Ok
            : new(CommandStatus.Rejected, "EarFun did not confirm the gesture assignment.");
    }, cancellationToken);
}
