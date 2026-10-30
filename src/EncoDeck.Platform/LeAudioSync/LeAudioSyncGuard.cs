// SPDX-License-Identifier: GPL-3.0-only
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Platform.LeAudioSync;

public enum LeAudioSyncState
{
    Off,
    /// <summary>This PC cannot use the feature (see <see cref="LeAudioSyncStatus.Support"/>).</summary>
    Unsupported,
    /// <summary>No LE Audio earbuds with both links (and an active render endpoint) connected.</summary>
    WaitingForDevice,
    /// <summary>The link was (re)built and its alignment has not been checked.</summary>
    NeedsCheck,
    /// <summary>The check clicks are playing; waiting for the user's answer.</summary>
    Checking,
    /// <summary>The user heard the clicks in the middle; the link is held so it stays that way.</summary>
    Aligned,
    /// <summary>Closing and reopening the link (<see cref="LeAudioSyncStatus.Rebuilds"/>).</summary>
    Rebuilding,
    /// <summary>A rebuild was asked for but other programs are playing; it runs once they stop.</summary>
    WaitingForIdle,
    /// <summary>A program records from the earbuds; Windows runs a different link until it stops.</summary>
    MicrophoneInUse,
}

public sealed record LeAudioSyncStatus(
    LeAudioSyncState State,
    LeAudioSyncSupport Support = LeAudioSyncSupport.Supported,
    int Rebuilds = 0,
    string? Detail = null);

/// <summary>The earbuds' LE Audio endpoints as MMDevice ids; Capture is null when Windows exposes no microphone.</summary>
public sealed record LeAudioEndpoints(string Render, string? Capture);

/// <summary>Windows services the guard drives; faked in tests.</summary>
public interface ILeAudioSyncPlatform
{
    LeAudioSyncSupport CheckSupport();

    IKeepAliveStream OpenKeepAlive(string renderEndpointId);

    bool HasOtherActiveSessions(string renderEndpointId);

    bool HasActiveCapture(string captureEndpointId);
}

public sealed record LeAudioSyncTimings(TimeSpan Teardown, TimeSpan CheckDuration, TimeSpan Poll)
{
    /// <summary>Windows tears the CIS down about 0.5 s after the last stream closes.</summary>
    public static LeAudioSyncTimings Default { get; } = new(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(2));
}

/// <summary>
/// Keeps LE Audio earbuds' left and right channels aligned on Intel offload PCs. Each time Windows builds the link
/// (both CIS) there is a chance the left one starts one ISO interval late, and nothing on the PC reports which way it
/// went; once built, the alignment holds as long as the link does. So the guard holds the link with a stream of zero
/// samples (programs that play later join it), lets the user hear identical clicks in both ears on that same stream,
/// and rebuilds the link when they are off-centre. It notices what rebuilds the link behind its back (the earbuds
/// leaving, a program opening their microphone) and asks for a new check. All state changes run on one queue.
/// </summary>
public sealed class LeAudioSyncGuard : IAsyncDisposable
{
    private readonly ILeAudioSyncPlatform _platform;
    private readonly ILogger _logger;
    private readonly LeAudioSyncTimings _timings;
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private bool _enabled;
    private LeAudioEndpoints? _endpoints;
    private LeAudioSyncSupport _support = LeAudioSyncSupport.Supported;
    private IKeepAliveStream? _keepAlive;
    private int _rebuilds;
    // Incremented whenever a pending timer must be ignored.
    private long _checkGeneration;
    private bool _polling;
    // A rebuild waiting for other programs to stop: whether to play the check once it is done.
    private bool _pendingCheckAfter;

    public LeAudioSyncGuard(ILeAudioSyncPlatform platform, ILogger logger, LeAudioSyncTimings? timings = null)
    {
        _platform = platform;
        _logger = logger;
        _timings = timings ?? LeAudioSyncTimings.Default;
        _loop = Task.Run(RunAsync);
    }

    public LeAudioSyncStatus Status { get; private set; } = new(LeAudioSyncState.Off);

    /// <summary>Raised on a background thread after <see cref="Status"/> changes.</summary>
    public event EventHandler? Changed;

    public void SetEnabled(bool enabled) => Post(() =>
    {
        if (_enabled == enabled) return Task.CompletedTask;
        _enabled = enabled;
        Reconcile();
        return Task.CompletedTask;
    });

    /// <summary>The active earbuds' endpoints while both earbuds are linked over LE, else null.</summary>
    public void SetEndpoints(LeAudioEndpoints? endpoints) => Post(() =>
    {
        if (endpoints == _endpoints) return Task.CompletedTask;
        bool renderChanged = !string.Equals(endpoints?.Render, _endpoints?.Render, StringComparison.OrdinalIgnoreCase);
        _endpoints = endpoints;
        if (renderChanged) CloseKeepAlive();
        Reconcile();
        return Task.CompletedTask;
    });

    /// <summary>Plays the check clicks on the held stream.</summary>
    public void StartCheck() => Post(() =>
    {
        BeginCheck();
        return Task.CompletedTask;
    });

    /// <summary>The user heard the clicks in the middle.</summary>
    public void ConfirmAligned() => Post(() =>
    {
        if (_keepAlive is null) return Task.CompletedTask;
        _keepAlive.StopCheck();
        _checkGeneration++;
        SetState(LeAudioSyncState.Aligned);
        return Task.CompletedTask;
    });

    /// <summary>The user heard the clicks off-centre (or doubled): rebuild the link, then check again.</summary>
    public void ReportOffset() => Post(() => RebuildAsync(checkAfter: true));

    /// <summary>Rebuilds the link without playing the check afterwards (diagnostics).</summary>
    public void Rebuild() => Post(() => RebuildAsync(checkAfter: false));

    /// <summary>Stops the check without an answer.</summary>
    public void CancelCheck() => Post(() =>
    {
        if (Status.State != LeAudioSyncState.Checking) return Task.CompletedTask;
        _keepAlive?.StopCheck();
        _checkGeneration++;
        SetState(LeAudioSyncState.NeedsCheck);
        return Task.CompletedTask;
    });

    private void Post(Func<Task> work) => _queue.Writer.TryWrite(work);

    private async Task RunAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "LE Audio sync step failed");
            }
        }
    }

    private void Reconcile()
    {
        if (!_enabled)
        {
            CloseKeepAlive();
            SetState(LeAudioSyncState.Off);
            return;
        }
        _support = _platform.CheckSupport();
        if (_support != LeAudioSyncSupport.Supported)
        {
            CloseKeepAlive();
            SetState(LeAudioSyncState.Unsupported);
            return;
        }
        if (_endpoints is null)
        {
            CloseKeepAlive();
            SetState(LeAudioSyncState.WaitingForDevice);
            return;
        }
        if (_keepAlive is null && OpenKeepAlive()) SetState(LeAudioSyncState.NeedsCheck);
        StartPolling();
    }

    private bool OpenKeepAlive()
    {
        if (_endpoints is not { } endpoints) return false;
        try
        {
            var stream = _platform.OpenKeepAlive(endpoints.Render);
            stream.Lost += (_, _) => Post(() => OnKeepAliveLostAsync(stream));
            _keepAlive = stream;
            _logger.LogInformation("LE Audio link held");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LE Audio keep-alive stream failed to open");
            // Forget the endpoints so the owner's next SetEndpoints (even with the same ids) opens again.
            _endpoints = null;
            SetState(LeAudioSyncState.WaitingForDevice, ex.Message);
            return false;
        }
    }

    private void BeginCheck()
    {
        if (_keepAlive is null || Status.State is LeAudioSyncState.MicrophoneInUse or LeAudioSyncState.Rebuilding) return;
        try
        {
            _keepAlive.PlayCheck(_timings.CheckDuration);
        }
        catch (NotSupportedException ex)
        {
            SetState(Status.State, ex.Message);
            return;
        }
        long generation = ++_checkGeneration;
        SetState(LeAudioSyncState.Checking);
        // No answer while the clicks play: the alignment is still unknown.
        _ = Task.Delay(_timings.CheckDuration, _cts.Token).ContinueWith(_ => Post(() =>
        {
            if (generation == _checkGeneration && Status.State == LeAudioSyncState.Checking) SetState(LeAudioSyncState.NeedsCheck);
            return Task.CompletedTask;
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private async Task RebuildAsync(bool checkAfter)
    {
        if (_keepAlive is null || _endpoints is not { } endpoints) return;
        _keepAlive.StopCheck();
        _checkGeneration++;
        bool busy;
        try { busy = _platform.HasOtherActiveSessions(endpoints.Render); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not list audio sessions");
            busy = true;
        }
        if (busy)
        {
            // While another program plays, closing this stream leaves the link up: nothing would change.
            _pendingCheckAfter = checkAfter;
            SetState(LeAudioSyncState.WaitingForIdle);
            return;
        }
        _rebuilds++;
        SetState(LeAudioSyncState.Rebuilding);
        CloseKeepAlive();
        await Task.Delay(_timings.Teardown, _cts.Token).ConfigureAwait(false);
        if (!_enabled || _endpoints != endpoints) return;
        if (!OpenKeepAlive()) return;
        _logger.LogInformation("LE Audio link rebuilt ({Rebuilds})", _rebuilds);
        if (checkAfter)
        {
            SetState(LeAudioSyncState.NeedsCheck);
            BeginCheck();
        }
        else
        {
            SetState(LeAudioSyncState.NeedsCheck);
        }
    }

    private void StartPolling()
    {
        if (_polling) return;
        _polling = true;
        _ = PollAsync();
    }

    /// <summary>Watches what the guard cannot see through its own stream: the microphone and other players.</summary>
    private async Task PollAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_timings.Poll, _cts.Token).ConfigureAwait(false);
                var done = new TaskCompletionSource<bool>();
                Post(async () =>
                {
                    try { done.TrySetResult(await PollOnceAsync().ConfigureAwait(false)); }
                    catch (Exception ex) { done.TrySetException(ex); }
                });
                if (!await done.Task.ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LE Audio sync polling stopped");
        }
        Post(() =>
        {
            _polling = false;
            if (_enabled && _keepAlive is not null) StartPolling();
            return Task.CompletedTask;
        });
    }

    /// <returns>false when there is nothing left to watch.</returns>
    private async Task<bool> PollOnceAsync()
    {
        if (!_enabled || _endpoints is not { } endpoints || _keepAlive is null) return false;
        if (endpoints.Capture is { } capture)
        {
            bool microphone;
            try { microphone = _platform.HasActiveCapture(capture); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not list capture sessions");
                microphone = false;
            }
            if (microphone && Status.State != LeAudioSyncState.MicrophoneInUse)
            {
                _keepAlive.StopCheck();
                _checkGeneration++;
                SetState(LeAudioSyncState.MicrophoneInUse);
                return true;
            }
            if (!microphone && Status.State == LeAudioSyncState.MicrophoneInUse)
            {
                // Windows went back to the playback link: a new one, so a new chance of a late start.
                SetState(LeAudioSyncState.NeedsCheck);
                return true;
            }
        }
        if (Status.State == LeAudioSyncState.WaitingForIdle)
        {
            bool busy;
            try { busy = _platform.HasOtherActiveSessions(endpoints.Render); }
            catch (Exception ex) when (ex is not OperationCanceledException) { busy = true; _logger.LogDebug(ex, "Could not list audio sessions"); }
            if (!busy) await RebuildAsync(_pendingCheckAfter).ConfigureAwait(false);
        }
        return true;
    }

    private Task OnKeepAliveLostAsync(IKeepAliveStream stream)
    {
        if (!ReferenceEquals(stream, _keepAlive)) return Task.CompletedTask;
        CloseKeepAlive();
        // The endpoint left (the earbuds disconnected); the owner reports it again once it is back.
        _endpoints = null;
        SetState(LeAudioSyncState.WaitingForDevice);
        return Task.CompletedTask;
    }

    private void CloseKeepAlive()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
        _checkGeneration++;
    }

    private void SetState(LeAudioSyncState state, string? detail = null)
    {
        var status = new LeAudioSyncStatus(state, _support, _rebuilds, detail);
        if (status == Status) return;
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        CloseKeepAlive();
        _cts.Dispose();
    }
}
