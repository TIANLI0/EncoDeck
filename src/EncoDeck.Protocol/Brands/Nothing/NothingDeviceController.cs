// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// Session rules follow HeyBuds' NothingSession; protocol references are attributed in its docs/licenses/Nothing.md.
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Nothing;

/// <summary>
/// Nothing / CMF controller over the dedicated RFCOMM service: identity, firmware, battery, noise
/// control and EQ presets. Only replies correlated by command and sequence update state;
/// acknowledgements and unsolicited events never establish a capability or confirm a write.
/// </summary>
internal sealed class NothingDeviceController : BrandControllerBase
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadbackDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>A sequence is never reused within a connection; the session ends before the byte wraps.</summary>
    internal const int MaxSequence = 250;

    private readonly NothingModel _model;
    private readonly NothingFrameDecoder _decoder;
    private readonly TimeSpan _timeout;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _pollStop = new();
    private Task? _pollTask;
    private NothingProfile _profile;
    private TaskCompletionSource<NothingFrame>? _pending;
    private ushort _pendingCommand;
    private byte _pendingSequence;
    private int _sequence;
    private string? _firmware;
    private bool _noiseAvailable, _noiseObserved, _eqAvailable, _eqObserved;

    public NothingDeviceController(IDuplexByteChannel channel, ControllerContext context, NothingProfile profile, TimeSpan? timeout = null)
        : base(channel, context)
    {
        _profile = profile;
        _model = profile.Model;
        _decoder = new NothingFrameDecoder(_model);
        _timeout = timeout ?? DefaultTimeout;
    }

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        _profile = _profile.WithConfirmed(false, false);
        Store.TryUpdate(DeviceId, s => s with { Profile = _profile, NoiseControl = null, Equalizer = null });

        // The name only selected a candidate; the device must report the same model code.
        var identity = await ReadAsync(_model.IdentityRead, false, cancellationToken).ConfigureAwait(false);
        if (identity is null || _model.IdentityCode(identity.Payload) != _model.ModelCode)
        {
            Logger.LogInformation("Nothing identity did not confirm {Model}", _model.ModelCode);
            return false;
        }

        // Firmware is needed for the CMF Buds 2 EQ gate; its absence never enables EQ.
        var firmware = await ReadAsync(_model.FirmwareRead, true, cancellationToken).ConfigureAwait(false);
        if (Closed.IsCompleted) return false;
        if (firmware is not null && NothingModel.ParseFirmware(firmware.Payload) is { } version)
        {
            _firmware = version;
            Store.TryUpdate(DeviceId, s => s with
            {
                Firmware = Observed.From(new FirmwareInfo(version, _model.ModelCode, new Dictionary<string, string>()), Now, StateSource.QueryResponse),
            });
        }

        if (!await ReadBatteryAsync(cancellationToken).ConfigureAwait(false)) return false;
        _noiseAvailable = await ReadNoiseAsync(true, cancellationToken).ConfigureAwait(false) is not null;
        if (Closed.IsCompleted) return false;
        if (_model.AllowsEq(_firmware))
        {
            var (id, _) = await ReadEqAsync(true, cancellationToken).ConfigureAwait(false);
            if (Closed.IsCompleted) return false;
            _eqAvailable = id is not null;
            if (!_eqAvailable) ClearEq();
        }
        PublishProfile();
        _pollTask ??= Task.Run(() => PollAsync(_pollStop.Token), CancellationToken.None);
        return true;
    }

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        IReadOnlyList<NothingFrame> frames;
        lock (_gate) frames = _decoder.Feed(data);
        foreach (var frame in frames)
        {
            Touch();
            lock (_gate)
            {
                if (_pending is not null && frame.Command == _pendingCommand && frame.Sequence == _pendingSequence)
                    _pending.TrySetResult(frame);
            }
        }
    }

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => RunExclusiveAsync(_operations, async ct =>
    {
        bool ok = await ReadBatteryAsync(ct).ConfigureAwait(false)
            && (!_noiseAvailable || await ReadNoiseAsync(false, ct).ConfigureAwait(false) is not null);
        if (ok && _eqAvailable)
        {
            // An invalid EQ reply (or active Advanced EQ) only withdraws the presets.
            var (id, invalid) = await ReadEqAsync(false, ct).ConfigureAwait(false);
            ok = id is not null || invalid;
        }
        if (ok) return CommandResult.Ok;
        // A failed refresh must not leave cached state presented as current.
        EndSession();
        return TimedOut;
    }, cancellationToken);

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!_profile.NoiseControlOptions.Any(o => o.Matches(target))) return Task.FromResult(NotSupported("Noise control"));
        byte value = target.Mode switch { NoiseControlMode.Off => 5, NoiseControlMode.NoiseCancellation => 1, _ => 7 };
        return RunCommandAsync(PendingKeys.NoiseControl, _operations, async ct =>
        {
            if (!_noiseAvailable || !_noiseObserved) return NotSupported("Noise control has not been read back from this device.");
            if (!await SendAsync(NothingModel.NoiseWrite(value)!, ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
            // The ACK arrives first and proves nothing; only a fresh read with a new sequence confirms.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                var fresh = await ReadNoiseAsync(false, ct).ConfigureAwait(false);
                if (fresh is null) return Closed.IsCompleted ? TimedOut : Malformed;
                if (fresh == target) return CommandResult.Ok;
                if (attempt < 5) await Task.Delay(ReadbackDelay, Context.Time, ct).ConfigureAwait(false);
            }
            return new(CommandStatus.Rejected, "The earbuds did not confirm the requested noise mode.");
        }, cancellationToken);
    }

    public override Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        if (!_profile.EqualizerPresets.Any(p => p.Id == presetId) || !byte.TryParse(presetId, out byte id) || _model.EqWrite(id) is not { } write)
            return Task.FromResult(NotSupported("EQ preset has not been read back from this device."));
        return RunCommandAsync(PendingKeys.Equalizer, _operations, async ct =>
        {
            if (!_eqAvailable || !_eqObserved) return NotSupported("EQ has not been read back from this device.");
            // Fresh Advanced EQ gate before changing a preset (Headphone (1)).
            var (before, invalid) = await ReadEqAsync(false, ct).ConfigureAwait(false);
            if (before is null) return invalid ? NotSupported("Advanced EQ is active or the preset reply was invalid.") : TimedOut;
            if (!await SendAsync(write, ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                var (fresh, bad) = await ReadEqAsync(false, ct).ConfigureAwait(false);
                if (fresh is null) return bad ? Malformed : TimedOut;
                if (fresh == id) return CommandResult.Ok;
                if (attempt < 5) await Task.Delay(ReadbackDelay, Context.Time, ct).ConfigureAwait(false);
            }
            return new(CommandStatus.Rejected, "The earbuds did not confirm the requested EQ preset.");
        }, cancellationToken);
    }

    private static CommandResult TimedOut => new(CommandStatus.Timeout, "The device did not return a valid control response.");

    private static CommandResult Malformed => new(CommandStatus.Rejected, "The device returned a malformed setting response.");

    private async Task<bool> ReadBatteryAsync(CancellationToken ct)
    {
        var frame = await ReadAsync(_model.BatteryRead, false, ct).ConfigureAwait(false);
        if (frame is null) return false;
        if (_model.ParseBattery(frame.Payload) is not { } readings)
        {
            Store.TryUpdate(DeviceId, s => s with { Battery = BatteryStatus.Unknown });
            return false;
        }
        // A complete report replaces the previous one: absent components become unknown.
        Observed<BatteryReading>? Get(BatteryComponent component) =>
            readings.TryGetValue(component, out var reading) ? Observed.From(reading, Now, StateSource.QueryResponse) : null;
        var battery = new BatteryStatus(Get(BatteryComponent.Left), Get(BatteryComponent.Right), Get(BatteryComponent.Case));
        Store.TryUpdate(DeviceId, s => s with { Battery = battery, LastUpdateSource = StateSource.QueryResponse });
        return true;
    }

    /// <summary>Reads the noise mode; null on a missing or malformed reply (the latter withdraws noise control).</summary>
    private async Task<NoiseControlState?> ReadNoiseAsync(bool probing, CancellationToken ct)
    {
        var frame = await ReadAsync(_model.NoiseRead, probing, ct).ConfigureAwait(false);
        if (frame is null) return null;
        NoiseControlState? state = _model.ParseNoise(frame.Payload) switch
        {
            1 => new(NoiseControlMode.NoiseCancellation),
            2 => new(NoiseControlMode.NoiseCancellation, "medium"),
            3 => new(NoiseControlMode.NoiseCancellation, "low"),
            4 => new(NoiseControlMode.Adaptive),
            5 => new(NoiseControlMode.Off),
            7 => new(NoiseControlMode.Transparency),
            _ => null,
        };
        _noiseObserved = state is not null;
        Store.TryUpdate(DeviceId, s => s with { NoiseControl = state is null ? null : Observed.From(state, Now, StateSource.QueryResponse) });
        PublishProfile();
        return state;
    }

    /// <summary>
    /// Reads the preset, then (Headphone (1)) whether Advanced EQ overrides it. Returns the preset,
    /// or Invalid when the reply was malformed or Advanced EQ is active; neither is ever changed here.
    /// </summary>
    private async Task<(byte? Id, bool Invalid)> ReadEqAsync(bool probing, CancellationToken ct)
    {
        if (!_model.AllowsEq(_firmware)) return (null, true);
        var frame = await ReadAsync(_model.EqRead, probing, ct).ConfigureAwait(false);
        if (frame is null) return (null, false);
        if (_model.ParseEq(frame.Payload) is not { } id)
        {
            ClearEq();
            return (null, true);
        }
        if (_model.AdvancedEqRead is { } advancedRead)
        {
            var advanced = await ReadAsync(advancedRead, probing, ct).ConfigureAwait(false);
            if (advanced is null) return (null, false);
            if (_model.ParseAdvancedEq(advanced.Payload) is not false)
            {
                ClearEq();
                return (null, true);
            }
        }
        _eqObserved = true;
        Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState(id.ToString(), id), Now, StateSource.QueryResponse) });
        PublishProfile();
        return (id, false);
    }

    private void ClearEq()
    {
        _eqObserved = false;
        Store.TryUpdate(DeviceId, s => s with { Equalizer = null });
        PublishProfile();
    }

    private void PublishProfile()
    {
        var next = _profile.WithConfirmed(_noiseAvailable && _noiseObserved, _eqAvailable && _eqObserved);
        if (ReferenceEquals(next, _profile) && Store.Current.Profile == _profile) return;
        _profile = next;
        Store.TryUpdate(DeviceId, s => s with { Profile = next });
    }

    private bool TryNextSequence(out byte sequence)
    {
        sequence = 0;
        if (Closed.IsCompleted) return false;
        if (_sequence >= MaxSequence)
        {
            Logger.LogInformation("Nothing sequence space used up; reconnecting");
            EndSession();
            return false;
        }
        sequence = (byte)++_sequence;
        return true;
    }

    /// <summary>
    /// Sends a read and waits for the reply with its command and sequence. A probe that gets no reply
    /// returns null and keeps the session (the decoder drops any truncated reply); any other timeout
    /// ends the session, as does cancelling an exchange in flight.
    /// </summary>
    private async Task<NothingFrame?> ReadAsync(NothingRequest request, bool probing, CancellationToken ct)
    {
        if (!TryNextSequence(out byte sequence)) return null;
        var completion = new TaskCompletionSource<NothingFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending = completion;
            _pendingCommand = request.ResponseCommand!.Value;
            _pendingSequence = sequence;
        }
        try
        {
            if (!await WriteAsync(NothingFrameCodec.Encode(request, sequence), ct).ConfigureAwait(false))
            {
                EndSession();
                return null;
            }
            var winner = await Task.WhenAny(completion.Task, Closed, Task.Delay(_timeout, Context.Time, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (winner == completion.Task) return await completion.Task.ConfigureAwait(false);
            if (Closed.IsCompleted) return null;
            if (probing)
            {
                // A truncated optional reply must not hold the next request behind its declared length.
                lock (_gate) _decoder.Reset();
                return null;
            }
            Logger.LogInformation("Nothing request {Command:X4} timed out; ending session", request.Command);
            EndSession();
            return null;
        }
        catch (OperationCanceledException)
        {
            EndSession();
            throw;
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_pending, completion)) _pending = null;
        }
    }

    private async Task<bool> SendAsync(NothingRequest request, CancellationToken ct)
    {
        if (!TryNextSequence(out byte sequence)) return false;
        try
        {
            return await WriteAsync(NothingFrameCodec.Encode(request, sequence), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EndSession();
            throw;
        }
    }

    protected override bool CanRunCommands => base.CanRunCommands && Store.Current.Device?.DeviceId == DeviceId;

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval, Context.Time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false) && !Closed.IsCompleted)
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Nothing polling stopped");
        }
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _pollStop.CancelAsync().ConfigureAwait(false);
        if (_pollTask is { } poll)
        {
            try
            {
                await poll.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        _pollStop.Dispose();
    }
}
