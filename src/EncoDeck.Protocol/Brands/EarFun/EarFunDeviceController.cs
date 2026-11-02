// SPDX-License-Identifier: GPL-3.0-only
// EarKit a77acd18890a5f28746c2303bfff0e23fff8d8ca (MIT), adapted to confirmed state and serialized transactions.
using System.Collections.Concurrent;
using System.Text;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.EarFun;

internal sealed class EarFunDeviceController(IDuplexByteChannel channel, ControllerContext context, EarFunProfile profile)
    : RequestReplyController(channel, context)
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(1);
    // EarKit gives switching earbuds up to 6 s before it believes a differing report.
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ReadbackRetryDelay = TimeSpan.FromMilliseconds(500);
    // The list answers as one frame per paired host, without a count or terminator.
    private static readonly TimeSpan SourceListWindow = TimeSpan.FromMilliseconds(1200);
    // EarKit re-reads the list for about 11 s after a connect or disconnect and repeats the command at 3 s and 6 s.
    private static readonly TimeSpan HostSwitchWindow = TimeSpan.FromSeconds(12);
    // EarKit's spacing between queued frames.
    private static readonly TimeSpan FrameGap = TimeSpan.FromMilliseconds(40);
    private readonly EarFunFrameCodec _codec = new(profile.Model.Gaia);
    private readonly ConcurrentDictionary<ushort, byte[]> _values = new();
    // Raw protocol log: values of the settings EncoDeck reads and writes; only the length of anything else
    // (the paired-device list carries host names).
    private readonly HashSet<ushort> _traced = [.. Enum.GetValues<EarFunSetting>()
        .SelectMany(s => new[] { EarFunSettings.Command(profile.Model, s).Read, EarFunSettings.Command(profile.Model, s).Write })
        .Where(c => c != 0), EarFunGestures.Read(profile.Model), EarFunGestures.Write(profile.Model),
        EarFunSettings.ConnectHost(profile.Model), EarFunSettings.DisconnectHost(profile.Model),
        EarFunSettings.Reset(profile.Model), profile.Model.Gaia ? EarFunEqualizer.GaiaCommand : EarFunEqualizer.EfCommand];
    // Reports of settings and gestures: logged once per session when EncoDeck cannot read the value.
    private readonly HashSet<ushort> _reads = [.. Enum.GetValues<EarFunSetting>().Select(s => EarFunSettings.Command(profile.Model, s).Read)
        .Where(c => c != 0), EarFunGestures.Read(profile.Model)];
    private readonly HashSet<ushort> _unreadable = [];
    // Refreshes that may still ask again for settings that have not answered.
    private int _rereads;
    // The PC's adapter address, to recognise this PC by the partial address in the list.
    private readonly byte[]? _host = Vivo.VivoParsers.TryParseHostAddress(context.Identity.DeviceId, out var host) ? host : null;
    private readonly object _sourceGate = new();
    private readonly List<EarFunSource> _sources = [];
    private readonly Dictionary<string, bool> _playing = [];
    private bool _freshSources;
    private int _sourceRevision;
    private EarFunProfile _profile = new(profile.Model);
    private EarFunModel Model => _profile.Model;

    protected override IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes)
    {
        BrandPacket[] packets = [.. _codec.Feed(bytes).Select(p => new BrandPacket(p.Command, p.Status, p.Payload))];
        foreach (var packet in packets) TraceFrame(FrameDirection.Received, packet.Command, Model.Gaia ? packet.Type : null, packet.Payload);
        // Frames the codec skips (another vendor, a reply form it does not know): only a frame header, never payload.
        if (packets.Length == 0 && Context.Trace is { } trace)
            trace.OnBrandFrame(FrameDirection.Received, bytes.Length > 0 && bytes[0] == (Model.Gaia ? 0xFF : 0xEF)
                ? $"EARFUN no EarFun frame in {bytes.Length} bytes, head {Convert.ToHexString(bytes[..Math.Min(Model.Gaia ? 8 : 4, bytes.Length)])}"
                : $"EARFUN no EarFun frame in {bytes.Length} bytes", Now);
        return packets;
    }

    private void TraceFrame(FrameDirection direction, ushort command, byte? status, byte[] payload)
    {
        if (Context.Trace is not { } trace) return;
        string body = _traced.Contains(command) ? Convert.ToHexString(payload) : $"<{payload.Length} bytes>";
        trace.OnBrandFrame(direction, $"EARFUN {(Model.Gaia ? "GAIA" : "EF")} {command:X4}{(status is { } s ? $" status {s:X2}" : "")} {body}", Now);
    }

    protected override void HandlePacket(BrandPacket packet)
    {
        if (!Closed.IsCompleted && packet.Type == 0 && Model.Has("list") && ApplySource(packet)) return;
        if (!Closed.IsCompleted && packet.Type == 0 && Apply(packet, StateSource.DeviceNotification))
        {
            _values[packet.Command] = packet.Payload;
            _profile = new(Model, _values.Keys.ToHashSet());
            Store.TryUpdate(DeviceId, s => s with { Profile = _profile, LastResponseAt = Now });
        }
        // The control stays unavailable until a report is understood; keep the first one for diagnostics.
        else if (packet.Type == 0 && _reads.Contains(packet.Command) && !_profile.Confirmed(packet.Command))
            lock (_unreadable)
                if (_unreadable.Add(packet.Command))
                    Logger.LogInformation("EarFun report {Command:X4} not understood: {Payload}", packet.Command, Convert.ToHexString(packet.Payload));
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
        foreach (var (feature, setting) in new[] { ("noise", EarFunSetting.Noise), ("game", EarFunSetting.Game),
            ("dual", EarFunSetting.Dual), ("ear", EarFunSetting.Wear), ("prompt", EarFunSetting.Prompt),
            ("find", EarFunSetting.Find), ("spatial", EarFunSetting.Spatial), ("sub", EarFunSetting.Sub),
            ("ambient", EarFunSetting.Ambient), ("autooff", EarFunSetting.AutoOff), ("mic", EarFunSetting.Mic),
            ("quality", EarFunSetting.Quality), ("lock", EarFunSetting.TouchLock) })
            if (Model.Has(feature) && (setting != EarFunSetting.Quality || EarFunSettings.CodecBits(Model).Count > 0)) yield return setting;
    }

    // ---- dual-connection list --------------------------------------------------------------------

    private bool ApplySource(BrandPacket packet)
    {
        if (packet.Command == EarFunSettings.DeviceList(Model))
        {
            if (EarFunSettings.Source(Model, packet.Payload) is not { } source) return false;
            lock (_sourceGate)
            {
                // A requested list replaces the previous one; TW600 restarts its numbering at 0 (EarKit).
                if (_freshSources || (Model.Variant == "tw600" && source.Number == 0)) { _sources.Clear(); _freshSources = false; }
                _sources.RemoveAll(s => s.Mac.AsSpan().SequenceEqual(source.Mac));
                _sources.Add(source);
                _sourceRevision++;
            }
            PublishSources();
            return true;
        }
        if (Model.Has("play") && packet.Command == EarFunSettings.PlayList && packet.Payload is [_, _, _, var play, ..])
        {
            lock (_sourceGate) _playing[Convert.ToHexString(packet.Payload, 0, 3)] = play == 1;
            PublishSources();
            return true;
        }
        return false;
    }

    private void PublishSources()
    {
        SourceDevice[] devices;
        lock (_sourceGate)
        {
            if (_sources.Count == 0) return;
            // The key carries the list number for the command and the partial address to re-find the host.
            devices = [.. _sources.Where(s => s.Connected || !Model.Has("connectedonly")).OrderBy(s => s.Number)
                .Select(s => new SourceDevice([s.Number, .. s.Mac], s.Name, s.Connected ? 2 : 0, IsThisHost(s),
                    _playing.TryGetValue(Convert.ToHexString(s.Mac), out bool playing) && playing, 0))];
        }
        Store.TryUpdate(DeviceId, s => s with { SourceDevices = Observed.From<IReadOnlyList<SourceDevice>>(devices, Now, StateSource.QueryResponse) });
    }

    /// <summary>
    /// The list has only part of each address (byte order unconfirmed, so both are accepted) and a name cut to 19
    /// bytes; EarKit recognises the PC by its name.
    /// </summary>
    private bool IsThisHost(EarFunSource source)
    {
        if (_host is { } host && source.Mac.Length is > 0 and < 6)
        {
            var tail = host.AsSpan(6 - source.Mac.Length);
            if (tail.SequenceEqual(source.Mac) || tail.SequenceEqual(source.Mac.Reverse().ToArray())) return true;
        }
        string machine = Environment.MachineName;
        return source.Name.Length > 0 && (source.Name.Equals(machine, StringComparison.OrdinalIgnoreCase)
            || (machine.Length == 15 && source.Name.StartsWith(machine, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<bool> RequestSourcesAsync(CancellationToken ct)
    {
        lock (_sourceGate) _freshSources = true;
        return await Send(EarFunSettings.DeviceList(Model), [], ct).ConfigureAwait(false)
            && (!Model.Has("play") || await Send(EarFunSettings.PlayList, [], ct).ConfigureAwait(false));
    }

    /// <summary>A fresh list, or null when no host entry arrived.</summary>
    private async Task<IReadOnlyList<SourceDevice>?> ReadSourcesAsync(CancellationToken ct)
    {
        int revision;
        lock (_sourceGate) revision = _sourceRevision;
        if (!await RequestSourcesAsync(ct).ConfigureAwait(false)) return null;
        await Task.Delay(SourceListWindow, Context.Time, ct).ConfigureAwait(false);
        lock (_sourceGate) if (_sourceRevision == revision) return null;
        return Store.Current.SourceDevices?.Value;
    }

    public override Task<CommandResult> MultiConnectAsync(MultiConnectOperation operation, byte[] address, CancellationToken cancellationToken = default)
    {
        // The earbuds have no command to forget a host.
        if (!Model.Has("list") || operation == MultiConnectOperation.Remove || address.Length < 2) return Task.FromResult(NotSupported("Dual connection operation"));
        bool connect = operation == MultiConnectOperation.Connect;
        bool Same(SourceDevice d) => d.Address.AsSpan(1).SequenceEqual(address.AsSpan(1));
        return Serialized(async ct =>
        {
            // Verify on a fresh list: same number and address, not this PC, and a free connection slot.
            var before = await ReadSourcesAsync(ct).ConfigureAwait(false);
            if (Closed.IsCompleted) return TimedOut;
            if (before?.FirstOrDefault(d => d.Address.AsSpan().SequenceEqual(address)) is not { } target)
                return new(CommandStatus.Rejected, "The paired-device list changed; nothing was sent.");
            if (target.IsConnected == connect) return CommandResult.Ok;
            if (!connect && target.IsThisDevice) return new(CommandStatus.Rejected, "This PC disconnects through Windows.");
            if (connect && before.Count(d => d.IsConnected) >= 2)
                return new(CommandStatus.Rejected, "Two devices are already connected; disconnect one first.");
            ushort command = connect ? EarFunSettings.ConnectHost(Model) : EarFunSettings.DisconnectHost(Model);
            await Request(command, [address[0]], ct, AckTimeout, optional: true).ConfigureAwait(false);
            long started = Context.Time.GetTimestamp();
            int resent = 0;
            while (!Closed.IsCompleted && Context.Time.GetElapsedTime(started) < HostSwitchWindow)
            {
                var list = await ReadSourcesAsync(ct).ConfigureAwait(false);
                if (list?.FirstOrDefault(Same) is { } now && now.IsConnected == connect) return CommandResult.Ok;
                if (!Model.Has("noresend") && resent < 2 && Context.Time.GetElapsedTime(started) >= TimeSpan.FromSeconds(3 * (resent + 1)))
                {
                    resent++;
                    await Request(command, [address[0]], ct, AckTimeout, optional: true).ConfigureAwait(false);
                }
            }
            return Closed.IsCompleted ? TimedOut : new(CommandStatus.Rejected, $"The earbuds did not report the host as {(connect ? "connected" : "disconnected")}.");
        }, cancellationToken);
    }

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        Store.TryUpdate(DeviceId, s => s with { Profile = _profile, Battery = BatteryStatus.Unknown,
            NoiseControl = null, Toggles = s.Toggles.Clear(), Options = s.Options.Clear(), PromptVolume = null, Spatial = null,
            Firmware = null, KeyFunctions = null, IsFindingEarbuds = false, SourceDevices = null });
        lock (_sourceGate) { _sources.Clear(); _playing.Clear(); }
        if (Model.Gaia && Model.Variant is not ("tw600" or "tw700"))
        {
            // Legacy QCC initialization/subscriptions, exactly as EarKit. TW600/TW700 must skip these.
            byte[][] bodies = [[0,10,3,0], [0,0x1D,0,1], [0,0x1D,0,0x0D,7,0,0,0,4],
                [0,0x1D,0,7,0x0D], [0,0x1D,0,7,5], [0,0x1D,0,7,8], [0,0x1D,0,7,1],
                [0,0x1D,0,7,6], [0,0x1D,0,7,7], [0,0x1D,0,7,7]];
            foreach (var body in bodies)
            {
                if (!await WriteAsync(new byte[] { 0xFF,4,0,(byte)(body.Length - 4) }.Concat(body).ToArray(), cancellationToken).ConfigureAwait(false)) return false;
                await Task.Delay(FrameGap, Context.Time, cancellationToken).ConfigureAwait(false);
            }
        }
        if (await Read(EarFunSetting.Firmware, cancellationToken).ConfigureAwait(false) is null
            || await Read(EarFunSetting.Battery, cancellationToken).ConfigureAwait(false) is null) return false;
        // Optional discovery is not a transaction: unsupported features may stay silent. Only valid
        // reports open controls. Subsequent transactions never reuse a timed-out session.
        foreach (var setting in Settings().Where(s => s is not (EarFunSetting.Firmware or EarFunSetting.Battery)))
        {
            if (!await Send(EarFunSettings.Command(Model, setting).Read, [], cancellationToken).ConfigureAwait(false)) return false;
            await Task.Delay(FrameGap, Context.Time, cancellationToken).ConfigureAwait(false);
        }
        if (Model.Has("keys") && !await Send(EarFunGestures.Read(Model), [], cancellationToken).ConfigureAwait(false)) return false;
        if (Model.Has("list") && !await RequestSourcesAsync(cancellationToken).ConfigureAwait(false)) return false;
        // The earbuds never report their EQ: show the curve EncoDeck last sent (kept across sessions by the
        // state cache), or a flat one until something is sent.
        Store.TryUpdate(DeviceId, s => s.CustomEqualizers?.Value is [var curve] && EarFunEqualizer.Fits(Model, curve) ? s
            : s with { CustomEqualizers = Observed.From<IReadOnlyList<CustomEqualizer>>([EarFunEqualizer.Flat(Model)], Now, StateSource.Optimistic) });
        _rereads = 3;
        StartPolling();
        return true;
    }

    private Task<bool> Send(ushort command, byte[] payload, CancellationToken ct)
    {
        TraceFrame(FrameDirection.Sent, command, null, payload);
        return WriteAsync(EarFunFrameCodec.Encode(Model.Gaia, command, payload), ct);
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
        if (pair == EarFunSetting.TouchLock)
        {
            if (EarFunSettings.TouchLockValue(Model, p) is not { } locked || EarFunSettings.Option(Model, pair, (byte)locked) is not { } option) return false;
            Store.TryUpdate(DeviceId, s => s with { Options = s.Options.SetItem(option.Id, Observed.From(option, Now, source)) });
            return true;
        }
        // EarKit reads only the first value byte; later bytes must not hide a reported setting.
        if (p.Length < 1) return false;
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
                    // TW700 keeps four flags in the low bits (all set: off); EarKit ignores the high bits (issue 16).
                    if (!Model.Tw700 && value > 1) return false;
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
            case EarFunSetting.Sub or EarFunSetting.Ambient or EarFunSetting.AutoOff or EarFunSetting.Mic:
                if (EarFunSettings.Option(Model, pair, value) is not { } option) return false;
                Store.TryUpdate(DeviceId, s => s with { Options = s.Options.SetItem(option.Id, Observed.From(option, Now, source)) }); return true;
            case EarFunSetting.Quality:
                // Independent codec bits; bits EncoDeck does not offer are kept as reported.
                Store.TryUpdate(DeviceId, s => s with { Toggles = EarFunSettings.CodecBits(Model)
                    .Aggregate(s.Toggles, (toggles, bit) => toggles.SetItem(bit.Key, Observed.From((value & bit.Value) != 0, Now, source))) });
                return true;
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

    private async Task<BrandPacket?> Request(ushort command, byte[] payload, CancellationToken ct, TimeSpan? timeout = null, bool optional = false)
    {
        try
        {
            TraceFrame(FrameDirection.Sent, command, null, payload);
            var reply = await ExchangeAsync(EarFunFrameCodec.Encode(Model.Gaia, command, payload), p => p.Command == command, ct, timeout).ConfigureAwait(false);
            // No sequence ids: a late reply must never confirm another transaction. Optional replies belong to
            // a write, which only its readback confirms (WriteConfirmed).
            if (reply is null && !optional) EndSession();
            return reply;
        }
        catch (OperationCanceledException) { EndSession(); throw; }
    }

    /// <summary>
    /// Writes a setting and confirms it by readback. The acknowledgement can only reject: the Air Pro 4+ applies a
    /// setting without one EncoDeck receives (issue 16), and earbuds that are still switching report the old value
    /// for a moment, so the readback repeats for up to <see cref="ConfirmWindow"/>. The session ends only when the
    /// earbuds stop answering reads. With <paramref name="restarts"/> (a setting the earbuds apply by restarting),
    /// a channel that closes after the write is the expected outcome.
    /// </summary>
    private async Task<CommandResult> WriteConfirmed(ushort write, byte[] payload, ushort read, Func<BrandPacket, bool> confirmed, CancellationToken ct, bool restarts = false)
    {
        long started = Context.Time.GetTimestamp();
        var ack = await Request(write, payload, ct, AckTimeout, optional: true).ConfigureAwait(false);
        if (Closed.IsCompleted) return Restarted(write, restarts);
        if (ack is null) Logger.LogInformation("EarFun write {Command:X4} not acknowledged; confirming by readback", write);
        else if (ack.Type != 0 || (!Model.Gaia && ack.Payload.Length != 0 && !ack.Payload.SequenceEqual(new byte[] { 0 })))
            return new(CommandStatus.Rejected, "EarFun rejected the setting.");
        int attempts = 0, answered = 0;
        while (true)
        {
            attempts++;
            if (await Request(read, [], ct, optional: true).ConfigureAwait(false) is { } reply)
            {
                answered++;
                if (confirmed(reply)) return CommandResult.Ok;
            }
            if (Closed.IsCompleted) return Restarted(write, restarts);
            if (Context.Time.GetElapsedTime(started) >= ConfirmWindow) break;
            await Task.Delay(ReadbackRetryDelay, Context.Time, ct).ConfigureAwait(false);
        }
        Logger.LogInformation("EarFun write {Command:X4} not confirmed: {Answered} of {Attempts} readbacks answered", write, answered, attempts);
        if (answered > 0) return new(CommandStatus.Rejected, "EarFun did not confirm the requested value.");
        EndSession();
        return TimedOut;
    }

    private CommandResult Restarted(ushort write, bool restarts)
    {
        if (!restarts) return TimedOut;
        // EncoDeck reconnects and reads the applied value once the earbuds are back.
        Logger.LogInformation("EarFun restarts to apply write {Command:X4}", write);
        return CommandResult.Ok;
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

    private Task<CommandResult> Change(EarFunSetting setting, byte[] payload, Func<byte[], bool> matches, CancellationToken ct, bool stopping = false) =>
        Serialized(token => ChangeCore(setting, payload, matches, token, stopping), ct);

    private async Task<CommandResult> ChangeCore(EarFunSetting setting, byte[] payload, Func<byte[], bool> matches, CancellationToken token, bool stopping = false, bool restarts = false)
    {
        var commands = EarFunSettings.Command(Model, setting);
        if (commands.Write == 0 || (!stopping && !Model.Has(Feature(setting)))) return NotSupported("EarFun setting");
        return await WriteConfirmed(commands.Write, payload, commands.Read,
            reply => reply.Type == 0 && Apply(reply, StateSource.QueryResponse) && matches(reply.Payload), token, restarts).ConfigureAwait(false);
    }

    private static string Feature(EarFunSetting setting) => setting switch
    {
        EarFunSetting.Wear => "ear", EarFunSetting.AutoOff => "autooff", EarFunSetting.TouchLock => "lock", _ => setting.ToString().ToLowerInvariant(),
    };

    private byte ToggleValue(EarFunSetting setting, bool enabled) =>
        setting == EarFunSetting.Wear ? (byte)(enabled ? 0 : Model.Tw700 ? 15 : 1) : (byte)(enabled ? Model.Gaia ? 1 : 2 : Model.Gaia ? 0 : 1);

    private Task<CommandResult> WriteToggle(EarFunSetting setting, bool enabled, CancellationToken ct, bool restarts = false)
    {
        byte value = ToggleValue(setting, enabled);
        return ChangeCore(setting, [value], p => setting == EarFunSetting.Wear && Model.Tw700 ? ((p[0] & 15) != 15) == enabled : p[0] == value, ct, restarts: restarts);
    }

    /// <summary>
    /// Dual connection and LDAC exclude each other on the LDAC models, so turning one on first turns the other off
    /// (EarKit asks, the official app blocks). Models that restart to apply dual connection do so after the write.
    /// </summary>
    private async Task<CommandResult> SetDual(bool enabled, CancellationToken ct)
    {
        if (enabled && Store.Current.Toggles.TryGetValue(DeviceToggle.Ldac, out var ldac) && ldac.Value)
        {
            var off = await WriteCodec(DeviceToggle.Ldac, false, ct).ConfigureAwait(false);
            if (!off.IsSuccess || Closed.IsCompleted) return off;
        }
        var result = await WriteToggle(EarFunSetting.Dual, enabled, ct, Model.Has(enabled ? "dualrestart" : "dualrestartoff")).ConfigureAwait(false);
        if (result.IsSuccess && enabled && Model.Has("list") && !Closed.IsCompleted) await RequestSourcesAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Changes one codec bit after a fresh read, keeping the others. The earbuds may drop the link to switch codecs
    /// (EarKit), so a channel that closes after the write counts as applied.
    /// </summary>
    private async Task<CommandResult> WriteCodec(DeviceToggle toggle, bool enabled, CancellationToken ct)
    {
        var (read, write) = EarFunSettings.Command(Model, EarFunSetting.Quality);
        byte bit = EarFunSettings.CodecBits(Model)[toggle];
        if (enabled && toggle == DeviceToggle.Ldac && Store.Current.Toggles.TryGetValue(DeviceToggle.DualDeviceConnection, out var dual) && dual.Value
            && !Model.Has("dualrestartoff"))
        {
            var off = await WriteToggle(EarFunSetting.Dual, false, ct).ConfigureAwait(false);
            if (!off.IsSuccess) return off;
        }
        if (await Request(read, [], ct).ConfigureAwait(false) is not { Type: 0, Payload: [var raw, ..] } current || !Apply(current, StateSource.QueryResponse)) return TimedOut;
        byte value = (byte)(enabled ? raw | bit : raw & ~bit);
        if (value == raw) return CommandResult.Ok;
        var result = await WriteConfirmed(write, [value], read, reply => reply.Type == 0 && Apply(reply, StateSource.QueryResponse)
            && reply.Payload is [var v, ..] && ((v & bit) != 0) == enabled, ct, restarts: true).ConfigureAwait(false);
        // Dual connection stays on where turning it off restarts the earbuds (the Clip); show what the firmware reports.
        if (result.IsSuccess && enabled && toggle == DeviceToggle.Ldac && Model.Has("dual") && !Closed.IsCompleted
            && await Request(EarFunSettings.Command(Model, EarFunSetting.Dual).Read, [], ct, optional: true).ConfigureAwait(false) is { Type: 0 } reread)
            Apply(reread, StateSource.QueryResponse);
        return result;
    }

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => Serialized(async ct =>
    {
        foreach (var setting in Settings().Where(s => s is EarFunSetting.Firmware or EarFunSetting.Battery || _profile.Confirmed(EarFunSettings.Command(Model, s).Read)))
            if (await Read(setting, ct).ConfigureAwait(false) is null)
                return Closed.IsCompleted ? TimedOut : new(CommandStatus.Rejected, "EarFun returned an invalid or rejected refresh response.");
        // Hosts come and go without a report; the entries arrive after this transaction.
        if (Model.Has("list")) await RequestSourcesAsync(ct).ConfigureAwait(false);
        // A start-up read can go unanswered; ask a few more times without waiting, like start-up (EarKit
        // re-reads every setting after each change).
        if (_rereads > 0)
        {
            _rereads--;
            foreach (var setting in Settings().Where(s => !_profile.Confirmed(EarFunSettings.Command(Model, s).Read)))
            {
                if (!await Send(EarFunSettings.Command(Model, setting).Read, [], ct).ConfigureAwait(false)) return TimedOut;
                await Task.Delay(FrameGap, Context.Time, ct).ConfigureAwait(false);
            }
            if (Model.Has("keys") && Store.Current.KeyFunctions is null) await Send(EarFunGestures.Read(Model), [], ct).ConfigureAwait(false);
        }
        return CommandResult.Ok;
    }, cancellationToken);

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        var option = EarFunSettings.NoiseModes(Model).FirstOrDefault(p => p.State == target);
        return option.State is null ? Task.FromResult(NotSupported("Noise mode")) : Change(EarFunSetting.Noise, [option.Value], p => p[0] == option.Value, cancellationToken);
    }
    public override Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        if (EarFunSettings.CodecBits(Model).ContainsKey(toggle)) return Serialized(ct => WriteCodec(toggle, enabled, ct), cancellationToken);
        EarFunSetting? setting = toggle switch { DeviceToggle.GamingMode => EarFunSetting.Game, DeviceToggle.DualDeviceConnection => EarFunSetting.Dual, DeviceToggle.InEarDetection => EarFunSetting.Wear, _ => null };
        if (setting is null || !Model.Has(Feature(setting.Value))) return Task.FromResult(NotSupported("Toggle"));
        return Serialized(ct => setting == EarFunSetting.Dual ? SetDual(enabled, ct) : WriteToggle(setting.Value, enabled, ct), cancellationToken);
    }
    public override Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default) =>
        mode is not (SpatialAudioMode.Off or SpatialAudioMode.Fixed) ? Task.FromResult(NotSupported("Head tracking")) : Change(EarFunSetting.Spatial, [mode == SpatialAudioMode.Off ? (byte)1 : (byte)2], p => p[0] == (mode == SpatialAudioMode.Off ? 1 : 2), cancellationToken);
    public override Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default)
    {
        if (level is < 0 or > 100 || level % 25 != 0) return Task.FromResult(NotSupported("Prompt volume"));
        byte value = (byte)(Model.Gaia ? (100 - level) / 25 : 1 + level / 25);
        return Change(EarFunSetting.Prompt, [value], p => p[0] == value, cancellationToken);
    }
    public override Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default)
    {
        if (!Model.Has("find")) return Task.FromResult(NotSupported("Find earbuds"));
        byte[] value = Model.Gaia ? [ring ? (byte)3 : (byte)0] : [ring ? (byte)1 : (byte)0, ring ? (byte)1 : (byte)0];
        return Change(EarFunSetting.Find, value, p => Model.Gaia ? p[0] == value[0] : p.SequenceEqual(value), cancellationToken, stopping: !ring);
    }
    public override Task<CommandResult> SetOptionAsync(string id, int value, CancellationToken cancellationToken = default)
    {
        if (!Store.Current.Options.TryGetValue(id, out var option) || !option.Value.Accepts(value)) return Task.FromResult(NotSupported("Option value"));
        var setting = new[] { EarFunSetting.Sub, EarFunSetting.Ambient, EarFunSetting.AutoOff, EarFunSetting.Mic, EarFunSetting.TouchLock }
            .Cast<EarFunSetting?>().FirstOrDefault(s => id == "earfun-" + s!.Value.ToString().ToLowerInvariant());
        if (setting is null) return Task.FromResult(NotSupported("Option"));
        return setting == EarFunSetting.TouchLock
            ? Change(EarFunSetting.TouchLock, EarFunSettings.TouchLockPayload(Model, value), p => EarFunSettings.TouchLockValue(Model, p) == value, cancellationToken)
            : Change(setting.Value, [(byte)value], p => EarFunSettings.Option(Model, setting.Value, p[0])?.Value == value, cancellationToken);
    }

    public override Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default) => Serialized(async ct =>
    {
        if (!Model.Has("keys")) return NotSupported("Gestures");
        var slots = EarFunGestures.Slots(Model);
        int index = Enumerable.Range(0, slots.Count).FirstOrDefault(i => slots[i].Matches(assignment), -1);
        if (index < 0 || slots[index].Choices.FirstOrDefault(c => c.Function == assignment.Function) is not { } choice
            || (choice.Requires is { } capability && !_profile.Capabilities.IsAvailable(capability, allowExperimental: false))) return NotSupported("Gesture assignment");
        var current = await Request(EarFunGestures.Read(Model), [], ct).ConfigureAwait(false);
        if (current is not { Type: 0 } || EarFunGestures.Decode(Model, current.Payload) is not { } old) return TimedOut;
        byte[] payload;
        if (Model.Gaia)
        {
            byte type = assignment.Action == GestureAction.SqueezeAndHold ? (byte)17 : (byte)assignment.Action;
            payload = assignment.Side == EarSide.Left ? [type, (byte)assignment.Function, 0, 0] : [0, 0, type, (byte)assignment.Function];
        }
        else { payload = current.Payload.ToArray(); payload[index] = (byte)assignment.Function; }
        return await WriteConfirmed(EarFunGestures.Write(Model), payload, EarFunGestures.Read(Model), reply => reply.Type == 0
            && EarFunGestures.Decode(Model, reply.Payload) is { } keys && keys.Select((key, i) => key == (i == index ? assignment : old[i])).All(v => v), ct).ConfigureAwait(false);
    }, cancellationToken);

    // ---- user EQ and factory reset (no readback) -------------------------------------------------

    public override Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken cancellationToken = default) =>
        preset.Id != EarFunEqualizer.CurveId || !EarFunEqualizer.Fits(Model, preset) ? Task.FromResult(NotSupported("EQ curve"))
        : Serialized(ct => WriteCurve([.. preset.Bands.Select(b => b.Gain)], ct), cancellationToken);

    /// <summary>The curve is the earbuds' only EQ; selecting it sends it again.</summary>
    public override Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) =>
        Store.Current.CustomEqualizers?.Value is [var curve] && curve.Id == id ? SaveCustomEqualizerAsync(curve, cancellationToken)
        : Task.FromResult(NotSupported("EQ curve"));

    /// <summary>
    /// Sends the curve band by band (in one write where the firmware takes it so). The earbuds answer EQ writes on
    /// another GAIA vendor or not at all, so a completed write is all EncoDeck learns: the state keeps it as sent.
    /// </summary>
    private async Task<CommandResult> WriteCurve(int[] gains, CancellationToken ct)
    {
        var frames = EarFunEqualizer.Encode(Model, gains);
        ushort command = Model.Gaia ? EarFunEqualizer.GaiaCommand : EarFunEqualizer.EfCommand;
        foreach (var frame in frames) TraceFrame(FrameDirection.Sent, command, null, Model.Gaia ? frame[8..] : frame[4..^2]);
        if (EarFunEqualizer.Combined(Model))
        {
            if (!await WriteAsync(frames.SelectMany(f => f).ToArray(), ct).ConfigureAwait(false)) return TimedOut;
        }
        else
        {
            foreach (var frame in frames)
            {
                if (!await WriteAsync(frame, ct).ConfigureAwait(false)) return TimedOut;
                await Task.Delay(FrameGap, Context.Time, ct).ConfigureAwait(false);
            }
        }
        Store.TryUpdate(DeviceId, s => s with { CustomEqualizers = Observed.From<IReadOnlyList<CustomEqualizer>>([EarFunEqualizer.Curve(Model, gains)], Now, StateSource.Sent) });
        return CommandResult.Ok;
    }

    /// <summary>EarKit's reset (GAIA 0x0336, EF 0x1000 with AF A5). The earbuds restart and forget every host, so the link drops.</summary>
    public override Task<CommandResult> FactoryResetAsync(CancellationToken cancellationToken = default) => Serialized(async ct =>
    {
        if (!await Send(EarFunSettings.Reset(Model), Model.Gaia ? [] : [0xAF, 0xA5], ct).ConfigureAwait(false)) return TimedOut;
        Logger.LogInformation("EarFun factory reset sent");
        return CommandResult.Ok;
    }, cancellationToken);
}
