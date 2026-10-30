// SPDX-License-Identifier: GPL-3.0-only
// Facts only (no code copied): battery 0x0CD6, charging 0x0009, case battery 0x000B and the
// secondary-ear "status 02" acknowledgement were observed on a real AKG N9 Hybrid by kalicyh/Raito
// (5804b65). Noise control (see AkgRace) follows Airoha's RACE specification and the Harman/JBL
// Airoha notes of HellzYeahh/earshot and gist wielkiborsuk/f46f6792…; it is unverified on AKG, so it
// is experimental opt-in only, read before every write and read back after it.
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Akg;

internal sealed class AkgHybridController(IDuplexByteChannel channel, ControllerContext context, bool earbuds)
    : RequestReplyController(channel, context)
{
    private const ushort BatteryLevel = 0x0CD6, Charging = 0x0009, CaseBattery = 0x000B, AgentChannel = 0x0CD4;
    private static readonly HashSet<ushort> TracedCommands = [BatteryLevel, Charging, CaseBattery, AgentChannel, AkgRace.MmiGetEnum, AkgRace.Anc, AkgRace.NvKeyRead];
    private static readonly TimeSpan ReadbackRetryDelay = TimeSpan.FromMilliseconds(700);
    private readonly RaceFrameCodec _codec = new();
    private bool? _agentRight;
    private readonly bool?[] _charging = new bool?[2];
    private AirohaAncLayout _layout;
    private bool _chipQueried;
    protected override IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes) => _codec.Feed(bytes);
    protected override async Task<bool> StartSessionAsync(CancellationToken ct)
    {
        bool ready = (await RefreshAsync(ct).ConfigureAwait(false)).IsSuccess;
        if (ready) StartPolling();
        return ready;
    }

    private Task<BrandPacket?> Send(ushort command, byte[] payload, Func<BrandPacket, bool> match, CancellationToken ct)
    {
        Trace(FrameDirection.Sent, command, 0x5A, payload);
        return ExchangeAsync(RaceFrameCodec.Encode(command, payload), match, ct);
    }

    /// <summary>
    /// Waits for the typed reply, or for an acknowledgement (type 0x5B) with a non-zero status, which
    /// is how a role without data answers (an N9 Hybrid answers the partner battery query with status 02).
    /// </summary>
    private Task<BrandPacket?> Query(ushort command, byte[] payload, byte type, Func<byte[], bool> valid, CancellationToken ct) =>
        Send(command, payload, p => p.Command == command
            && ((p.Type == type && valid(p.Payload)) || (p.Type == 0x5B && type != 0x5B && p.Payload is [> 0])), ct);

    private static bool Answered(BrandPacket? packet, byte type) => packet is not null && packet.Type == type;

    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            _agentRight = null; // Agent/partner may swap whenever a bud is returned to the case.
            if (earbuds) await Query(AgentChannel, [], 0x5B, p => p is [0, 0 or 1], cancellationToken).ConfigureAwait(false);
            bool battery = false;
            foreach (byte role in earbuds ? new byte[] { 0, 1 } : new byte[] { 0 })
            {
                // Charging first (the order Raito observed); the battery reading then carries the flag.
                await Query(Charging, [role], 0x5D, p => p.Length == 3 && p[0] == 0 && p[1] == role && p[2] <= 1, cancellationToken).ConfigureAwait(false);
                battery |= Answered(await Query(BatteryLevel, [role], 0x5D, p => p.Length == 3 && p[0] == 0 && p[1] == role && p[2] <= 100, cancellationToken).ConfigureAwait(false), 0x5D);
            }
            if (earbuds) await Query(CaseBattery, [], 0x5D, p => p.Length == 2 && p[0] == 0 && p[1] is > 0 and <= 100, cancellationToken).ConfigureAwait(false);
            await ProbeAncAsync(cancellationToken).ConfigureAwait(false);
            return battery && (!earbuds || _agentRight.HasValue) ? CommandResult.Ok : TimedOut;
        }
        finally { Operations.Release(); }
    }

    /// <summary>
    /// Read-only discovery: the chip family once (it fixes the pass-through numbering), then the ANC
    /// status. The profile states the outcome, so a failed probe is explained instead of leaving the
    /// noise controls silently missing.
    /// </summary>
    private async Task ProbeAncAsync(CancellationToken ct)
    {
        if (!Context.AllowExperimental())
        {
            Publish(new AkgAncProbe(AkgAncStatus.NeedsOptIn), clearNoise: true);
            return;
        }
        if (!_chipQueried)
        {
            _chipQueried = true;
            var chip = await Send(AkgRace.NvKeyRead, AkgRace.ChipNameRequest,p => p.Command == AkgRace.NvKeyRead && p.Type == 0x5B, ct).ConfigureAwait(false);
            if (chip is not null && AkgRace.TryParseChipName(chip.Payload, out string name)) _layout = AkgRace.Classify(name);
        }
        var reply = await ReadAncAsync(ct).ConfigureAwait(false);
        if (reply is null)
        {
            Publish(new AkgAncProbe(AkgAncStatus.Failed, Detail: "Noise control unavailable: no reply to the ANC status read (Airoha RACE 0x0901 module 5) within 3 s."), clearNoise: true);
            return;
        }
        AirohaAncLayout layout = _layout;
        if (!AkgRace.TryParseAncStatus(reply.Payload, out byte filter) || AkgRace.ModeOf(filter, ref layout) is not { } mode)
        {
            string why = reply.Payload is [_, _, not 0, ..]
                ? $"answered the ANC status read with status 0x{reply.Payload[2]:X2}"
                : $"sent an unrecognized ANC status {Convert.ToHexString(reply.Payload)}";
            Publish(new AkgAncProbe(AkgAncStatus.Failed, Detail: $"Noise control unavailable: the headset {why} (Airoha RACE 0x0901 module 5)."), clearNoise: true);
            return;
        }
        _layout = layout;
        bool ambient = _layout != AirohaAncLayout.Unknown;
        string detail = $"Experimental: ANC status read answered (RACE 0x0901 module 5, filter {filter}); changes use RACE 0x0E06 and are read back. "
            + (ambient ? $"Ambient uses the {_layout} pass-through filter." : "Ambient stays hidden until the chip's pass-through numbering is known.");
        var probe = new AkgAncProbe(AkgAncStatus.Ready, ambient, detail);
        Store.TryUpdate(DeviceId, s => s with
        {
            Profile = Same(s.Profile, probe) ? s.Profile : new AkgHybridProfile(earbuds, probe),
            NoiseControl = Observed.From(new NoiseControlState(mode), Now, StateSource.QueryResponse),
        });
    }

    private Task<BrandPacket?> ReadAncAsync(CancellationToken ct) =>
        Send(AkgRace.MmiGetEnum, AkgRace.AncStatusRequest, p => p.Command == AkgRace.MmiGetEnum && p.Type == 0x5B && AkgRace.IsAncStatusReply(p.Payload), ct);

    private void Publish(AkgAncProbe probe, bool clearNoise) => Store.TryUpdate(DeviceId, s => s with
    {
        Profile = Same(s.Profile, probe) ? s.Profile : new AkgHybridProfile(earbuds, probe),
        NoiseControl = clearNoise ? null : s.NoiseControl,
    });

    private static bool Same(Core.Profiles.IDeviceProfile? profile, AkgAncProbe probe) => profile is AkgHybridProfile { Anc: var current } && current == probe;

    protected override void HandlePacket(BrandPacket packet)
    {
        Trace(FrameDirection.Received, packet.Command, packet.Type, packet.Payload);
        var p = packet.Payload;
        if (packet.Type == 0x5B && packet.Command == AgentChannel && p is [0, 0 or 1]) _agentRight = p[1] == 1;
        else if (packet.Type == 0x5D && packet.Command == Charging && p is [0, <= 1, <= 1]) _charging[p[1]] = p[2] == 1;
        else if (packet.Type == 0x5D && packet.Command == BatteryLevel && p is [0, <= 1, <= 100])
        {
            // Never label agent/partner as left/right without a confirmed channel response.
            if (earbuds && !_agentRight.HasValue) return;
            var component = !earbuds || (p[1] == 0 ? !_agentRight!.Value : _agentRight!.Value) ? BatteryComponent.Left : BatteryComponent.Right;
            Battery(component, p[2], _charging[p[1]]);
        }
        else if (packet.Type == 0x5D && packet.Command == CaseBattery && p is [0, > 0 and <= 100]) Battery(BatteryComponent.Case, p[1], null);
        else if (packet.Command is AkgRace.MmiGetEnum or AkgRace.Anc or AkgRace.NvKeyRead) { }
        else return;
        Touch();
    }

    private void Battery(BatteryComponent component, int value, bool? charging) => Store.TryUpdate(DeviceId, s => s with
    { Battery = s.Battery.With(component, Observed.From(new BatteryReading(value, charging), Now, StateSource.QueryResponse)) });

    /// <summary>Known commands with their payload; anything else only by length.</summary>
    private void Trace(FrameDirection direction, ushort command, byte type, byte[] payload)
    {
        if (Context.Trace is not { } trace) return;
        string body = TracedCommands.Contains(command) ? Convert.ToHexString(payload) : $"<{payload.Length} bytes>";
        trace.OnBrandFrame(direction, $"AKG RACE {type:X2} {command:X4} {body}", Now);
    }

    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (target.LevelId is not null || target.Mode is not (NoiseControlMode.Off or NoiseControlMode.NoiseCancellation or NoiseControlMode.Transparency))
            return CommandResult.Unsupported("Unsupported ANC mode.");
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            if (!Context.AllowExperimental()) return CommandResult.Unsupported("AKG Hybrid noise control is experimental; enable experimental features first.");
            // Read before every write: the session must still answer, and the read may reveal the layout.
            await ProbeAncAsync(cancellationToken).ConfigureAwait(false);
            if (Store.Current.Profile is not AkgHybridProfile { Anc.Status: AkgAncStatus.Ready }) return CommandResult.Unsupported("The headset did not answer the ANC status read.");
            if (AkgRace.AncPayload(target.Mode, _layout) is not { } payload) return CommandResult.Unsupported("Ambient sound needs the chip's pass-through numbering, which is not known yet.");
            if (Store.Current.NoiseControl?.Value.Mode == target.Mode) return CommandResult.Ok;
            var ack = await Send(AkgRace.Anc, payload, p => p.Command == AkgRace.Anc && p.Type == 0x5B && AkgRace.IsAncReply(payload, p.Payload), cancellationToken).ConfigureAwait(false);
            if (ack is not null && ack.Payload[0] != 0) return new(CommandStatus.Rejected, $"Device rejected the ANC change (status 0x{ack.Payload[0]:X2}).");
            // Without an acknowledgement only the readback decides; the filter can take a moment to switch.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) await Task.Delay(ReadbackRetryDelay, Context.Time, cancellationToken).ConfigureAwait(false);
                await ProbeAncAsync(cancellationToken).ConfigureAwait(false);
                if (Store.Current.Profile is not AkgHybridProfile { Anc.Status: AkgAncStatus.Ready }) return TimedOut;
                if (Store.Current.NoiseControl?.Value.Mode == target.Mode) return CommandResult.Ok;
            }
            return new(CommandStatus.Rejected, "ANC readback did not match.");
        }
        finally { Operations.Release(); }
    }
}
