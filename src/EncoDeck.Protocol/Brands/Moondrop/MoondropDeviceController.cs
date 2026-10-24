// SPDX-License-Identifier: GPL-3.0-only
// Protocol adapted from huime180/MiuixMoondrop, GPL-3.0-or-later.
using System.Buffers.Binary;
using System.Text;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Moondrop;

/// <summary>GAIA device settings, serialized and confirmed by device read-back.</summary>
public sealed partial class MoondropDeviceController : BrandControllerBase
{
    private readonly bool _rfcomm;
    private bool _usesHeader = true;
    private readonly ITimer _burstTimer;
    private readonly GaiaFrameDecoder _decoder = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    // Cached: in-flight requests may outlive DisposeCoreAsync, and a disposed source's Token throws.
    private readonly CancellationToken _stopToken;
    private (int Feature, int Command, TaskCompletionSource<GaiaFrame> Completion)? _pending;
    private MoondropProfile _profile;
    private byte[] _batteries = [0, 1, 2, 3];
    private readonly bool[] _explicitBattery = new bool[2];
    private Task? _poll;

    public MoondropDeviceController(IDuplexByteChannel channel, ControllerContext context, MoondropProfile profile)
        : base(channel, context)
    {
        _rfcomm = channel.Description.StartsWith("RFCOMM", StringComparison.OrdinalIgnoreCase);
        _profile = profile;
        _stopToken = _stop.Token;
        // Bare RFCOMM has no message boundaries; variable-length responses require a quiet burst.
        _burstTimer = Context.Time.CreateTimer(_ => Receive([], true), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private async Task NegotiateRfcommAsync(CancellationToken ct)
    {
        // EDGE answers the framed BASIC application-version query without a CSR API probe.
        if (_profile.RfcommChannel is not null && await RequestAsync(0, 5, [], ct).ConfigureAwait(false) is { } version)
        {
            Apply(version, StateSource.QueryResponse);
            return;
        }
        // Upstream's read-only CSR API-version probe: official framing first, bare PDU second.
        if (await RequestAsync(-1, 0, [], ct).ConfigureAwait(false) is not null) return;
        _usesHeader = false;
        lock (_gate) _decoder.Reset();
        if (await RequestAsync(-1, 0, [], ct).ConfigureAwait(false) is not null) return;
        _usesHeader = true; // Inconclusive probes never select raw framing.
        lock (_gate) _decoder.Reset();
    }

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        if (_rfcomm) await NegotiateRfcommAsync(cancellationToken).ConfigureAwait(false);
        var features = new HashSet<int>();
        bool complete = false;
        for (int page = 0; page < 32; page++)
        {
            var response = await RequestAsync(0, page == 0 ? 1 : 2, [], cancellationToken).ConfigureAwait(false);
            if (response is null) break;
            byte[] p = response.Payload;
            if (p.Length >= 3 && p.Length % 2 == 1 && p[0] <= 1)
            {
                for (int i = 1; i < p.Length; i += 2) features.Add(p[i]);
                if (p[0] == 0) { complete = true; break; }
            }
            else if (p.Length > 0 && p.Length % 4 == 0)
            {
                for (int offset = 0; offset < p.Length; offset += 4)
                {
                    uint word = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(offset));
                    for (int bit = 0; bit < 32; bit++)
                        if ((word & (1u << bit)) != 0) features.Add(offset * 8 + bit);
                }
                complete = true;
                break;
            }
            else break;
        }
        if (complete)
        {
            _profile = _profile.WithFeatures(features);
            Store.TryUpdate(DeviceId, s => s with { Profile = _profile });
        }
        var supported = await RequestAsync(13, 0, [], cancellationToken).ConfigureAwait(false);
        if (supported is { Payload.Length: > 0 } && supported.Payload.All(b => b <= 3))
            _batteries = supported.Payload.Distinct().ToArray();
        var refresh = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!complete && !refresh.IsSuccess) return false;
        _poll = PollAsync(_stopToken);
        return true;
    }

    protected override void OnReceived(ReadOnlySpan<byte> data) => Receive(data, false);

    private void Receive(ReadOnlySpan<byte> data, bool burstEnd)
    {
        IReadOnlyList<GaiaFrame> frames;
        lock (_gate)
        {
            frames = _decoder.Feed(data, _rfcomm, burstEnd);
            if (_rfcomm && !burstEnd) _burstTimer.Change(TimeSpan.FromMilliseconds(40), Timeout.InfiniteTimeSpan);
        }
        foreach (var frame in frames)
        {
            if (frame.Type is not (1 or 2 or 3)) continue;
            Touch();
            TaskCompletionSource<GaiaFrame>? completion = null;
            lock (_gate)
            {
                if (frame.Type is 2 or 3 && _pending is { } pending && pending.Feature == frame.Feature && pending.Command == frame.Command)
                    completion = pending.Completion;
            }
            if (completion is not null) completion.TrySetResult(frame);
            else if (frame.Type == 1) Apply(frame, StateSource.DeviceNotification);
        }
    }

    private async Task<GaiaFrame?> RequestAsync(int feature, int command, byte[] payload, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopToken);
        await _requests.WaitAsync(linked.Token).ConfigureAwait(false);
        var completion = new TaskCompletionSource<GaiaFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_gate)
            {
                // Never join a timed-out bare response to a different request's payload.
                if (_rfcomm && !_usesHeader) _decoder.Reset();
                _pending = (feature, command, completion);
            }
            byte[] packet = feature == -1 ? GaiaFrameCodec.EncodeVersionProbe(_rfcomm && _usesHeader)
                : GaiaFrameCodec.Encode(feature, command, payload, _rfcomm && _usesHeader);
            if (!await WriteAsync(packet, linked.Token).ConfigureAwait(false)) return null;
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), Context.Time, linked.Token).ConfigureAwait(false);
            // A GAIA error reply ends the wait at once instead of after the timeout.
            return response.Type == 3 ? null : response;
        }
        catch (TimeoutException) { return null; }
        finally
        {
            lock (_gate) _pending = null;
            _requests.Release();
        }
    }

    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private async Task<CommandResult> RefreshCoreAsync(CancellationToken ct)
    {
        bool answered = false;
        answered |= await RefreshSettingsAsync(ct).ConfigureAwait(false);
        var battery = await RequestAsync(13, 1, _batteries, ct).ConfigureAwait(false);
        if (battery is null) battery = await RequestAsync(13, 1, [], ct).ConfigureAwait(false);
        if (battery is not null) answered |= Apply(battery, StateSource.QueryResponse);
        if (_profile.AncFeature is { } anc)
        {
            var noise = await RequestAsync(anc, anc == 2 ? 1 : 3, [], ct).ConfigureAwait(false);
            if (noise is not null) answered |= Apply(noise, StateSource.QueryResponse);
        }
        if (_profile.HasFeature(16))
        {
            foreach (var toggle in new[] { DeviceToggle.HiRes, DeviceToggle.Ldac, DeviceToggle.Lc3 })
            {
                if (_profile.Toggles.Contains(toggle) && _profile.Capabilities.IsAvailable(MoondropProfile.ToggleCapability(toggle), Context.AllowExperimental())) continue;
                var (feature, get, _) = ToggleCommand(toggle);
                if (await RequestAsync(feature, get, [], ct).ConfigureAwait(false) is { Payload: [0 or 1] }
                    && (toggle != DeviceToggle.HiRes || _profile.LhdcSupport is CapabilitySupport.Supported or CapabilitySupport.Experimental))
                    _profile = _profile.WithCodec(toggle);
            }
            Store.TryUpdate(DeviceId, s => s with { Profile = _profile });
        }
        foreach (var toggle in _profile.Toggles)
        {
            if (toggle == DeviceToggle.PromptTone) continue; // Read VoiceConf once below, not a separate switch command.
            if (toggle is DeviceToggle.SpatialAudio or DeviceToggle.HeadTracking) continue;
            if (toggle is DeviceToggle.Ldac or DeviceToggle.Lc3 && !_profile.Capabilities.IsAvailable(MoondropProfile.ToggleCapability(toggle), Context.AllowExperimental())) continue;
            var (feature, get, _) = ToggleCommand(toggle);
            var frame = await RequestAsync(feature, get, [], ct).ConfigureAwait(false);
            if (frame is not null) answered |= Apply(frame, StateSource.QueryResponse);
        }
        await RefreshSpatialAsync(ct).ConfigureAwait(false);
        if (_profile.Capabilities.IsAvailable(DeviceCapability.PromptVolume, Context.AllowExperimental()))
        {
            var voice = await RequestAsync(14, 1, [], ct).ConfigureAwait(false);
            if (voice is not null) answered |= Apply(voice, StateSource.QueryResponse);
        }
        return answered ? CommandResult.Ok : new(CommandStatus.Timeout, "No valid GAIA state response.");
    }

    private bool Apply(GaiaFrame frame, StateSource source)
    {
        byte[] p = frame.Payload;
        if (frame.Feature == 0 && frame.Command == 5 && p.Length is > 0 and <= 128)
        {
            string version = Encoding.UTF8.GetString(p).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(version) || version.Any(char.IsControl)) return false;
            Store.TryUpdate(DeviceId, s => s with { Firmware = Observed.From(FirmwareInfo.Empty with { Version = version }, Now, source), LastUpdateSource = source });
            return true;
        }
        if (ApplySettings(frame, source)) return true;
        if (frame.Feature == 13 && frame.Command == 1 && p.Length >= 2 && p.Length % 2 == 0)
        {
            bool updated = false;
            Store.TryUpdate(DeviceId, s =>
            {
                var battery = s.Battery;
                // Explicit per-ear values win over a single-device fallback, regardless of pair order.
                for (int i = 0; i < p.Length; i += 2)
                {
                    if (p[i] is < 1 or > 3 || p[i + 1] > 100) continue;
                    int component = p[i] - 1;
                    if (component < 2) _explicitBattery[component] = true;
                    battery = battery.With((BatteryComponent)component, Observed.From(new BatteryReading(p[i + 1], null), Now, source));
                    updated = true;
                }
                for (int i = 0; i < p.Length; i += 2)
                {
                    if (p[i] != 0 || p[i + 1] > 100) continue;
                    var reading = Observed.From(new BatteryReading(p[i + 1], null), Now, source);
                    if (!_explicitBattery[0]) battery = battery with { Left = reading };
                    if (!_explicitBattery[1]) battery = battery with { Right = reading };
                    updated = true;
                }
                return updated ? s with { Battery = battery, LastUpdateSource = source } : s;
            });
            return updated;
        }
        if (frame.Feature == _profile.AncFeature && frame.Command == (frame.Feature == 2 ? 1 : 3) &&
            (p.Length == 1 || frame.Feature == 8 && p.Length == 4) && _profile.DecodeNoise(p[0]) is { } noise)
        {
            Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(noise, Now, source), LastUpdateSource = source });
            return true;
        }
        DeviceToggle? toggle = (frame.Feature, frame.Command) switch
        {
            (16, 1) => DeviceToggle.Lc3,
            (16, 2) => DeviceToggle.Ldac,
            (16, 5) => DeviceToggle.HiRes,
            (17, 1) => DeviceToggle.InEarDetection,
            (18, 1) => DeviceToggle.SpatialAudio,
            (18, 3) => DeviceToggle.HeadTracking,
            (20, 1) => DeviceToggle.DualDeviceConnection,
            (27, 1) => DeviceToggle.BassEngine,
            (30, 1) => DeviceToggle.ChannelSwap,
            _ => null,
        };
        if (toggle is { } t && p.Length == 1 && p[0] <= 1)
        {
            Store.TryUpdate(DeviceId, s => s with
            {
                Toggles = s.Toggles.SetItem(t, Observed.From(p[0] == 1, Now, source)),
                BassEngine = t == DeviceToggle.BassEngine ? Observed.From(new BassEngineState(0, 0, 0), Now, source) : s.BassEngine,
                LastUpdateSource = source,
            });
            if (t is DeviceToggle.SpatialAudio or DeviceToggle.HeadTracking && Store.Current.Spatial is not null)
                UpdateSpatial(source);
            return true;
        }
        if (frame.Feature == 14 && frame.Command == 1 && ValidVoice(p))
        {
            Store.TryUpdate(DeviceId, s => s with { PromptVolume = Observed.From((int)p[1], Now, source), Toggles = s.Toggles.SetItem(DeviceToggle.PromptTone, Observed.From(p[0] == 1, Now, source)), LastUpdateSource = source });
            return true;
        }
        return false;
    }

    private void UpdateSpatial(StateSource source = StateSource.QueryResponse) => Store.TryUpdate(DeviceId, s => s with
    {
        Spatial = s.Toggles.TryGetValue(DeviceToggle.SpatialAudio, out var spatial) && s.Toggles.TryGetValue(DeviceToggle.HeadTracking, out var tracking)
            ? Observed.From(!spatial.Value ? SpatialAudioMode.Off : tracking.Value ? SpatialAudioMode.HeadTracking : SpatialAudioMode.Fixed, Now, source)
            : null,
    });

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!_profile.Capabilities.IsAvailable(DeviceCapability.NoiseCancellation, Context.AllowExperimental()) ||
            _profile.AncFeature is not { } feature || !_profile.HasFeature(feature) || _profile.EncodeNoise(target) is not { } value)
            return Task.FromResult(NotSupported("Noise mode"));
        return RunWriteAsync("noise-control", async ct =>
        {
            await RequestAsync(feature, feature == 2 ? 2 : 4, [value], ct).ConfigureAwait(false);
            var actual = await RequestAsync(feature, feature == 2 ? 1 : 3, [], ct).ConfigureAwait(false);
            if (actual is null || !Apply(actual, StateSource.QueryResponse)) return new(CommandStatus.Timeout);
            return Store.Current.NoiseControl?.Value == target ? CommandResult.Ok : new(CommandStatus.Rejected, "Device reports a different noise mode.");
        }, cancellationToken);
    }

    private static (int Feature, int Get, int Set) ToggleCommand(DeviceToggle toggle) => toggle switch
    {
        DeviceToggle.HiRes => (16, 5, 6),
        DeviceToggle.Ldac => (16, 2, 4),
        DeviceToggle.Lc3 => (16, 1, 3),
        DeviceToggle.DualDeviceConnection => (20, 1, 2),
        DeviceToggle.IndicatorLight => (19, 1, 2),
        DeviceToggle.InEarDetection => (17, 1, 2),
        DeviceToggle.SpatialAudio => (18, 1, 2),
        DeviceToggle.HeadTracking => (18, 3, 4),
        DeviceToggle.BassEngine => (27, 1, 2),
        DeviceToggle.ChannelSwap => (30, 1, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(toggle)),
    };

    public override Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        if (!_profile.Toggles.Contains(toggle)) return Task.FromResult(NotSupported(toggle.ToString()));
        if (toggle == DeviceToggle.PromptTone)
            return CanWrite(DeviceCapability.PromptTone, 14) ? WriteVoiceAsync(enabled, null, cancellationToken) : Task.FromResult(NotSupported("Prompt tone"));
        var support = toggle == DeviceToggle.HiRes ? _profile.LhdcSupport
            : _profile.Capabilities.Get(MoondropProfile.ToggleCapability(toggle)).Support;
        if (support != CapabilitySupport.Supported && !(support == CapabilitySupport.Experimental && Context.AllowExperimental()))
            return Task.FromResult(NotSupported(toggle.ToString()));
        return RunWriteAsync($"toggle:{toggle}", async ct =>
        {
            if (enabled && toggle is DeviceToggle.HiRes or DeviceToggle.DualDeviceConnection)
            {
                var other = toggle == DeviceToggle.HiRes ? DeviceToggle.DualDeviceConnection : DeviceToggle.HiRes;
                if (_profile.Toggles.Contains(other))
                {
                    var off = await WriteToggleAsync(other, false, ct).ConfigureAwait(false);
                    if (!off.IsSuccess) return off;
                }
            }
            return await WriteToggleAsync(toggle, enabled, ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<CommandResult> WriteToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken ct)
    {
        var (feature, get, set) = ToggleCommand(toggle);
        await RequestAsync(feature, set, [enabled ? (byte)1 : (byte)0], ct).ConfigureAwait(false);
        var actual = await RequestAsync(feature, get, [], ct).ConfigureAwait(false);
        if (actual is null || !Apply(actual, StateSource.QueryResponse)) return new(CommandStatus.Timeout);
        return actual.Payload[0] == (enabled ? 1 : 0) ? CommandResult.Ok : new(CommandStatus.Rejected, "Device reports a different switch state.");
    }

    public override Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default)
    {
        if (level is < 0 or > 100) return Task.FromResult(new CommandResult(CommandStatus.Rejected, "Volume must be 0–100."));
        if (!CanWrite(DeviceCapability.PromptVolume, 14)) return Task.FromResult(NotSupported("Prompt volume"));
        return WriteVoiceAsync(null, level, cancellationToken);
    }

    private async Task<CommandResult> RunWriteAsync(string description, Func<CancellationToken, Task<CommandResult>> action, CancellationToken ct)
    {
        if (Closed.IsCompleted) return CommandResult.NotConnected;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopToken);
        var pending = new PendingCommand(Guid.NewGuid(), description, Now);
        bool entered = false;
        try
        {
            await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            Store.TryUpdate(DeviceId, s => s with { PendingCommands = s.PendingCommands.Add(pending), LastError = null });
            var result = await action(linked.Token).ConfigureAwait(false);
            if (!result.IsSuccess)
                Store.TryUpdate(DeviceId, s => s with { LastError = new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now) });
            return result;
        }
        catch (OperationCanceledException) { return new(CommandStatus.Cancelled); }
        finally
        {
            Store.TryUpdate(DeviceId, s => s with { PendingCommands = s.PendingCommands.RemoveAll(p => p.Id == pending.Id) });
            if (entered) _operations.Release();
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), Context.Time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false) && !Closed.IsCompleted)
                await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.LogDebug(ex, "Moondrop polling stopped"); }
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _burstTimer.DisposeAsync().ConfigureAwait(false);
        if (_poll is not null) await _poll.ConfigureAwait(false);
        _stop.Dispose();
    }
}
