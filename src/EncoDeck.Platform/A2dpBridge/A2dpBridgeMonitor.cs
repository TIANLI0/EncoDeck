// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Principal;
using System.Text.Json.Nodes;
using EncoDeck.Platform.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Platform.A2dpBridge;

public enum BridgeLink
{
    /// <summary>No BlueCaster core runs for this user.</summary>
    NotRunning,

    /// <summary>The core runs; the pipe session is being opened.</summary>
    Connecting,

    Connected,

    /// <summary>The core refused EncoDeck's protocol version.</summary>
    Incompatible,
}

/// <param name="Server">"bluecaster-core/3.0.0" ("a2dpwb-core/2.0.1" up to 2.0.x) while connected.</param>
/// <param name="Detail">Why the core is not usable (Incompatible), in English from the core.</param>
/// <param name="Rfcomm">The core opens RFCOMM channels to the device it streams to (its "rfcomm" feature, 2.0.2).</param>
public sealed record BridgeStatus(BridgeLink Link, BridgeSnapshot? Snapshot = null, string? Server = null, string? Detail = null, bool Rfcomm = false)
{
    public static BridgeStatus NotRunning { get; } = new(BridgeLink.NotRunning);

    public bool IsConnected => Link == BridgeLink.Connected && Snapshot is not null;

    /// <summary>"2.0.1" from <see cref="Server"/>.</summary>
    public string? ServerVersion => Server is { } s && s.IndexOf('/') is var slash and >= 0 ? s[(slash + 1)..] : null;
}

/// <summary>The core is not running or not connected.</summary>
public sealed class BridgeUnavailableException() : InvalidOperationException("BlueCaster is not running.");

/// <summary>
/// Follows the BlueCaster core of this user (or an A2DP Windows Bridge 2.0.x one) and keeps one pipe session to it
/// while it runs. Detection is a check of the core's single-instance mutex (opened and closed at once, like the core's
/// own <c>--quit</c>), every <see cref="IdleInterval"/> while no core runs; once connected, everything is pushed by the core.
/// </summary>
public sealed class A2dpBridgeMonitor : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly string? _instance;
    private readonly string _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private TaskCompletionSource _wake = NewWake();
    // After a wake (the core was just started), look every quarter second for a while: a new core takes a moment to
    // create its mutex and pipe, and a single look would find nothing and wait a whole interval.
    private long _fastUntil;
    private long _wakes;
    private A2dpBridgeConnection? _connection;
    private Task _loop = Task.CompletedTask;
    private BridgeStatus _status = BridgeStatus.NotRunning;

    /// <param name="instance">The core's <c>BLUECASTER_INSTANCE</c> (a side-by-side test core); null for the normal one.</param>
    public A2dpBridgeMonitor(ILogger? logger = null, string? instance = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _instance = instance;
        _client = $"encodeck/{SemanticVersion.FromAssembly(typeof(A2dpBridgeMonitor).Assembly)}";
    }

    /// <summary>How often to look for a core while none runs.</summary>
    public TimeSpan IdleInterval { get; set; } = TimeSpan.FromSeconds(10);

    public BridgeStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Raised on a background thread whenever <see cref="Status"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>BlueCaster's pipe (3.0 and later), <c>bluecaster-&lt;SID&gt;</c>.</summary>
    public static string PipeName(string? instance = null) => $"bluecaster-{WindowsIdentity.GetCurrent().User?.Value}{Suffix(instance)}";

    /// <summary>The pipe of A2DP Windows Bridge 2.0.x, <c>a2dpwb-&lt;SID&gt;</c>, which BlueCaster serves as well for a while.</summary>
    public static string LegacyPipeName(string? instance = null) => $"a2dpwb-{WindowsIdentity.GetCurrent().User?.Value}{Suffix(instance)}";

    /// <summary>BlueCaster's single-instance mutex.</summary>
    public static string MutexName(string? instance = null) => $@"Local\BlueCaster_Core{Suffix(instance)}";

    /// <summary>The mutex of a 2.0.x core (a BlueCaster core holds it too, for a while).</summary>
    public static string LegacyMutexName(string? instance = null) => $@"Local\A2DPWB_v2_Core{Suffix(instance)}";

    private static string Suffix(string? instance) => string.IsNullOrEmpty(instance) ? "" : "-" + instance;

    /// <summary>Whether a core (BlueCaster, or A2DP Windows Bridge 2.0.x) runs in this session.</summary>
    public static bool IsCoreRunning(string? instance = null) => RunningPipe(instance) is not null;

    /// <summary>
    /// The pipe of the core that runs in this session: BlueCaster's own name when its mutex is there, the 2.0.x name
    /// when only a 2.0.x core runs; null when none does.
    /// </summary>
    internal static string? RunningPipe(string? instance = null) =>
        MutexExists(MutexName(instance)) ? PipeName(instance) : MutexExists(LegacyMutexName(instance)) ? LegacyPipeName(instance) : null;

    /// <summary>
    /// The handle is closed at once: holding it would keep the mutex alive after the core exits and make the next
    /// core believe another one runs.
    /// </summary>
    private static bool MutexExists(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return false; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (!_loop.IsCompleted) return;
            _loop = Task.Run(RunAsync);
        }
    }

    /// <summary>Looks for the core now instead of at the next interval (after starting it, or when its page opens).</summary>
    public void Wake()
    {
        TaskCompletionSource wake;
        lock (_gate)
        {
            wake = _wake;
            _wake = NewWake();
            _fastUntil = Environment.TickCount64 + 15_000;
            _wakes++;
        }
        wake.TrySetResult();
    }

    /// <summary>Waits until a session is open (true) or the timeout passes.</summary>
    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        deadline.CancelAfter(timeout);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? s, EventArgs e) { if (Status.IsConnected) connected.TrySetResult(); }
        Changed += OnChanged;
        try
        {
            if (Status.IsConnected) return true;
            Wake();
            await connected.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Status.IsConnected; }
        finally { Changed -= OnChanged; }
    }

    /// <summary>A request to the connected core; <see cref="BridgeUnavailableException"/> when there is none.</summary>
    public Task<JsonNode?> RequestAsync(string method, JsonNode? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        A2dpBridgeConnection? connection;
        lock (_gate) connection = _connection;
        return connection is null
            ? Task.FromException<JsonNode?>(new BridgeUnavailableException())
            : connection.RequestAsync(method, parameters, timeout ?? TimeSpan.FromSeconds(15), ct);
    }

    /// <summary>
    /// An RFCOMM channel from the core's adapter to the device it streams to (see <see cref="BridgeStatus.Rfcomm"/>);
    /// <see cref="BridgeUnavailableException"/> when no core is connected, <see cref="NotSupportedException"/> when it
    /// is older than 2.0.2. The channel ends with the session, the stream or the A2DP connection.
    /// </summary>
    public Task<BridgeRfcommChannel> OpenRfcommAsync(string address, Guid service, CancellationToken ct = default)
    {
        A2dpBridgeConnection? connection;
        lock (_gate) connection = _connection;
        if (connection is null) return Task.FromException<BridgeRfcommChannel>(new BridgeUnavailableException());
        // Cores before 2.0.2 close the session on a request they do not know.
        if (!connection.Features.Contains(A2dpBridgeConnection.RfcommFeature))
            return Task.FromException<BridgeRfcommChannel>(new NotSupportedException("This BlueCaster core cannot open RFCOMM channels."));
        return connection.OpenRfcommAsync(address, service, ct);
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        var stopped = Task.Delay(Timeout.Infinite, token);
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!IsCoreRunning(_instance))
                {
                    SetStatus(BridgeStatus.NotRunning);
                    bool fast;
                    lock (_gate) fast = Environment.TickCount64 < _fastUntil;
                    await IdleAsync(fast ? TimeSpan.FromMilliseconds(250) : IdleInterval, token).ConfigureAwait(false);
                    continue;
                }
                SetStatus(new BridgeStatus(BridgeLink.Connecting));
                var connection = await ConnectAsync(token).ConfigureAwait(false);
                if (connection is null)
                {
                    // Running but no pipe yet (just started, or still starting up): look again soon.
                    SetStatus(BridgeStatus.NotRunning);
                    await IdleAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    continue;
                }
                await using (connection)
                {
                    lock (_gate) _connection = connection;
                    _logger.LogInformation("BlueCaster connected ({Server})", connection.Server);
                    connection.Changed += _ => SetConnected(connection);
                    SetConnected(connection);
                    await Task.WhenAny(connection.Completion, stopped).ConfigureAwait(false);
                    lock (_gate) _connection = null;
                }
                _logger.LogInformation("BlueCaster disconnected");
                SetStatus(BridgeStatus.NotRunning);
                // The core is exiting or restarting: give it a moment before looking again.
                await IdleAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
            catch (BridgeRejectedException ex)
            {
                _logger.LogWarning("BlueCaster refused the connection: {Reason}", ex.Message);
                SetStatus(new BridgeStatus(BridgeLink.Incompatible, Detail: ex.Message));
                await IdleAsync(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("BlueCaster session failed: {Error}", ex.Message);
                SetStatus(BridgeStatus.NotRunning);
                try { await IdleAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<A2dpBridgeConnection?> ConnectAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            // Asked again each time: a 2.0.x core may have just been replaced by BlueCaster (or the other way round).
            if (RunningPipe(_instance) is not { } pipe) return null;
            try
            {
                return await A2dpBridgeConnection.OpenAsync(pipe, _client, TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or IOException and not BridgeRejectedException)
            {
                await Task.Delay(400, token).ConfigureAwait(false);
            }
        }
        return null;
    }

    private long _seenWakes;

    private async Task IdleAsync(TimeSpan delay, CancellationToken token)
    {
        Task wake;
        lock (_gate)
        {
            // A wake while the loop was busy (not waiting) is not lost: look again at once.
            if (_wakes != _seenWakes)
            {
                _seenWakes = _wakes;
                return;
            }
            wake = _wake.Task;
        }
        await Task.WhenAny(wake, Task.Delay(delay, token)).ConfigureAwait(false);
        lock (_gate) _seenWakes = _wakes;
        token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The connection's snapshot is read under the status lock, so whichever of the reader's change and the first
    /// publication runs last sets the newest state (the reader replaces the snapshot before it raises Changed).
    /// </summary>
    private void SetConnected(A2dpBridgeConnection connection)
    {
        lock (_gate) _status = new BridgeStatus(BridgeLink.Connected, connection.Snapshot, connection.Server,
            Rfcomm: connection.Features.Contains(A2dpBridgeConnection.RfcommFeature));
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogWarning(ex, "BlueCaster status handler failed"); }
    }

    private void SetStatus(BridgeStatus status)
    {
        lock (_gate)
        {
            if (_status == status) return;
            _status = status;
        }
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogWarning(ex, "BlueCaster status handler failed"); }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        Wake();
        try { await _loop.ConfigureAwait(false); } catch (Exception) { }
        _stop.Dispose();
    }
}
