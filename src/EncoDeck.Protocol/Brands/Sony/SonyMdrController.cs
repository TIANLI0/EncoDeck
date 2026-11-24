// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Sony;

/// <summary>
/// Sony MDR v1 / v2 controller. Every request waits for its ACK (750 ms, ten retransmissions) and a
/// matching reply; settings are offered only after a live readback and every write is confirmed by
/// reading the setting again. Replies that arrive before the ACK of their request (WH-1000XM3 4.5.2,
/// mos9527/SonyHeadphonesClient PR #66) complete the request as well.
/// </summary>
internal sealed partial class SonyMdrController(IDuplexByteChannel channel, ControllerContext context, SonyAkgProfile profile)
    : BrandControllerBase(channel, context)
{
    private const string AmbientLevelOption = "sony-ambient-level";
    private readonly SonyFrameCodec _sony = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _gate = new();
    private TaskCompletionSource<byte[]>? _pending;
    private byte _expected, _subtype, _sequence;
    private TaskCompletionSource? _ack;
    private byte _ackSequence;
    private byte? _receivedSequence;
    private bool _v2;
    private HashSet<byte>? _functions;
    private byte? _noiseType;
    private byte _batteryType;
    private byte? _caseType;
    private bool _noiseReady;
    private string? _firmware;
    private SonyAkgProfile _profile = profile;
    /// <summary>Last ambient level the device reported (v1 keeps none while not in ambient mode).</summary>
    private int _ambientLevel = SonyV2Noise.MaximumAmbientLevel;

    private string Model => _profile.DisplayName;
    private byte NoiseType => _v2 ? _noiseType!.Value : (byte)2;
    private bool Has(byte function) => _functions?.Contains(function) == true;

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting Sony MDR protocol handshake");
        var protocol = await Query(0, 1, 0, cancellationToken).ConfigureAwait(false);
        if (protocol is null) return false;
        _v2 = protocol.Length == 8;
        Logger.LogInformation("Sony MDR protocol {Protocol} confirmed", _v2 ? "v2" : "v1");
        if (await Query(4, 5, 2, cancellationToken).ConfigureAwait(false) is { } info && SonySettings.TryDeviceInfo(info, 2) is { } firmware)
        {
            _firmware = firmware;
            Logger.LogInformation("Sony firmware {Firmware}", firmware);
            Store.TryUpdate(DeviceId, s => s with { Firmware = Observed.From(new FirmwareInfo(firmware, Model, new Dictionary<string, string>()), Now, StateSource.QueryResponse) });
        }
        var functions = await Query(6, 7, 0, cancellationToken).ConfigureAwait(false);
        if (functions is not null && SonySettings.TryFunctions(functions, _v2, out var set)) _functions = set;
        else if (_v2) return false;
        if (_v2)
        {
            foreach (byte function in _functions!)
            {
                _noiseType ??= SonyV2Noise.InquiredType(function);
                _eqType ??= function switch { 80 => (byte)0, 82 => (byte)2, 87 => (byte)4, _ => null };
                if (function == 40) _batteryType = 8;
                if (function == 33) _batteryType = 1;
                if (function == 41) _batteryType = 9;
                if (function == 34) _caseType = 2;
                if (function == 42) _caseType = 10;
            }
            Logger.LogInformation(
                "Sony v2 capability types: battery={Battery:X2}, noise={Noise:X2}, eq={Eq:X2}, function table={FunctionCount} entries",
                _batteryType, _noiseType ?? 0, _eqType ?? 0, _functions.Count);
        }
        else
        {
            // Without a function table keep the long-standing defaults (PRESET_EQ type 1, NC/ASM type 2).
            if (_functions is null || Has(SonySettings.V1PresetEq)) _eqType = 1;
            _batteryType = Has(SonySettings.V1LeftRightBattery) || (_functions is null && _profile.Form == DeviceForm.Earbuds) ? (byte)1 : (byte)0;
            if (Has(SonySettings.V1CradleBattery)) _caseType = 2;
        }
        Store.TryUpdate(DeviceId, s => s with { Options = ImmutableDictionary<string, Observed<DeviceOption>>.Empty });
        return (await RefreshAsync(cancellationToken).ConfigureAwait(false)).Status == CommandStatus.Success;
    }

    private async Task<byte[]?> Query(byte command, byte expected, byte subtype, CancellationToken ct, byte[]? payload = null)
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _pending = completion; _expected = expected; _subtype = subtype; }
        try
        {
            // The reply may arrive before the ACK; it completes this request either way.
            if (!await Send(command, payload ?? [subtype], ct).ConfigureAwait(false)) return null;
            try
            {
                await Task.WhenAny(completion.Task, Closed).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                return completion.Task.IsCompletedSuccessfully ? await completion.Task.ConfigureAwait(false) : null;
            }
            catch (TimeoutException)
            {
                Logger.LogInformation("Control response timed out: command {Command:X2}, subtype {Subtype:X2}", command, subtype);
                return null;
            }
        }
        finally { lock (_gate) { if (_pending == completion) _pending = null; } }
    }

    private async Task<bool> Send(byte command, byte[] payload, CancellationToken ct)
    {
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _ack = ack; _ackSequence = (byte)(_sequence ^ 1); }
        try
        {
            // Sound Connect Bl.e / yl.c: 750 ms ACK timeout, at most ten retransmissions.
            // Retransmit the same frame and sequence; changing the bit could apply a SET twice.
            byte[] frame = SonyFrameCodec.Encode(0x0c, _sequence, [command, .. payload]);
            for (int attempt = 0; attempt <= 10; attempt++)
            {
                if (Closed.IsCompleted || !await WriteAsync(frame, ct).ConfigureAwait(false)) return false;
                try
                {
                    await Task.WhenAny(ack.Task, Closed).WaitAsync(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
                    if (!ack.Task.IsCompletedSuccessfully || Closed.IsCompleted) return false;
                    _sequence ^= 1;
                    return true;
                }
                catch (TimeoutException)
                {
                    Logger.LogInformation("Sony ACK timeout: command {Command:X2}, sequence {Sequence}, attempt {Attempt}/11", command, _sequence, attempt + 1);
                }
            }
            // Like the official client, reconnect when ACK retries are exhausted.
            // The peer may have processed the request, so do not reuse its sequence bit.
            EndSession();
            return false;
        }
        catch (OperationCanceledException)
        {
            EndSession();
            throw;
        }
        finally { lock (_gate) { if (_ack == ack) _ack = null; } }
    }

    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            byte batteryCommand = _v2 ? (byte)0x22 : (byte)0x10;
            byte[]? battery = await Query(batteryCommand, (byte)(batteryCommand + 1), _batteryType, cancellationToken).ConfigureAwait(false);
            if (_v2 ? _noiseType.HasValue : _functions is null || Has(SonySettings.V1NoiseAndAmbient))
                _noiseReady = await Query(0x66, 0x67, NoiseType, cancellationToken).ConfigureAwait(false) is not null;
            _profile = _profile.WithNoiseSupport(_noiseReady).WithAmbientModes(_noiseReady);
            PublishProfile();
            if (_eqType.HasValue) await RefreshEqualizer(cancellationToken).ConfigureAwait(false);
            if (_caseType.HasValue) await Query(batteryCommand, (byte)(batteryCommand + 1), _caseType.Value, cancellationToken).ConfigureAwait(false);
            await RefreshSettings(cancellationToken).ConfigureAwait(false);
            return battery is null ? new(CommandStatus.Timeout, "No battery response on the official control service.") : CommandResult.Ok;
        }
        finally { _commands.Release(); }
    }

    private void PublishProfile() => Store.TryUpdate(DeviceId, s => s with { Profile = _profile });

    private async Task<byte[]?> ReadNoise(CancellationToken ct) => await Query(0x66, 0x67, NoiseType, ct).ConfigureAwait(false);

    private bool TryReadNoise(byte[] p, out NoiseControlMode mode, out bool voice, out int level) => _v2
        ? SonyV2Noise.TryRead(p, out mode, out voice, out level)
        : SonyV1Noise.TryRead(p, out mode, out voice, out level);

    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        bool? voice = target.LevelId switch { null => null, SonyAkgProfile.SonyVoice => true, SonyAkgProfile.SonyAmbient => false, _ => (bool?)null };
        if (target.Mode is not (NoiseControlMode.Off or NoiseControlMode.NoiseCancellation or NoiseControlMode.Transparency)
            || (target.LevelId is not null && (target.Mode != NoiseControlMode.Transparency || voice is null)))
            return CommandResult.Unsupported("This protocol exposes off, noise cancelling and ambient sound (standard or focus on voice) only.");
        return await Exclusive(PendingKeys.NoiseControl, async ct =>
        {
            if (!_noiseReady || (_v2 && !_noiseType.HasValue)) return CommandResult.Unsupported("A compatible noise-control layout has not been confirmed on this device.");
            if (SonySettings.IgnoresNoiseWrites(Model, _firmware))
                return CommandResult.Unsupported("WF-C700N firmware 2.0.5 acknowledges noise-control changes but does not apply them; update the earbuds to 2.0.7 or later.");
            var current = await ReadNoise(ct).ConfigureAwait(false);
            if (current is null) return new(CommandStatus.Timeout, "Noise parameters unavailable.");
            var payload = _v2 ? SonyV2Noise.SetPayload(current, target.Mode, voice, null) : SonyV1Noise.SetPayload(current, target.Mode, voice, _ambientLevel);
            if (payload is null) return CommandResult.Unsupported("Unknown noise parameter layout.");
            if (!await Send(0x68, payload, ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
            if (await ReadNoise(ct).ConfigureAwait(false) is not { } response) return new(CommandStatus.Timeout, "Write was not confirmed by a fresh device readback.");
            var actual = Store.Current.NoiseControl?.Value;
            return actual?.Mode == target.Mode && (target.LevelId is null || actual.LevelId == target.LevelId)
                ? CommandResult.Ok : new(CommandStatus.Rejected, "Device readback differs from requested mode.");
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one serialized command; cancellation is reported, not thrown. A success only counts while this
    /// device is still the active one.
    /// </summary>
    private Task<CommandResult> Exclusive(string pendingKey, Func<CancellationToken, Task<CommandResult>> body, CancellationToken cancellationToken) =>
        RunCommandAsync(pendingKey, _commands, async ct =>
        {
            var result = await body(ct).ConfigureAwait(false);
            return Store.Current.Device?.DeviceId == DeviceId || result.Status != CommandStatus.Success ? result : CommandResult.NotConnected;
        }, cancellationToken);

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        foreach (var frame in _sony.Feed(data))
        {
            if (frame.Type == 1 && frame.Payload.Length == 0)
            { lock (_gate) { if (frame.Sequence == _ackSequence) _ack?.TrySetResult(); } continue; }
            // Official DataType.ackRequired / Bl.f also ACK common, table-2 and
            // unimplemented DATA messages. Ignoring them stalls the peer's send queue.
            bool ackRequired = frame.Type is 0 or 2 or 7 or 9 or 10 or 12 or 13 or 14 or 39 or 45;
            bool shot = frame.Type is 16 or 18 or 25 or 26 or 28 or 29 or 30;
            if (!ackRequired && !shot) continue;
            if (ackRequired && !WriteAsync(SonyFrameCodec.Encode(1, (byte)(frame.Sequence ^ 1), []), CancellationToken.None).GetAwaiter().GetResult()) continue;
            if (_receivedSequence == frame.Sequence) continue;
            _receivedSequence = frame.Sequence;
            // Only table-1 payloads are parsed; other tables can reuse opcode numbers.
            if (frame.Type is 12 or 28 && frame.Payload.Length >= 2) Handle(frame.Payload);
        }
    }

    private void Handle(byte[] p)
    {
        if (p.Length < 2) return;
        bool valid = HandleSetting(p);
        if (p[0] is 0x57 or 0x59 && p[1] == _eqType && SonyEqualizer.TryParameter(p, out var preset, out var bands, !_v2))
        {
            // Keep the band values of the active preset so a curve changed on the phone shows up as stored.
            Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState($"sony-{preset}", preset, bands), Now, StateSource.QueryResponse) });
            valid = true;
        }
        else if ((!_v2 && p[0] is 0x11 or 0x13) || (_v2 && p[0] is 0x23 or 0x25))
        {
            // Subtype 9 is the extra "second unit" layout the earbuds push on connect; on a headset
            // it carries zeros and would otherwise overwrite the real (single) battery level with 0.
            if (p[1] == 9 && _profile.Form != DeviceForm.Earbuds) valid = true;
            else if (p[1] is 1 or 9 && p.Length >= 6) { Battery(BatteryComponent.Left, p[2], p[3]); Battery(BatteryComponent.Right, p[4], p[5]); valid = true; }
            else if (p[1] is 0 or 2 or 8 or 10 && p.Length >= 4) { Battery(p[1] is 2 or 10 ? BatteryComponent.Case : BatteryComponent.Left, p[2], p[3]); valid = true; }
        }
        else if (p[0] is 0x67 or 0x69 && p[1] == (_v2 ? _noiseType : 2) && TryReadNoise(p, out var mode, out bool voice, out int level))
        {
            Noise(mode, voice, level);
            valid = true;
        }
        else if (p[0] is 0x01 or 0x05 or 0x07 or 0x51) valid = true;
        if (valid) Store.TryUpdate(DeviceId, s => s with { LastResponseAt = Now, LastUpdateSource = StateSource.QueryResponse });
        // Matching command and inquired type complete the request; the caller validates the layout.
        lock (_gate)
            if (p[0] == _expected && p[1] == _subtype) _pending?.TrySetResult(p);
    }

    private void Noise(NoiseControlMode mode, bool voice, int level)
    {
        if (level > 0) _ambientLevel = level;
        string? levelId = mode == NoiseControlMode.Transparency ? voice ? SonyAkgProfile.SonyVoice : SonyAkgProfile.SonyAmbient : null;
        Store.TryUpdate(DeviceId, s => s with
        {
            NoiseControl = Observed.From(new NoiseControlState(mode, levelId), Now, StateSource.QueryResponse),
            // The ambient level only applies (and v1 only reports it) while ambient sound is on.
            Options = mode == NoiseControlMode.Transparency && level > 0
                ? s.Options.SetItem(AmbientLevelOption, Observed.From(new DeviceOption(AmbientLevelOption, "SonyOption_AmbientLevel", level, [], 0)
                    { Minimum = 1, Maximum = SonyV2Noise.MaximumAmbientLevel }, Now, StateSource.QueryResponse))
                : s.Options.Remove(AmbientLevelOption),
        });
    }

    private void Battery(BatteryComponent component, byte level, byte charging)
    {
        if (level > 100) return;
        Store.TryUpdate(DeviceId, s => s with { Battery = s.Battery.With(component, Observed.From(new BatteryReading(level, charging == 1), Now, StateSource.QueryResponse)) });
    }
}
