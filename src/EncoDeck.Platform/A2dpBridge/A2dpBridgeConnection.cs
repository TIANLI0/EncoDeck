// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace EncoDeck.Platform.A2dpBridge;

/// <summary>The core refused the connection (another protocol version).</summary>
public sealed class BridgeRejectedException(string reason) : IOException(reason);

/// <summary>An error answer of the core: a code the UI can act on ("adapter.driver_missing") and its message.</summary>
public sealed class BridgeRequestException(string code, BridgeMessage message) : IOException($"{code}: {message.Key}")
{
    public string Code { get; } = code;

    public BridgeMessage BridgeMessage { get; } = message;
}

/// <summary>
/// One session on the core's per-user pipe (<c>\\.\pipe\bluecaster-&lt;SID&gt;</c>, <c>a2dpwb-&lt;SID&gt;</c> up to 2.0.x): length-prefixed JSON frames,
/// hello → welcome + snapshot, then events with consecutive revisions and request/response pairs. EncoDeck
/// subscribes to nothing high-rate, so the core sends only state changes (and the data of its RFCOMM channels).
/// </summary>
internal sealed class A2dpBridgeConnection : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    /// <summary>The welcome's feature for rfcomm.open / send / close (core 2.0.2).</summary>
    public const string RfcommFeature = "rfcomm";
    private const int MaxFrame = 4 << 20;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    // The core answers rfcomm.open within 20 s, also behind other opens, and asks for at least 25 s.
    private static readonly TimeSpan RfcommOpenTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RfcommSendTimeout = TimeSpan.FromSeconds(10);

    private readonly Stream _pipe;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    // rfcomm.open requests: answered on the reader thread, which registers the channel before reading on, so data
    // the core sends right behind the answer finds it. An open given up on before its answer closes its channel.
    private readonly ConcurrentDictionary<long, TaskCompletionSource<BridgeRfcommChannel>> _opens = new();
    private readonly ConcurrentDictionary<long, byte> _abandonedOpens = new();
    private readonly ConcurrentDictionary<uint, BridgeRfcommChannel> _channels = new();
    private readonly CancellationTokenSource _stop = new();
    private long _nextId;
    // Reader-thread state: the last revision applied, and a resynchronisation in flight.
    private long _revision;
    private List<JsonObject>? _resync;
    private long _resyncId;
    // Set before the reader fails the pending requests: a request registered later sees it and fails at once.
    private volatile bool _closed;
    private Task _reader = Task.CompletedTask;

    private A2dpBridgeConnection(Stream pipe, string server, IReadOnlyList<string> features, BridgeSnapshot snapshot, long revision)
    {
        _pipe = pipe;
        Server = server;
        Features = features;
        Snapshot = snapshot;
        _revision = revision;
    }

    /// <summary>"bluecaster-core/3.0.0" ("a2dpwb-core/2.0.1" up to 2.0.x).</summary>
    public string Server { get; }

    /// <summary>The welcome's additions within protocol 1 ("rfcomm"); empty for cores before 2.0.2.</summary>
    public IReadOnlyList<string> Features { get; }

    /// <summary>Replaced (never mutated) on the reader thread.</summary>
    public BridgeSnapshot Snapshot { get; private set; }

    /// <summary>Raised on the reader thread after every state change.</summary>
    public event Action<BridgeSnapshot>? Changed;

    /// <summary>Completes when the core closes the pipe, says bye, or the connection is disposed.</summary>
    public Task Completion => _reader;

    public static async Task<A2dpBridgeConnection> OpenAsync(string pipeName, string client, TimeSpan connectTimeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(connectTimeout, ct).ConfigureAwait(false);
            return await HandshakeAsync(pipe, client, ct).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The handshake on an open stream (tests use an in-memory duplex stream).</summary>
    internal static async Task<A2dpBridgeConnection> HandshakeAsync(Stream pipe, string client, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        await WriteFrameAsync(pipe, new JsonObject { ["type"] = "hello", ["protocol"] = ProtocolVersion, ["client"] = client }, timeout.Token).ConfigureAwait(false);
        var welcome = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false) ?? throw new IOException("BlueCaster closed the connection.");
        switch (BridgeJson.Str(welcome["type"]))
        {
            case "welcome": break;
            case "reject": throw new BridgeRejectedException(BridgeJson.Str(welcome["reason"]) ?? "rejected");
            default: throw new InvalidDataException("BlueCaster did not answer hello.");
        }
        string server = BridgeJson.Str(welcome["server"]) ?? "bluecaster-core";
        var first = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false) ?? throw new IOException("BlueCaster closed the connection.");
        if (BridgeJson.Str(first["type"]) != "snapshot" || first["state"] is not JsonObject state) throw new InvalidDataException("BlueCaster sent no snapshot.");
        var features = (welcome["features"] as JsonArray ?? []).Select(BridgeJson.Str).OfType<string>().ToArray();
        var connection = new A2dpBridgeConnection(pipe, server, features, BridgeJson.Snapshot(state), BridgeJson.Long(first["rev"]) ?? 0);
        connection._reader = Task.Run(connection.ReadLoopAsync);
        return connection;
    }

    /// <summary>Sends a request and waits for its response; the core's error answers throw <see cref="BridgeRequestException"/>.</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            if (_closed) throw new IOException("BlueCaster closed the connection.");
            var request = new JsonObject { ["type"] = "request", ["id"] = id, ["method"] = method };
            if (parameters is not null) request["params"] = parameters;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            await _write.WaitAsync(linked.Token).ConfigureAwait(false);
            try { await WriteFrameAsync(_pipe, request, linked.Token).ConfigureAwait(false); }
            finally { _write.Release(); }
            linked.CancelAfter(timeout);
            try { return await answer.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (_stop.IsCancellationRequested || _closed) throw new IOException("BlueCaster closed the connection.");
                throw new TimeoutException($"BlueCaster did not answer {method}.");
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>
    /// rfcomm.open: an RFCOMM channel from the core's adapter to the device it streams to (<paramref name="address"/>,
    /// the service found by the core's own SDP query). Error answers throw <see cref="BridgeRequestException"/>
    /// (rfcomm.not_connected, rfcomm.service_not_found, rfcomm.open_failed, rfcomm.busy).
    /// </summary>
    public async Task<BridgeRfcommChannel> OpenRfcommAsync(string address, Guid service, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _nextId);
        var opened = new TaskCompletionSource<BridgeRfcommChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        _opens[id] = opened;
        bool answered = false;
        try
        {
            if (_closed) throw new IOException("BlueCaster closed the connection.");
            var request = new JsonObject
            {
                ["type"] = "request", ["id"] = id, ["method"] = "rfcomm.open",
                ["params"] = new JsonObject { ["address"] = address, ["uuid"] = service.ToString("D") },
            };
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            await _write.WaitAsync(linked.Token).ConfigureAwait(false);
            try { await WriteFrameAsync(_pipe, request, linked.Token).ConfigureAwait(false); }
            finally { _write.Release(); }
            linked.CancelAfter(RfcommOpenTimeout);
            try
            {
                var channel = await opened.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                answered = true;
                return channel;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (_stop.IsCancellationRequested || _closed) throw new IOException("BlueCaster closed the connection.");
                throw new TimeoutException("BlueCaster did not answer rfcomm.open.");
            }
        }
        catch (BridgeRequestException)
        {
            answered = true;
            throw;
        }
        finally
        {
            if (!answered)
            {
                // Given up before the answer: the channel it may still open is closed when it comes. The reader
                // may have taken the answer just now: then the channel is already there, and is closed here.
                if (_opens.TryRemove(id, out _)) _abandonedOpens[id] = 0;
                else _ = opened.Task.ContinueWith(t => t.Result.DisposeAsync(), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            }
        }
    }

    /// <summary>rfcomm.send: queued on the air in the order of the calls; rfcomm.busy when the core's queue is full.</summary>
    internal Task SendRfcommAsync(uint channel, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        RequestAsync("rfcomm.send", new JsonObject { ["channel_id"] = channel, ["data"] = Convert.ToBase64String(data.Span) }, RfcommSendTimeout, ct);

    /// <summary>rfcomm.close (after what was sent before it); no rfcomm_closed event follows.</summary>
    internal async Task CloseRfcommAsync(uint channel)
    {
        if (!_channels.TryRemove(channel, out _) || _closed) return;
        try { await RequestAsync("rfcomm.close", new JsonObject { ["channel_id"] = channel }, RfcommSendTimeout, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void OnRfcommOpened(long id, JsonObject frame)
    {
        if (_opens.TryRemove(id, out var open))
        {
            if (frame["error"] is JsonObject error)
                open.TrySetException(new BridgeRequestException(BridgeJson.Str(error["code"]) ?? "error", BridgeJson.Message(error["message"]) ?? BridgeMessage.Empty));
            else if (RfcommChannelId(frame["result"]) is { } channelId)
            {
                var channel = new BridgeRfcommChannel(this, channelId, (int)(BridgeJson.Long(frame["result"]?["mtu"]) ?? 0));
                _channels[channelId] = channel;
                open.TrySetResult(channel);
            }
            else open.TrySetException(new InvalidDataException("BlueCaster answered rfcomm.open without a channel."));
        }
        else if (_abandonedOpens.TryRemove(id, out _) && RfcommChannelId(frame["result"]) is { } orphan)
        {
            _ = SendAsync(new JsonObject
            {
                ["type"] = "request", ["id"] = Interlocked.Increment(ref _nextId), ["method"] = "rfcomm.close",
                ["params"] = new JsonObject { ["channel_id"] = orphan },
            });
        }
    }

    /// <summary>rfcomm_data and rfcomm_closed: not state, they carry the current revision.</summary>
    private void OnRfcommEvent(string kind, JsonObject? value)
    {
        if (RfcommChannelId(value) is not { } id) return;
        if (kind == "rfcomm_closed")
        {
            if (_channels.TryRemove(id, out var closed)) closed.OnClosed(BridgeJson.Str(value!["reason"]) ?? "remote");
            return;
        }
        if (!_channels.TryGetValue(id, out var channel) || BridgeJson.Str(value!["data"]) is not { } data) return;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { return; }
        if (bytes.Length > 0) channel.OnData(bytes);
    }

    private static uint? RfcommChannelId(JsonNode? value) =>
        BridgeJson.Long(value?["channel_id"]) is long id and >= 0 and <= uint.MaxValue ? (uint)id : null;

    /// <summary>Whether this session holds an RFCOMM request for <paramref name="id"/> (reader thread).</summary>
    private bool IsRfcommOpen(long id) => _opens.ContainsKey(id) || _abandonedOpens.ContainsKey(id);

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(_pipe, _stop.Token).ConfigureAwait(false);
                if (frame is null) break;
                switch (BridgeJson.Str(frame["type"]))
                {
                    case "response":
                        long id = BridgeJson.Long(frame["id"]) ?? -1;
                        if (id == _resyncId)
                        {
                            _resyncId = 0;
                            if (frame["result"] is JsonObject fresh) ApplyResync(fresh);
                            else _resync = null;
                        }
                        else if (IsRfcommOpen(id)) OnRfcommOpened(id, frame);
                        else if (_pending.TryGetValue(id, out var answer))
                        {
                            if (frame["error"] is JsonObject error)
                                answer.TrySetException(new BridgeRequestException(BridgeJson.Str(error["code"]) ?? "error",
                                    BridgeJson.Message(error["message"]) ?? BridgeMessage.Empty));
                            else answer.TrySetResult(frame["result"]?.DeepClone());
                        }
                        break;
                    case "snapshot" when frame["state"] is JsonObject state:
                        // The core's own snapshot (it noticed this reader fell behind) carries its true revision.
                        _resync = null;
                        _resyncId = 0;
                        _revision = BridgeJson.Long(frame["rev"]) ?? _revision;
                        Publish(BridgeJson.Snapshot(state));
                        break;
                    case "event" when frame["event"] is JsonObject @event:
                        string? kind = BridgeJson.Str(@event["kind"]);
                        if (kind is "rfcomm_data" or "rfcomm_closed")
                        {
                            OnRfcommEvent(kind, @event["value"] as JsonObject);
                            break;
                        }
                        if (kind is "link" or "log" or "receiver") break; // Not state, no revision; never subscribed.
                        if (kind == "bye") return;
                        long rev = BridgeJson.Long(frame["rev"]) ?? 0;
                        // Subscribed before the snapshot was taken: events up to its revision are already in it.
                        if (rev <= _revision) break;
                        bool gap = rev > _revision + 1;
                        _revision = rev;
                        _resync?.Add(@event);
                        if (BridgeJson.Apply(Snapshot, @event, out _) is { } next) Publish(next);
                        if (gap && _resync is null) StartResync();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or System.Text.Json.JsonException)
        {
            failure = ex;
        }
        finally
        {
            _closed = true;
            var closed = failure ?? new IOException("BlueCaster closed the connection.");
            foreach (var pending in _pending.Values) pending.TrySetException(closed);
            foreach (var open in _opens.Values) open.TrySetException(closed);
            // The core closes a session's channels with it.
            foreach (var id in _channels.Keys)
                if (_channels.TryRemove(id, out var channel)) channel.OnClosed("disconnected");
        }
    }

    /// <summary>A lost event: ask for the whole state and replay the events that arrive meanwhile on top of it, as BlueCaster's window does.</summary>
    private void StartResync()
    {
        _resync = [];
        long id = Interlocked.Increment(ref _nextId);
        _resyncId = id;
        _ = SendAsync(new JsonObject { ["type"] = "request", ["id"] = id, ["method"] = "state.snapshot" });
    }

    private void ApplyResync(JsonObject state)
    {
        var snapshot = BridgeJson.Snapshot(state);
        foreach (var @event in _resync ?? []) snapshot = BridgeJson.Apply(snapshot, @event, out _) ?? snapshot;
        _resync = null;
        Publish(snapshot);
    }

    private void Publish(BridgeSnapshot snapshot)
    {
        Snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }

    private async Task SendAsync(JsonObject message)
    {
        try
        {
            await _write.WaitAsync(_stop.Token).ConfigureAwait(false);
            try { await WriteFrameAsync(_pipe, message, _stop.Token).ConfigureAwait(false); }
            finally { _write.Release(); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    internal static async Task WriteFrameAsync(Stream stream, JsonNode message, CancellationToken ct)
    {
        byte[] json = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (json.Length > MaxFrame) throw new InvalidDataException("Frame too large.");
        byte[] frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
        json.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One frame, or null at the end of the stream.</summary>
    internal static async Task<JsonObject?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int got = 0;
        while (got < 4)
        {
            int n = await stream.ReadAsync(header.AsMemory(got), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (got == 0) return null;
                throw new IOException("BlueCaster frame was cut off.");
            }
            got += n;
        }
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxFrame) throw new InvalidDataException("BlueCaster frame is too large.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("BlueCaster frame is not an object.");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        try { await _reader.ConfigureAwait(false); } catch (Exception) { }
        _stop.Dispose();
    }
}
