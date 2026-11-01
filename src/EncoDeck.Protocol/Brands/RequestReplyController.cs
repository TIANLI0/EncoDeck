// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands;

public sealed record BrandPacket(ushort Command, byte Type, byte[] Payload);

/// <summary>One outstanding transaction, including cancellation and transport shutdown.</summary>
public abstract class RequestReplyController(IDuplexByteChannel channel, ControllerContext context)
    : BrandControllerBase(channel, context)
{
    protected readonly SemaphoreSlim Operations = new(1, 1);
    private readonly object _pendingGate = new();
    private TaskCompletionSource<BrandPacket>? _pending;
    private Func<BrandPacket, bool>? _match;
    private readonly CancellationTokenSource _pollStop = new();
    private Task? _poll;

    protected void StartPolling() => _poll ??= PollAsync(_pollStop.Token);
    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), Context.Time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false) && !Closed.IsCompleted)
                await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.LogWarning(ex, "Periodic device refresh stopped"); }
    }
    protected override async ValueTask DisposeCoreAsync()
    {
        await _pollStop.CancelAsync().ConfigureAwait(false);
        if (_poll is not null) await _poll.ConfigureAwait(false);
        _pollStop.Dispose();
    }

    protected abstract IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes);
    protected abstract void HandlePacket(BrandPacket packet);

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        foreach (var packet in Decode(data))
        {
            HandlePacket(packet);
            lock (_pendingGate)
                if (_match?.Invoke(packet) == true) _pending?.TrySetResult(packet);
        }
    }

    protected async Task<BrandPacket?> ExchangeAsync(byte[] bytes, Func<BrandPacket, bool> match, CancellationToken ct, TimeSpan? timeout = null)
    {
        var completion = new TaskCompletionSource<BrandPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate) { _pending = completion; _match = match; }
        try
        {
            if (!await WriteAsync(bytes, ct).ConfigureAwait(false)) return null;
            var winner = await Task.WhenAny(completion.Task, Closed, Task.Delay(timeout ?? TimeSpan.FromSeconds(3), ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return winner == completion.Task ? await completion.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            lock (_pendingGate)
                if (ReferenceEquals(_pending, completion)) { _pending = null; _match = null; }
        }
    }

    protected static CommandResult TimedOut => new(CommandStatus.Timeout, "The device did not return a valid control response.");
}
