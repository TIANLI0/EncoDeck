// SPDX-License-Identifier: GPL-3.0-only
using System.Threading.Channels;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Platform.A2dpBridge;

/// <summary>
/// An RFCOMM channel the BlueCaster core opened from its own adapter (rfcomm.open), as EncoDeck's byte channel: what
/// the earbuds send arrives as rfcomm_data events, writes are rfcomm.send requests. Windows sees nothing of it.
/// </summary>
public sealed class BridgeRfcommChannel : IDuplexByteChannel
{
    /// <summary>The most rfcomm.send takes at once (64 KiB, decoded).</summary>
    internal const int MaxSend = 64 * 1024;
    private static readonly TimeSpan BusyDelay = TimeSpan.FromMilliseconds(20);
    private const int BusyRetries = 100;

    private readonly A2dpBridgeConnection _connection;
    private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private ReadOnlyMemory<byte> _rest;
    private int _disposed;

    internal BridgeRfcommChannel(A2dpBridgeConnection connection, uint id, int mtu)
    {
        _connection = connection;
        Id = id;
        Mtu = mtu;
    }

    /// <summary>The core's channel id (per session).</summary>
    public uint Id { get; }

    /// <summary>The RFCOMM frame size the channel opened with.</summary>
    public int Mtu { get; }

    /// <summary>Why the core closed the channel ("remote", "disconnected", "stopped", "overflow"); null while it is open.</summary>
    public string? CloseReason { get; private set; }

    public string Description => $"BlueCaster RFCOMM channel {Id}";

    internal void OnData(byte[] data) => _received.Writer.TryWrite(data);

    internal void OnClosed(string reason)
    {
        CloseReason ??= reason;
        _received.Writer.TryComplete();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        while (_rest.IsEmpty)
        {
            if (!await _received.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            if (_received.Reader.TryRead(out var next)) _rest = next;
        }
        int count = Math.Min(buffer.Length, _rest.Length);
        _rest.Span[..count].CopyTo(buffer.Span);
        _rest = _rest[count..];
        return count;
    }

    /// <summary>Returns once the core has queued the data; a full queue (rfcomm.busy) is waited out briefly.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        while (!data.IsEmpty)
        {
            var chunk = data[..Math.Min(data.Length, MaxSend)];
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await _connection.SendRfcommAsync(Id, chunk, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (BridgeRequestException ex) when (ex.Code == "rfcomm.busy" && attempt < BusyRetries)
                {
                    await Task.Delay(BusyDelay, cancellationToken).ConfigureAwait(false);
                }
                // Not answered in time: it may or may not have been sent, so the channel is no use any more.
                catch (TimeoutException ex) { throw new IOException(ex.Message, ex); }
            }
            data = data[chunk.Length..];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _received.Writer.TryComplete();
        await _connection.CloseRfcommAsync(Id).ConfigureAwait(false);
    }
}
