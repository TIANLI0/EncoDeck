// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Huawei;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Honor;

/// <summary>
/// HONOR MBB commands from the official plugin, plus the HONOR CHOICE variant from community notes.
/// Only the frame codec and the result-code convention are shared with HUAWEI.
/// </summary>
internal sealed class HonorDeviceController(IDuplexByteChannel channel, ControllerContext context, HonorDeviceProfile profile)
    : RequestReplyController(channel, context)
{
    internal const ushort Battery = 0x0108, BatteryNotify = 0x0127;
    internal const ushort AncGet = 0x2B2A, AncSet = 0x2B04;
    internal const ushort WearGet = 0x2B11, WearSet = 0x2B10;
    // The HONOR plugin's GET/SET ordering differs from HUAWEI.
    internal const ushort DualGet = 0x2B2E, DualSet = 0x2B2F;
    internal const ushort EqGet = 0x2B6D, EqSet = 0x2B6E, EqNotify = 0x2B6F;
    private readonly MbbFrameCodec _codec = new();
    private HonorDeviceProfile _profile = profile;
    private static readonly byte[] EmptyQuery = [1, 0];

    protected override IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes) =>
        [.. _codec.Feed(bytes).Select(f => new BrandPacket(f.Command, 0, f.Payload))];

    protected override void HandlePacket(BrandPacket packet)
    {
        if (!HuaweiWire.TryParseTlv(packet.Payload, out var fields) || fields.ContainsKey(127)) return;
        switch (packet.Command)
        {
            case Battery or BatteryNotify:
                if (!ValidBattery(fields)) return;
                Store.TryUpdate(DeviceId, state =>
                {
                    var battery = state.Battery;
                    byte[] levels = fields.TryGetValue(2, out var parts) ? parts : fields[1];
                    fields.TryGetValue(3, out var charging);
                    BatteryComponent[] components = [BatteryComponent.Left, BatteryComponent.Right, BatteryComponent.Case];
                    for (int i = 0; i < Math.Min(3, levels.Length); i++)
                    {
                        if (levels[i] > 100) continue;
                        bool? charged = charging is not null && charging.Length > i && charging[i] <= 1 ? charging[i] == 1 : null;
                        battery = battery.With(components[i], Observed.From(new BatteryReading(levels[i], charged), Now, StateSource.DeviceNotification));
                    }
                    return state with { Battery = battery, LastUpdateSource = StateSource.DeviceNotification };
                });
                break;
            case AncGet when _profile.Model.Noise:
                if (ParseNoise(fields) is { } noise)
                    Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(noise, Now, StateSource.DeviceNotification) });
                break;
            case WearGet when _profile.Model.Wear:
                ApplyToggle(DeviceToggle.InEarDetection, fields);
                break;
            case DualGet when _profile.Model.Dual:
                ApplyToggle(DeviceToggle.DualDeviceConnection, fields);
                break;
            case EqGet or EqNotify when _profile.Model.Eq:
                if (!fields.TryGetValue(1, out var current) || current.Length != 1) return;
                if (fields.TryGetValue(2, out var list) && list.Length > 0)
                {
                    _profile = _profile.WithEq(list);
                    Store.TryUpdate(DeviceId, s => s with { Profile = _profile });
                }
                if (_profile.EqualizerPresets.Any(p => p.Id == current[0].ToString()))
                    Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState(current[0].ToString(), current[0]), Now, StateSource.DeviceNotification) });
                break;
        }
    }

    private static bool ValidBattery(Dictionary<byte, byte[]> fields) => fields.TryGetValue(2, out var levels)
        ? levels.Length is >= 1 and <= 3 && levels.Any(v => v <= 100)
        : fields.TryGetValue(1, out var overall) && overall.Length == 1 && overall[0] <= 100;

    private void ApplyToggle(DeviceToggle toggle, Dictionary<byte, byte[]> fields)
    {
        if (fields.TryGetValue(1, out var v) && v.Length == 1 && v[0] <= 1)
            Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(toggle, Observed.From(v[0] == 1, Now, StateSource.DeviceNotification)) });
    }

    private NoiseControlState? ParseNoise(Dictionary<byte, byte[]> fields)
    {
        // HONOR CHOICE reports the mode in the last byte of TLV 1 (one byte, or [sub, mode]); no levels.
        if (_profile.Model.Choice)
        {
            return fields.TryGetValue(1, out var c) && c.Length is 1 or 2
                ? c[^1] switch { 0 => new(NoiseControlMode.Off), 1 => new(NoiseControlMode.NoiseCancellation), 2 => new(NoiseControlMode.Transparency), _ => null }
                : null;
        }
        if (!fields.TryGetValue(1, out var v) || v.Length != 2) return null;
        string? level = _profile.Model.AncLevels ? v[0] switch { 0 => "cozy", 1 => "general", 2 => "ultra", 3 => "dynamic", _ => null } : null;
        return v[1] switch
        {
            0 => new(NoiseControlMode.Off),
            1 when !_profile.Model.AncLevels || level is not null => new(NoiseControlMode.NoiseCancellation, level),
            2 => new(NoiseControlMode.Transparency),
            _ => null,
        };
    }

    private Task<BrandPacket?> Query(ushort command, CancellationToken ct) =>
        ExchangeAsync(_codec.Encode(command, _profile.Model.Choice && command is Battery or AncGet ? []
            : command == Battery ? new byte[] { 1, 0, 2, 0, 3, 0 }
            : command == EqGet ? new byte[] { 1, 0, 1, 0 } : EmptyQuery), p => p.Command == command && !IsAck(p), ct);

    /// <summary>Write acknowledgement <c>7F 04 00 01 86 A0</c> (result 100000), as on HUAWEI firmware.</summary>
    private static bool IsAck(BrandPacket packet) =>
        HuaweiWire.TryParseTlv(packet.Payload, out var fields) && HuaweiWire.IsSuccessAck(fields);

    private static bool Fields(BrandPacket? packet, out Dictionary<byte, byte[]> fields)
    {
        fields = [];
        return packet is not null && HuaweiWire.TryParseTlv(packet.Payload, out fields) && !fields.ContainsKey(127);
    }

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        if (!_profile.Model.Mbb) return false;
        var packet = await Query(Battery, cancellationToken).ConfigureAwait(false);
        if (!Fields(packet, out var fields) || !ValidBattery(fields)) return false;
        await RefreshSettings(cancellationToken).ConfigureAwait(false);
        StartPolling();
        return true;
    }

    private async Task RefreshSettings(CancellationToken ct)
    {
        if (_profile.Model.Noise) await Query(AncGet, ct).ConfigureAwait(false);
        if (_profile.Model.Wear) await Query(WearGet, ct).ConfigureAwait(false);
        if (_profile.Model.Dual) await Query(DualGet, ct).ConfigureAwait(false);
        if (_profile.Model.Eq) await Query(EqGet, ct).ConfigureAwait(false);
    }

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => RunExclusiveAsync(Operations, async ct =>
    {
        var response = await Query(Battery, ct).ConfigureAwait(false);
        if (!Fields(response, out var fields) || !ValidBattery(fields)) return Failure(response);
        await RefreshSettings(ct).ConfigureAwait(false);
        return CommandResult.Ok;
    }, cancellationToken);

    private static CommandResult Failure(BrandPacket? response) => response is null ? TimedOut :
        new(CommandStatus.Rejected, "HONOR returned an error or malformed control response.");

    protected override bool CanRunCommands => base.CanRunCommands && Store.Current.Device?.DeviceId == DeviceId;

    private async Task<CommandResult> Confirm(ushort set, byte[] value, ushort get,
        Func<Dictionary<byte, byte[]>, bool> matches, CancellationToken ct)
    {
        // Do not publish an optimistic setting. Only a fresh, matching read-back confirms the write.
        // The plugin acknowledges with TLV 1 = 0; HUAWEI-derived firmware with result code 100000.
        var ack = await ExchangeAsync(_codec.Encode(set, value), p => p.Command == set, ct).ConfigureAwait(false);
        if (ack is not null && !IsAck(ack) && (!Fields(ack, out var ackFields)
            || ackFields.TryGetValue(1, out var result) && result.Any(b => b != 0))) return Failure(ack);
        var response = await Query(get, ct).ConfigureAwait(false);
        if (Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
        if (!Fields(response, out var fields)) return Failure(response);
        return matches(fields) ? CommandResult.Ok : new(CommandStatus.Rejected, "The earphones did not confirm the requested setting.");
    }

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        // A mode without a level (the mode button) is allowed whenever the mode itself is offered.
        if (!_profile.NoiseControlOptions.Any(o => o.Matches(target) || (target.LevelId is null && o.Mode == target.Mode)))
            return Task.FromResult(NotSupported("Noise control"));
        byte mode = target.Mode switch { NoiseControlMode.Off => 0, NoiseControlMode.NoiseCancellation => 1, _ => 2 };
        // 0xFF keeps the current level, as on HUAWEI firmware (OpenFreebuds' FreeBuds 4i profile drives Earbuds 2 SE this way).
        byte sub = target.Mode == NoiseControlMode.Off ? (byte)0
            : (byte)(target.LevelId switch { "cozy" => 0, "general" => 1, "ultra" => 2, "dynamic" => 3, _ => 0xFF });
        byte[] write = _profile.Model.Choice ? [1, 1, mode] : [1, 2, mode, sub];
        return RunCommandAsync(PendingKeys.NoiseControl, Operations, ct => Confirm(AncSet, write, AncGet,
            f => ParseNoise(f) is { } n && n.Mode == target.Mode && (target.LevelId is null || n.LevelId == target.LevelId), ct), cancellationToken);
    }

    public override Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        if (!_profile.Toggles.Contains(toggle)) return Task.FromResult(NotSupported(toggle.ToString()));
        ushort set = toggle == DeviceToggle.InEarDetection ? WearSet : DualSet;
        ushort get = toggle == DeviceToggle.InEarDetection ? WearGet : DualGet;
        return RunCommandAsync(PendingKeys.Toggle(toggle), Operations, ct => Confirm(set, [1, 1, enabled ? (byte)1 : (byte)0], get,
            f => f.TryGetValue(1, out var v) && v.Length == 1 && v[0] == (enabled ? 1 : 0), ct), cancellationToken);
    }

    public override Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        if (!_profile.EqualizerPresets.Any(p => p.Id == presetId) || !byte.TryParse(presetId, out byte value))
            return Task.FromResult(NotSupported("EQ preset has not been advertised by this device."));
        return RunCommandAsync(PendingKeys.Equalizer, Operations, ct => Confirm(EqSet, [1, 1, value], EqGet,
            f => f.TryGetValue(1, out var v) && v is [var actual] && actual == value, ct), cancellationToken);
    }
}
