// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands;

/// <summary>
/// Shared plumbing for the non-OPPO brand controllers: a read loop over the channel that hands
/// received bytes to <see cref="OnReceived"/>, serialized writes, <see cref="Closed"/>, and
/// <see cref="CommandStatus.Unsupported"/> answers for every command a brand does not implement.
/// </summary>
public abstract class BrandControllerBase : IDeviceController
{
    private readonly IDuplexByteChannel _channel;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _readLoop;
    private int _disposed;

    protected BrandControllerBase(IDuplexByteChannel channel, ControllerContext context)
    {
        _channel = channel;
        Context = context;
        Logger = context.Loggers.CreateLogger(GetType());
    }

    protected ControllerContext Context { get; }

    protected ILogger Logger { get; }

    protected IDeviceStateStore Store => Context.Store;

    protected string DeviceId => Context.Identity.DeviceId;

    protected DateTimeOffset Now => Context.Time.GetUtcNow();

    public Task Closed => _closed.Task;

    /// <summary>Ends a session whose protocol sequence can no longer be safely reused.</summary>
    protected void EndSession() => _closed.TrySetResult();

    public event EventHandler? CaseLidOpened;

    protected void RaiseCaseLidOpened() => CaseLidOpened?.Invoke(this, EventArgs.Empty);

    /// <summary>Starts the read loop, then runs the brand's start-up sequence.</summary>
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        _readLoop ??= Task.Run(() => ReadLoopAsync(_lifetime.Token));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await StartSessionAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>Brand handshake and initial reads. Return false when the device does not answer.</summary>
    protected abstract Task<bool> StartSessionAsync(CancellationToken cancellationToken);

    /// <summary>Called on the read loop for every chunk received.</summary>
    protected abstract void OnReceived(ReadOnlySpan<byte> data);

    protected async Task<bool> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_closed.Task.IsCompleted) return false;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Write failed");
            _closed.TrySetResult();
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await _channel.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;
                Context.Trace?.OnRawReceived(buffer.AsSpan(0, read), Now);
                try
                {
                    OnReceived(buffer.AsSpan(0, read));
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to handle received data");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogInformation(ex, "Control channel read ended");
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    /// <summary>How often a frame may refresh <see cref="DeviceState.LastResponseAt"/>.</summary>
    private static readonly TimeSpan LivenessUpdateInterval = TimeSpan.FromSeconds(30);

    /// <summary>Records that the device answered, for the "last response" indicator.</summary>
    /// <remarks>
    /// Throttled like the OPPO controller: every state change wakes the UI, the cache and resource
    /// sync, and periodic device chatter would otherwise do that for each received frame.
    /// </remarks>
    protected void Touch()
    {
        var now = Now;
        // Returning the same instance makes the store skip the change and its event.
        Store.TryUpdate(DeviceId, s => s.LastResponseAt is { } last && now - last <= LivenessUpdateInterval && now >= last
            ? s
            : s with { LastResponseAt = now });
    }

    /// <summary>
    /// Whether a command may start now; checked after the command gate is entered. Brands add their own
    /// conditions (the store's active device, a resolved route).
    /// </summary>
    protected virtual bool CanRunCommands => !Closed.IsCompleted;

    /// <summary>
    /// Runs one device-changing command: waits for <paramref name="gate"/> (one command at a time), marks it
    /// pending under <paramref name="pendingKey"/> (see <see cref="PendingKeys"/>) together with the optional
    /// optimistic value, runs <paramref name="action"/> (write and confirm), then always removes the marker.
    /// A failure rolls back the optimistic fields that are still optimistic and is recorded as the last error.
    /// </summary>
    /// <param name="gate">
    /// The brand's command gate, shared with its refresh; null when the brand serializes its traffic another
    /// way or the caller already holds the gate.
    /// </param>
    protected Task<CommandResult> RunCommandAsync(
        string pendingKey,
        SemaphoreSlim? gate,
        Func<CancellationToken, Task<CommandResult>> action,
        CancellationToken cancellationToken,
        Func<DeviceState, DeviceState>? applyOptimistic = null) =>
        RunCoreAsync(pendingKey, gate, action, applyOptimistic, cancellationToken);

    /// <summary>
    /// Like <see cref="RunCommandAsync"/> but without a pending marker or last error: for refreshes and for
    /// sequences whose steps mark themselves.
    /// </summary>
    protected Task<CommandResult> RunExclusiveAsync(SemaphoreSlim? gate, Func<CancellationToken, Task<CommandResult>> action, CancellationToken cancellationToken) =>
        RunCoreAsync(null, gate, action, null, cancellationToken);

    private async Task<CommandResult> RunCoreAsync(
        string? pendingKey,
        SemaphoreSlim? gate,
        Func<CancellationToken, Task<CommandResult>> action,
        Func<DeviceState, DeviceState>? applyOptimistic,
        CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            try
            {
                if (gate is not null)
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    entered = true;
                }
            }
            catch (OperationCanceledException)
            {
                // Never started: nothing to mark, roll back or report.
                return new CommandResult(CommandStatus.Cancelled);
            }
            if (!CanRunCommands) return CommandResult.NotConnected;

            PendingCommand? pending = null;
            DeviceState? before = null, optimistic = null;
            if (pendingKey is not null)
            {
                pending = new PendingCommand(Guid.NewGuid(), pendingKey, Now);
                Store.TryUpdate(DeviceId, s =>
                {
                    before = s;
                    optimistic = applyOptimistic?.Invoke(s) ?? s;
                    return optimistic with { PendingCommands = s.PendingCommands.Add(pending), LastError = null };
                });
            }

            CommandResult result;
            try
            {
                result = await action(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new CommandResult(CommandStatus.Cancelled);
            }
            catch (Exception ex)
            {
                // E.g. the channel torn down mid-command. The marker must still go: a marker left behind
                // keeps the control disabled until the app restarts.
                Logger.LogWarning(ex, "Command {Name} failed", pendingKey ?? "exclusive operation");
                result = new CommandResult(CommandStatus.TransportError, ex.Message);
            }

            if (pending is not null)
            {
                Store.TryUpdate(DeviceId, s =>
                {
                    var next = s with { PendingCommands = s.PendingCommands.RemoveAll(p => p.Id == pending.Id) };
                    if (result.IsSuccess) return next;
                    if (before is not null && optimistic is not null) next = OptimisticRollback.Restore(before, optimistic, next);
                    // Gate answers (not supported, not connected) are not device failures.
                    return result.Status is CommandStatus.Unsupported or CommandStatus.NotConnected
                        ? next
                        : next with { LastError = new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now) };
                });
                Logger.LogInformation("Command {Name} -> {Result}", pendingKey, result);
            }
            return result;
        }
        finally
        {
            if (entered) gate!.Release();
        }
    }
    protected static CommandResult NotSupported(string what) => CommandResult.Unsupported($"{what} is not supported on this device.");

    public virtual Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken) => Task.FromResult(NotSupported("Noise control"));

    public virtual Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken) => Task.FromResult(NotSupported("Equalizer"));

    public virtual Task<CommandResult> SetEqualizerBandsAsync(IReadOnlyList<int> bands, CancellationToken cancellationToken) => Task.FromResult(NotSupported("Editable equalizer bands"));

    public virtual Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken) => Task.FromResult(NotSupported(toggle.ToString()));

    public abstract Task<CommandResult> RefreshAsync(CancellationToken cancellationToken);

    public virtual Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Spatial audio"));

    public virtual Task<CommandResult> SetLeBrSwitchAsync(bool enabled, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("LE/BR switch"));

    public virtual Task<CommandResult> SetGameSoundAsync(int type, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Game sound"));

    public virtual Task<CommandResult> RefreshGameModeAsync(CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Game mode"));

    public virtual Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Prompt volume"));

    public virtual Task<CommandResult> SetGainAsync(int level, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Gain"));

    public virtual Task<CommandResult> SetBassEngineLevelAsync(int level, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Bass engine"));

    public virtual Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Gestures"));

    public virtual Task<CommandResult> SetNoiseCycleAsync(EarSide side, uint modeMask, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Noise cycle"));

    public virtual Task<CommandResult> SetHeadMotionTypeAsync(int type, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Head gestures"));

    public virtual Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Find earbuds"));

    public virtual Task<CommandResult> RunFitTestAsync(CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Fit test"));

    public virtual Task<CommandResult> MultiConnectAsync(MultiConnectOperation operation, byte[] address, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Dual connection"));

    public virtual Task<CommandResult> SetPreferredDeviceAsync(byte[]? address, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Preferred device"));

    public virtual Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Custom EQ"));

    public virtual Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Custom EQ"));

    public virtual Task<CommandResult> AddCustomEqualizerAsync(CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Custom EQ"));

    public virtual Task<CommandResult> DeleteCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Custom EQ"));

    public virtual Task<CommandResult> SetOptionAsync(string id, int value, CancellationToken cancellationToken = default) => Task.FromResult(NotSupported($"Option {id}"));

    public virtual Task<CommandResult> FactoryResetAsync(CancellationToken cancellationToken = default) => Task.FromResult(NotSupported("Factory reset"));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _channel.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        _closed.TrySetResult();
        await DisposeCoreAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;
}
