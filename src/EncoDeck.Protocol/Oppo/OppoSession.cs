// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Protocol.Oppo;

public enum SequenceMode
{
    Auto,
    FixedQuery,
    FixedSet,
}

/// <param name="Command">Request command id.</param>
/// <param name="Payload">Request payload.</param>
/// <param name="Sequence">How to pick the sequence byte.</param>
/// <param name="ResponseCommands">Response ids that answer this request; defaults to <c>Command | 0x8000</c>.</param>
public sealed record OppoRequest(
    ushort Command,
    byte[] Payload,
    SequenceMode Sequence = SequenceMode.Auto,
    IReadOnlyList<ushort>? ResponseCommands = null)
{
    public IReadOnlyList<ushort> ExpectedResponses => ResponseCommands ?? [OppoCommand.ResponseOf(Command)];

    public override string ToString() => $"{OppoCommand.Describe(Command)} payload={Convert.ToHexString(Payload)}";
}

public enum RequestStatus
{
    Success,
    Timeout,
    TransportError,
    Cancelled,
    Closed,
}

public sealed record RequestResult(RequestStatus Status, OppoFrame? Response, string? Detail = null)
{
    public bool IsSuccess => Status == RequestStatus.Success;
}

public enum FrameDirection
{
    Sent,
    Received,
}

/// <summary>Observer for raw protocol traffic (diagnostic mode / Probe). Must be cheap and non-throwing.</summary>
public interface IProtocolTrace
{
    void OnFrame(FrameDirection direction, OppoFrame frame, DateTimeOffset timestamp);

    void OnRawReceived(ReadOnlySpan<byte> data, DateTimeOffset timestamp);

    /// <summary>A non-OPPO brand frame, summarized by its controller with identifiers already left out.</summary>
    void OnBrandFrame(FrameDirection direction, string summary, DateTimeOffset timestamp)
    {
    }
}

public enum SessionCloseReason
{
    LocalDispose,
    RemoteClosed,
    TransportError,
}

public sealed class SessionClosedEventArgs(SessionCloseReason reason, Exception? error) : EventArgs
{
    public SessionCloseReason Reason { get; } = reason;
    public Exception? Error { get; } = error;
}

/// <summary>
/// One open control channel. Writes are serialized, requests are processed one at a time
/// (the device answers in order and some responses carry no usable correlation id), and every
/// frame that does not answer the in-flight request is surfaced through <see cref="UnsolicitedFrame"/>.
/// </summary>
public sealed class OppoSession : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly IDuplexByteChannel _channel;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly IProtocolTrace? _trace;
    private readonly OppoFrameDecoder _decoder = new();
    private readonly OppoSequenceAllocator _sequences = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _pendingGate = new();
    private PendingRequest? _pending;
    private Task? _readLoop;
    private int _closed;

    public OppoSession(IDuplexByteChannel channel, ILogger? logger = null, TimeProvider? time = null, IProtocolTrace? trace = null)
    {
        _channel = channel;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _trace = trace;
    }

    /// <summary>Raised on the read-loop thread for notifications and responses nobody is waiting for.</summary>
    public event EventHandler<OppoFrame>? UnsolicitedFrame;

    /// <summary>Raised once when the session ends for any reason.</summary>
    public event EventHandler<SessionClosedEventArgs>? Closed;

    public DateTimeOffset? LastReceivedAt { get; private set; }

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    public long DiscardedBytes => _decoder.DiscardedBytes;

    public void Start()
    {
        if (_readLoop is not null) throw new InvalidOperationException("Session already started.");
        _readLoop = Task.Run(() => ReadLoopAsync(_lifetime.Token));
    }

    /// <summary>Sends a request and waits for its response.</summary>
    public async Task<RequestResult> RequestAsync(OppoRequest request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsOpen) return new RequestResult(RequestStatus.Closed, null, "Session closed.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await _requestGate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(cancellationToken);
        }

        try
        {
            byte sequence = PickSequence(request.Sequence);
            var frame = new OppoFrame(request.Command, sequence, request.Payload);
            var pending = new PendingRequest(request, sequence);
            lock (_pendingGate) _pending = pending;

            try
            {
                await WriteFrameAsync(frame, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Cancelled(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Write failed for {Request}", request);
                await CloseAsync(SessionCloseReason.TransportError, ex).ConfigureAwait(false);
                return new RequestResult(RequestStatus.TransportError, null, ex.Message);
            }

            try
            {
                var response = await pending.Completion.Task.WaitAsync(timeout ?? DefaultTimeout, _time, linked.Token).ConfigureAwait(false);
                if (response.Sequence != sequence)
                {
                    _logger.LogDebug("Response {Command:X4} sequence {Got:X2} differs from request {Sent:X2}", response.Command, response.Sequence, sequence);
                }
                return new RequestResult(RequestStatus.Success, response);
            }
            catch (TimeoutException)
            {
                _logger.LogInformation("Request timed out: {Request}", request);
                return new RequestResult(RequestStatus.Timeout, null, $"No response to {OppoCommand.Describe(request.Command)}.");
            }
            catch (OperationCanceledException)
            {
                return Cancelled(cancellationToken);
            }
            catch (SessionClosedException ex)
            {
                return new RequestResult(ex.Reason == SessionCloseReason.TransportError ? RequestStatus.TransportError : RequestStatus.Closed, null, ex.Message);
            }
        }
        finally
        {
            lock (_pendingGate) _pending = null;
            _requestGate.Release();
        }
    }

    /// <summary>Sends a frame without waiting for a response (e.g. event subscription).</summary>
    public async Task<bool> SendAsync(ushort command, byte[] payload, SequenceMode mode = SequenceMode.Auto, CancellationToken cancellationToken = default)
    {
        if (!IsOpen) return false;
        try
        {
            await WriteFrameAsync(new OppoFrame(command, PickSequence(mode), payload), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Write failed for {Command:X4}", command);
            await CloseAsync(SessionCloseReason.TransportError, ex).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Sends a fully specified frame, e.g. a reply that must echo the device's sequence number.</summary>
    public async Task<bool> SendFrameAsync(OppoFrame frame, CancellationToken cancellationToken = default)
    {
        if (!IsOpen) return false;
        try
        {
            await WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Write failed for {Command:X4}", frame.Command);
            await CloseAsync(SessionCloseReason.TransportError, ex).ConfigureAwait(false);
            return false;
        }
    }

    private byte PickSequence(SequenceMode mode) => mode switch
    {
        SequenceMode.FixedQuery => OppoSequenceAllocator.FixedQuery,
        SequenceMode.FixedSet => OppoSequenceAllocator.FixedSet,
        _ => _sequences.Next(),
    };

    private async Task WriteFrameAsync(OppoFrame frame, CancellationToken cancellationToken)
    {
        var linkFrames = frame.EncodeLinkFrames();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _trace?.OnFrame(FrameDirection.Sent, frame, _time.GetUtcNow());
            for (int i = 0; i < linkFrames.Count; i++)
            {
                if (i > 0)
                {
                    // The official client paces fragments 20 ms apart.
                    await Task.Delay(TimeSpan.FromMilliseconds(20), _time, cancellationToken).ConfigureAwait(false);
                }
                await _channel.WriteAsync(linkFrames[i], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await _channel.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    await CloseAsync(SessionCloseReason.RemoteClosed, null).ConfigureAwait(false);
                    return;
                }
                var now = _time.GetUtcNow();
                LastReceivedAt = now;
                _trace?.OnRawReceived(buffer.AsSpan(0, read), now);
                foreach (var frame in _decoder.Push(buffer.AsSpan(0, read)))
                {
                    _trace?.OnFrame(FrameDirection.Received, frame, now);
                    Dispatch(frame);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Local shutdown.
        }
        catch (Exception ex)
        {
            if (IsOpen) _logger.LogInformation("Control channel read failed: {Message}", ex.Message);
            await CloseAsync(SessionCloseReason.TransportError, ex).ConfigureAwait(false);
        }
    }

    private void Dispatch(OppoFrame frame)
    {
        PendingRequest? pending;
        lock (_pendingGate) pending = _pending;
        if (pending is not null && frame.IsResponse && pending.Request.ExpectedResponses.Contains(frame.Command))
        {
            if (pending.Completion.TrySetResult(frame)) return;
        }

        try
        {
            UnsolicitedFrame?.Invoke(this, frame);
        }
        catch (Exception ex)
        {
            // A faulty subscriber must not take the channel down.
            _logger.LogError(ex, "Unsolicited frame handler failed for {Frame}", frame);
        }
    }

    private Task CloseAsync(SessionCloseReason reason, Exception? error)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return Task.CompletedTask;

        // Order matters: observers learn about the closure before any waiting request fails because of it.
        var dispose = _channel.DisposeAsync();
        try
        {
            Closed?.Invoke(this, new SessionClosedEventArgs(reason, error));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session closed handler failed");
        }

        _lifetime.Cancel();
        PendingRequest? pending;
        lock (_pendingGate) pending = _pending;
        if (pending?.Completion.TrySetException(new SessionClosedException(reason)) == true)
        {
            // A request can still be inside WriteFrameAsync (or have stopped waiting on cancellation).
            // Its result reports the closure, but it may never await this completion task.
            _ = pending.Completion.Task.Exception;
        }
        return dispose.AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(SessionCloseReason.LocalDispose, null).ConfigureAwait(false);
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Read loop did not stop within 2 s");
            }
        }
        // _lifetime is intentionally not disposed: concurrent RequestAsync calls may still link to it.
    }

    private static RequestResult Cancelled(CancellationToken callerToken) =>
        callerToken.IsCancellationRequested
            ? new RequestResult(RequestStatus.Cancelled, null)
            : new RequestResult(RequestStatus.Closed, null, "Session closed.");

    private sealed class PendingRequest(OppoRequest request, byte sequence)
    {
        public OppoRequest Request { get; } = request;
        public byte Sequence { get; } = sequence;
        public TaskCompletionSource<OppoFrame> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SessionClosedException(SessionCloseReason reason) : Exception($"Control channel closed ({reason}).")
    {
        public SessionCloseReason Reason { get; } = reason;
    }
}
