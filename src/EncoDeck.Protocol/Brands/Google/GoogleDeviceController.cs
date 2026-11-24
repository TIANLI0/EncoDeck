// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// Session flow follows HeyBuds' GoogleSession; Maestro RPC facts from PixelBudsMacOS (MIT) and pbpctrl (Apache-2.0 OR MIT).
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Google;

/// <summary>
/// Controller for Pixel Buds Pro / Pro 2 over the Maestro RFCOMM service. Every unary RPC is matched
/// by route, service, method and a never-reused call id. Battery arrives only on the
/// SubscribeRuntimeInfo server stream; noise and EQ are read with ReadSetting, and writes are
/// confirmed only by a fresh read-back, never by the WriteSetting response.
/// </summary>
internal sealed class GoogleDeviceController : BrandControllerBase
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ResubscribeAfter = TimeSpan.FromSeconds(30), BatteryExpiresAfter = TimeSpan.FromSeconds(120);
    private static readonly uint GetSoftwareInfo = GoogleMaestro.Hash("GetSoftwareInfo"), ReadSetting = GoogleMaestro.Hash("ReadSetting"),
        WriteSetting = GoogleMaestro.Hash("WriteSetting"), SubscribeRuntimeInfo = GoogleMaestro.Hash("SubscribeRuntimeInfo"),
        SubscribeToSettingsChanges = GoogleMaestro.Hash("SubscribeToSettingsChanges");
    private static readonly byte[] ReadNoisePayload = ProtoFields.Encode(4, new ProtoValue.UInt(GoogleMaestro.NoiseSetting));
    private static readonly byte[] ReadEqualizerPayload = ProtoFields.Encode(4, new ProtoValue.UInt(GoogleMaestro.EqualizerSetting));

    private readonly GoogleFrameCodec _codec = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Dictionary<uint, uint> _streams = [];
    private readonly TimeSpan _requestTimeout;
    private readonly CancellationTokenSource _pollStop = new();
    private Task? _pollTask;
    private TaskCompletionSource<GoogleRpcFrame>? _pending;
    private Func<GoogleRpcFrame, bool>? _match;
    private GoogleProfile _profile;
    private uint _callId = (uint)Random.Shared.Next(1, 1_000_001);
    private uint? _route;
    private bool _equalizerReadable;
    private DateTimeOffset _lastBattery = DateTimeOffset.MinValue;

    internal GoogleDeviceController(IDuplexByteChannel channel, ControllerContext context, GoogleProfile profile, TimeSpan? requestTimeout = null)
        : base(channel, context)
    {
        _profile = profile.With(noise: false, equalizer: false);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(3);
    }

    private sealed record Reply(GoogleRpcFrame? Frame, CommandResult Result);

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        Store.TryUpdate(DeviceId, s => s with { Profile = _profile, NoiseControl = null, Equalizer = null });
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Probe only the documented logical routes, and only with reads: never write a setting
            // merely to find out which earbud answers.
            foreach (uint candidate in GoogleMaestro.Candidates)
            {
                if (Closed.IsCompleted) return false;
                var info = await RpcAsync(GetSoftwareInfo, [], candidate, ProbeTimeout, probe: true, cancellationToken).ConfigureAwait(false);
                if (info.Frame is null || GoogleMaestro.Firmware(info.Frame.Payload) is not { } version) continue;
                var setting = await RpcAsync(ReadSetting, ReadNoisePayload, candidate, ProbeTimeout, probe: true, cancellationToken).ConfigureAwait(false);
                if (setting.Frame is null || GoogleMaestro.Noise(setting.Frame.Payload) is not { } noise) continue;
                lock (_gate) _route = candidate;
                Logger.LogInformation("Maestro route {Route} confirmed", candidate);
                Store.TryUpdate(DeviceId, s => s with
                {
                    Firmware = Observed.From(new FirmwareInfo(version, null, new Dictionary<string, string>()), Now, StateSource.QueryResponse),
                });
                ApplyNoise(noise, StateSource.QueryResponse);
                break;
            }
            if (_route is null)
            {
                Logger.LogInformation("No Maestro route answered both the software and noise reads");
                return false;
            }
            // EQ is optional; only a timeout (which ends the session) stops the start-up.
            await ReadEqualizerAsync(cancellationToken).ConfigureAwait(false);
            if (Closed.IsCompleted) return false;
            if (!(await RefreshCoreAsync(cancellationToken).ConfigureAwait(false)).IsSuccess) return false;
        }
        finally
        {
            _operations.Release();
        }
        _pollTask ??= Task.Run(() => PollAsync(_pollStop.Token), CancellationToken.None);
        return true;
    }

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        IReadOnlyList<GoogleRpcFrame> frames;
        lock (_gate) frames = _codec.Feed(data);
        foreach (var frame in frames)
        {
            HandleStream(frame);
            TaskCompletionSource<GoogleRpcFrame>? pending = null;
            lock (_gate)
                if (_match?.Invoke(frame) == true) pending = _pending;
            pending?.TrySetResult(frame);
        }
    }

    /// <summary>Server-stream packets update state only for a live subscription of this route.</summary>
    private void HandleStream(GoogleRpcFrame frame)
    {
        lock (_gate)
        {
            if (frame.Channel != _route || frame.Service != GoogleMaestro.ServiceId
                || !_streams.TryGetValue(frame.Call, out uint method) || method != frame.Method) return;
            if (frame.Type is 1 or 5)
            {
                _streams.Remove(frame.Call);
                return;
            }
        }
        if (frame.Type != 7 || frame.Status != 0) return;
        Touch();
        if (frame.Method == SubscribeRuntimeInfo)
        {
            if (GoogleMaestro.Runtime(frame.Payload) is not { } battery) return;
            _lastBattery = Now;
            Observed<BatteryReading>? Read(BatteryReading? value) => value is null ? null : Observed.From(value, Now, StateSource.DeviceNotification);
            // A runtime report is a snapshot: a component it omits is unknown, not the old reading.
            Store.TryUpdate(DeviceId, s => s with
            {
                Battery = new BatteryStatus(Read(battery.Left), Read(battery.Right), Read(battery.Case)),
                LastUpdateSource = StateSource.DeviceNotification,
            });
        }
        else if (frame.Method == SubscribeToSettingsChanges)
        {
            if (GoogleMaestro.Noise(frame.Payload) is { } noise) ApplyNoise(noise, StateSource.DeviceNotification);
            if (GoogleMaestro.Equalizer(frame.Payload) is { } bands) ApplyEqualizer(bands, StateSource.DeviceNotification);
        }
    }

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => RunExclusiveAsync(_operations, RefreshCoreAsync, cancellationToken);

    private async Task<CommandResult> RefreshCoreAsync(CancellationToken ct)
    {
        var noise = await ReadNoiseAsync(ct).ConfigureAwait(false);
        if (noise.Result.Status != CommandStatus.Success) return noise.Result;
        if (_equalizerReadable)
        {
            var eq = await ReadEqualizerAsync(ct).ConfigureAwait(false);
            if (eq.Result.Status != CommandStatus.Success) return eq.Result;
        }
        uint route = _route!.Value;
        if (Now - _lastBattery >= ResubscribeAfter)
        {
            // Cancel a quiet runtime stream (Pigweed CLIENT_ERROR, status CANCELLED) and subscribe
            // again for a fresh snapshot. Late packets keep the old call id and are ignored.
            uint[] stale;
            lock (_gate)
            {
                stale = [.. _streams.Where(s => s.Value == SubscribeRuntimeInfo).Select(s => s.Key)];
                foreach (uint id in stale) _streams.Remove(id);
            }
            foreach (uint id in stale)
            {
                if (!await WriteAsync(GoogleFrameCodec.Encode(new GoogleRpcFrame(route, SubscribeRuntimeInfo, id, type: 4, status: 1)), ct).ConfigureAwait(false))
                    return new(CommandStatus.TransportError);
            }
        }
        var runtime = await SubscribeAsync(SubscribeRuntimeInfo, firstSnapshot: true, ct).ConfigureAwait(false);
        if (!runtime.IsSuccess) return runtime;
        var settings = await SubscribeAsync(SubscribeToSettingsChanges, firstSnapshot: false, ct).ConfigureAwait(false);
        if (!settings.IsSuccess) return settings;
        if (Now - _lastBattery >= BatteryExpiresAfter)
            Store.TryUpdate(DeviceId, s => s with { Battery = BatteryStatus.Unknown });
        return CommandResult.Ok;
    }

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (target.LevelId is not null || target.Mode is not (NoiseControlMode.Off or NoiseControlMode.NoiseCancellation or NoiseControlMode.Transparency))
            return Task.FromResult(NotSupported($"Noise mode {target.Mode}/{target.LevelId}"));
        return RunCommandAsync(PendingKeys.NoiseControl, _operations, async ct =>
        {
            if (!_profile.NoiseConfirmed) return NotSupported("Noise control has not been confirmed by this device.");
            var write = await RpcAsync(WriteSetting, GoogleMaestro.NoiseWrite(target.Mode), null, _requestTimeout, probe: false, ct).ConfigureAwait(false);
            if (write.Frame is null) return write.Result;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                var fresh = await ReadNoiseAsync(ct).ConfigureAwait(false);
                if (fresh.Result.Status != CommandStatus.Success) return fresh.Result;
                if (Store.Current.NoiseControl?.Value.Mode == target.Mode) return CommandResult.Ok;
                if (attempt < 5) await Task.Delay(TimeSpan.FromMilliseconds(200), Context.Time, ct).ConfigureAwait(false);
            }
            return new CommandResult(CommandStatus.Rejected, "Device reports a different noise mode.");
        }, cancellationToken);
    }

    public override Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        if (!int.TryParse(presetId, out int id) || id < 0 || id >= GoogleMaestro.Presets.Length)
            return Task.FromResult(NotSupported($"EQ preset {presetId}"));
        return RunCommandAsync(PendingKeys.Equalizer, _operations, async ct =>
        {
            if (!_profile.EqualizerConfirmed) return NotSupported("EQ has not been confirmed by this device.");
            var write = await RpcAsync(WriteSetting, GoogleMaestro.EqualizerWrite(GoogleMaestro.Presets[id]), null, _requestTimeout, probe: false, ct).ConfigureAwait(false);
            if (write.Frame is null) return write.Result;
            var fresh = await ReadEqualizerAsync(ct).ConfigureAwait(false);
            if (fresh.Result.Status != CommandStatus.Success) return fresh.Result;
            return Store.Current.Equalizer?.Value.RawPresetValue == id
                ? CommandResult.Ok
                : new CommandResult(CommandStatus.Rejected, "Device reports a different EQ curve.");
        }, cancellationToken);
    }

    private async Task<Reply> ReadNoiseAsync(CancellationToken ct)
    {
        var reply = await RpcAsync(ReadSetting, ReadNoisePayload, null, _requestTimeout, probe: false, ct).ConfigureAwait(false);
        if (reply.Frame is null) return reply;
        if (GoogleMaestro.Noise(reply.Frame.Payload) is not { } mode)
        {
            // A malformed or absent field withdraws the control rather than confirming a cached value.
            SetProfile(p => p.With(noise: false));
            Store.TryUpdate(DeviceId, s => s with { NoiseControl = null });
            return reply with { Result = new(CommandStatus.Rejected, "Malformed noise-control read-back.") };
        }
        ApplyNoise(mode, StateSource.QueryResponse);
        return reply;
    }

    private async Task<Reply> ReadEqualizerAsync(CancellationToken ct)
    {
        var reply = await RpcAsync(ReadSetting, ReadEqualizerPayload, null, _requestTimeout, probe: false, ct).ConfigureAwait(false);
        if (reply.Frame is null) return reply;
        if (GoogleMaestro.Equalizer(reply.Frame.Payload) is not { } bands)
        {
            SetProfile(p => p.With(equalizer: false));
            Store.TryUpdate(DeviceId, s => s with { Equalizer = null });
            return reply with { Result = new(CommandStatus.Rejected, "Malformed EQ read-back.") };
        }
        _equalizerReadable = true;
        ApplyEqualizer(bands, StateSource.QueryResponse);
        return reply;
    }

    private void ApplyNoise(NoiseControlMode mode, StateSource source)
    {
        SetProfile(p => p.With(noise: true));
        Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(new NoiseControlState(mode), Now, source), LastUpdateSource = source });
    }

    /// <summary>A curve matching no preset is a custom EQ: shown as known, with no preset selected.</summary>
    private void ApplyEqualizer(float[] bands, StateSource source)
    {
        int? preset = GoogleMaestro.Preset(bands);
        SetProfile(p => p.With(equalizer: true));
        Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState(preset?.ToString(), preset), Now, source), LastUpdateSource = source });
    }

    private void SetProfile(Func<GoogleProfile, GoogleProfile> change)
    {
        GoogleProfile next;
        lock (_gate)
        {
            next = change(_profile);
            if (next.NoiseConfirmed == _profile.NoiseConfirmed && next.EqualizerConfirmed == _profile.EqualizerConfirmed) return;
            _profile = next;
        }
        Store.TryUpdate(DeviceId, s => s with { Profile = next });
    }

    private async Task<CommandResult> SubscribeAsync(uint method, bool firstSnapshot, CancellationToken ct)
    {
        uint route = _route!.Value, id;
        lock (_gate)
        {
            if (_streams.ContainsValue(method)) return CommandResult.Ok;
            id = NextCall();
            _streams[id] = method;
        }
        var request = GoogleFrameCodec.Encode(new GoogleRpcFrame(route, method, id));
        if (!firstSnapshot)
        {
            if (await WriteAsync(request, ct).ConfigureAwait(false)) return CommandResult.Ok;
            lock (_gate) _streams.Remove(id);
            return new(CommandStatus.TransportError);
        }
        var reply = await ExchangeAsync(request, f => Identifies(f, route, method, id) && f.Type is 1 or 5 or 7, _requestTimeout, probe: false, ct).ConfigureAwait(false);
        if (reply.Frame is { Type: 7, Status: 0 }) return CommandResult.Ok;
        lock (_gate) _streams.Remove(id);
        return reply.Frame is null ? reply.Result : new(CommandStatus.Rejected, "The earbuds refused the runtime subscription.");
    }

    /// <summary>One unary RPC. A response with an error type or nonzero status fails the call.</summary>
    private async Task<Reply> RpcAsync(uint method, byte[] payload, uint? selected, TimeSpan timeout, bool probe, CancellationToken ct)
    {
        uint route, id;
        lock (_gate)
        {
            if ((selected ?? _route) is not { } r) return new(null, CommandResult.NotConnected);
            route = r;
            id = NextCall();
        }
        var reply = await ExchangeAsync(GoogleFrameCodec.Encode(new GoogleRpcFrame(route, method, id, payload)),
            f => Identifies(f, route, method, id) && f.Type is 1 or 5, timeout, probe, ct).ConfigureAwait(false);
        if (reply.Frame is { Type: 1, Status: 0 }) return reply;
        return reply.Frame is null ? reply : new(null, new(CommandStatus.Rejected, $"Maestro RPC error (status {reply.Frame.Status})."));
    }

    private static bool Identifies(GoogleRpcFrame frame, uint route, uint method, uint id) =>
        frame.Channel == route && frame.Service == GoogleMaestro.ServiceId && frame.Method == method && frame.Call == id;

    private uint NextCall() => _callId = _callId == uint.MaxValue ? 1 : _callId + 1;

    private async Task<Reply> ExchangeAsync(byte[] request, Func<GoogleRpcFrame, bool> match, TimeSpan timeout, bool probe, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<GoogleRpcFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending = completion;
            _match = match;
        }
        try
        {
            if (!await WriteAsync(request, ct).ConfigureAwait(false)) return new(null, new(CommandStatus.TransportError));
            try
            {
                await Task.WhenAny(completion.Task, Closed).WaitAsync(timeout, Context.Time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A read probe may miss; any other unanswered call retires the session.
                if (!probe)
                {
                    Logger.LogInformation("Maestro call timed out; ending the control session");
                    EndSession();
                }
                return new(null, new(CommandStatus.Timeout, "The earbuds did not answer the Maestro call."));
            }
            return completion.Task.IsCompletedSuccessfully
                ? new(await completion.Task.ConfigureAwait(false), CommandResult.Ok)
                : new(null, CommandResult.NotConnected);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, completion))
                {
                    _pending = null;
                    _match = null;
                }
            }
        }
    }

    /// <summary>Commands need the Maestro route confirmed at start-up.</summary>
    protected override bool CanRunCommands => base.CanRunCommands && _route is not null;

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
            Logger.LogDebug(ex, "Pixel Buds polling stopped");
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
