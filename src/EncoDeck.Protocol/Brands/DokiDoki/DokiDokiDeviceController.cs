// SPDX-License-Identifier: GPL-3.0-only
// Session behavior (heartbeat, stop on exit, write serialization) follows Yilia src/chunfeng/manager.ts (MIT).
using EncoDeck.Core.Commands;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.DokiDoki;

/// <param name="HeartbeatInterval">Yilia and the official client keep about 2 s.</param>
/// <param name="MinActionInterval">Minimum spacing between intensity writes; superseded values are dropped.</param>
/// <param name="StopTimeout">Upper bound for one stop sequence, including the final one on close.</param>
public sealed record DokiDokiTiming(TimeSpan HeartbeatInterval, TimeSpan MinActionInterval, TimeSpan StopTimeout)
{
    public static DokiDokiTiming Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
}

/// <summary>
/// Drives one DokiDoki actuator. Headset commands answer <see cref="CommandStatus.Unsupported"/>.
/// The session starts and ends with a stop of every channel; a failed write closes the session,
/// and closing always attempts a final stop. The device never acknowledges writes.
/// </summary>
public sealed class DokiDokiDeviceController : BrandControllerBase, IActuatorCommands
{
    private static readonly ActuatorChannel[] AllChannels = Enum.GetValues<ActuatorChannel>();

    private readonly IDuplexByteChannel _channel;
    private readonly DokiDokiFamily _family;
    private readonly DokiDokiTiming _timing;
    // Message ids must be allocated and written in the same order, so one gate covers both.
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly int[] _versions = new int[AllChannels.Length];
    private readonly CancellationTokenSource _heartbeatStop = new();
    private Task? _heartbeat;
    private int _messageId;
    private long _lastActionTicks = long.MinValue;
    private volatile bool _closing;

    public DokiDokiDeviceController(IDuplexByteChannel channel, ControllerContext context, DokiDokiFamily family, DokiDokiTiming? timing = null)
        : base(channel, context)
    {
        _channel = channel;
        _family = family;
        _timing = timing ?? DokiDokiTiming.Default;
        Channels = DokiDokiCommands.SupportedChannels(family).Select(DokiDokiChannels.ToActuator).ToArray();
    }

    public DokiDokiFamily Family => _family;

    public IReadOnlyList<ActuatorChannel> Channels { get; }

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        // Start from a known state: nothing running, whatever an earlier session left behind.
        if (!await SendStopAsync(cancellationToken).ConfigureAwait(false)) return false;
        if (!await SendAsync(DokiDokiCommands.EncodeHeartbeat, () => true, cancellationToken).ConfigureAwait(false)) return false;
        _heartbeat ??= Task.Run(() => HeartbeatLoopAsync(_heartbeatStop.Token));
        return true;
    }

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        // FFB8 notifications have no known meaning (Yilia ignores them); only record liveness.
        Touch();
        if (Logger.IsEnabled(LogLevel.Debug)) Logger.LogDebug("DokiDoki notification: {Data}", Convert.ToHexString(data));
    }

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported("Refresh"));

    public async Task<CommandResult> SetIntensityAsync(ActuatorChannel channel, int strength, CancellationToken cancellationToken = default)
    {
        if (DokiDokiChannels.FromActuator(channel) is not { } wire || !DokiDokiCommands.Supports(_family, wire))
            return NotSupported($"{channel} output");
        int level = Math.Clamp(strength, DokiDokiConstants.MinStrength, DokiDokiConstants.MaxStrength);
        if (level > 0 && !Context.AllowExperimental())
            return CommandResult.Unsupported("Actuator output requires experimental features to be enabled.");
        if (_closing || Closed.IsCompleted) return CommandResult.NotConnected;

        int index = (int)channel;
        int version = Interlocked.Increment(ref _versions[index]);
        bool Current() => !_closing && Volatile.Read(ref _versions[index]) == version;

        try
        {
            await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!Current()) return Superseded();
                long last = Interlocked.Read(ref _lastActionTicks);
                if (last != long.MinValue)
                {
                    var wait = _timing.MinActionInterval - Context.Time.GetElapsedTime(last);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, Context.Time, cancellationToken).ConfigureAwait(false);
                }
                if (!Current()) return Superseded();
                bool written = await SendAsync(id => DokiDokiCommands.EncodeAction(id, _family, wire, level), Current, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastActionTicks, Context.Time.GetTimestamp());
                if (!Current() && !written) return _closing ? CommandResult.NotConnected : Superseded();
                return written ? Written() : new CommandResult(CommandStatus.TransportError, "The control channel failed while writing.");
            }
            finally
            {
                _actionGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(CommandStatus.Cancelled);
        }
    }

    public async Task<CommandResult> StopAllAsync()
    {
        Supersede();
        if (Closed.IsCompleted) return CommandResult.NotConnected;
        using var timeout = new CancellationTokenSource(_timing.StopTimeout);
        try
        {
            return await SendStopAsync(timeout.Token).ConfigureAwait(false)
                ? Written()
                : new CommandResult(CommandStatus.TransportError, "Stop could not be written; the device may still be running.");
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(CommandStatus.Timeout, "Stop timed out; the device may still be running.");
        }
    }

    protected override async ValueTask OnClosingAsync()
    {
        _closing = true;
        Supersede();
        _heartbeatStop.Cancel();
        using var timeout = new CancellationTokenSource(_timing.StopTimeout);
        try
        {
            await _ioGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                // Bypass the closed check: a failed heartbeat closes the session, but the stop is still worth trying.
                foreach (var frame in NextStopFrames())
                {
                    try
                    {
                        LogSent(frame, stop: true);
                        await _channel.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.LogInformation("Final stop write failed: {Message}", ex.Message);
                    }
                }
            }
            finally
            {
                _ioGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("Final stop timed out; the device may still be running");
        }
        if (_heartbeat is { } heartbeat)
        {
            try
            {
                await heartbeat.WaitAsync(_timing.StopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    protected override ValueTask DisposeCoreAsync()
    {
        _heartbeatStop.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !Closed.IsCompleted)
            {
                await Task.Delay(_timing.HeartbeatInterval, Context.Time, ct).ConfigureAwait(false);
                if (!await SendAsync(DokiDokiCommands.EncodeHeartbeat, () => !_closing, ct).ConfigureAwait(false) && !_closing)
                {
                    Logger.LogWarning("DokiDoki heartbeat failed; closing the session");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> SendStopAsync(CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Best effort for every frame: one failed channel must not skip the others.
            bool ok = true;
            foreach (var frame in NextStopFrames())
            {
                LogSent(frame, stop: true);
                ok &= await WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            return ok;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>Writes one frame unless <paramref name="stillWanted"/> turns false while waiting for the gate.</summary>
    private async Task<bool> SendAsync(Func<int, byte[]> encode, Func<bool> stillWanted, CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!stillWanted()) return false;
            var frame = encode(NextMessageId());
            LogSent(frame);
            return await WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>Call only under <see cref="_ioGate"/>.</summary>
    private IReadOnlyList<byte[]> NextStopFrames()
    {
        var frames = DokiDokiCommands.EncodeStopAll(NextMessageId(), _family);
        for (int i = 1; i < frames.Count; i++) NextMessageId();
        return frames;
    }

    /// <summary>Stop frames are kept in the normal log as a safety record; everything else is Debug.</summary>
    private void LogSent(byte[] frame, bool stop = false)
    {
        if (stop) Logger.LogInformation("DokiDoki stop sent: {Frame}", Convert.ToHexString(frame));
        else if (Logger.IsEnabled(LogLevel.Debug)) Logger.LogDebug("DokiDoki sent: {Frame}", Convert.ToHexString(frame));
    }

    /// <summary>Call only under <see cref="_ioGate"/>.</summary>
    private int NextMessageId() => _messageId = DokiDokiCommands.NextMessageId(_messageId);

    private void Supersede()
    {
        for (int i = 0; i < _versions.Length; i++) Interlocked.Increment(ref _versions[i]);
    }

    private static CommandResult Written() => new(CommandStatus.Success, "Written; the device does not acknowledge commands.");

    private static CommandResult Superseded() => new(CommandStatus.Cancelled, "Superseded by a newer command.");
}
