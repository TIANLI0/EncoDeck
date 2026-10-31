// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Core.Configuration;
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;
using EncoDeck.Platform.LeAudioSync;
using EncoDeck.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// Feeds the LE Audio sync guard (Labs): the switch, and the active earbuds' LE Audio endpoints while both earbuds are
/// linked over LE (only two CIS can start a frame apart). The guard itself refuses PCs without an Intel radio on the
/// SST offload path.
/// </summary>
public sealed class LeAudioSyncCompanion : IAsyncDisposable
{
    private static readonly TimeSpan ResolveInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryAfterLoss = TimeSpan.FromSeconds(10);
    private const int ResolveAttempts = 15;

    private readonly IDeviceStateStore _store;
    private readonly ISettingsStore _settings;
    private readonly ILogger _logger;
    private readonly WindowsAudioEndpointPlatform _endpoints = new();
    private readonly object _gate = new();
    private ulong? _target;
    // Non-null while a lookup runs or is scheduled.
    private CancellationTokenSource? _resolve;
    private DateTimeOffset _lastResolve = DateTimeOffset.MinValue;
    private bool _started;

    public LeAudioSyncCompanion(IDeviceStateStore store, ISettingsStore settings, ILogger logger)
    {
        _store = store;
        _settings = settings;
        _logger = logger;
        Guard = new LeAudioSyncGuard(new WindowsLeAudioSyncPlatform(), logger);
        Guard.Changed += OnGuardChanged;
    }

    public LeAudioSyncGuard Guard { get; }

    /// <summary>Whether this PC qualifies; null until checked at start.</summary>
    public LeAudioSyncSupport? Support { get; private set; }

    /// <summary>
    /// The active device plays over LE Audio (LC3) with both earbud links up. The Labs LE Audio adjustments (this
    /// calibration and the bitrate) are only offered then.
    /// </summary>
    public bool LeAudioConnected
    {
        get { lock (_gate) return _target is not null; }
    }

    /// <summary>Raised on a background thread when <see cref="Support"/>, <see cref="LeAudioConnected"/> or the guard's status changes.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _settings.Changed += OnSettingsChanged;
        _store.StateChanged += OnStateChanged;
        _ = Task.Run(() =>
        {
            try
            {
                Support = LeAudioSyncEligibility.Check();
                _logger.LogInformation("LE Audio sync support: {Support}", Support);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not check LE Audio sync support");
                Support = LeAudioSyncSupport.NoRadio;
            }
            Changed?.Invoke(this, EventArgs.Empty);
            Guard.SetEnabled(_settings.Current.LeAudioSyncGuard);
            UpdateTarget(force: true);
        });
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => Guard.SetEnabled(settings.LeAudioSyncGuard);

    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs e) => UpdateTarget(force: false);

    private void UpdateTarget(bool force)
    {
        DeviceState state = _store.Current;
        ulong? target = state is { IsLeAudio: true, LeAudioLinks.BothConnected: true, Device: { Transport: DeviceTransport.Bluetooth } device }
            ? device.Address.Value
            : null;
        bool changed;
        lock (_gate)
        {
            changed = target != _target;
            if (!force && !changed) return;
            _target = target;
        }
        StartResolve(target);
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StartResolve(ulong? address, TimeSpan delay = default)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _resolve?.Cancel();
            _resolve?.Dispose();
            _resolve = null;
            if (address is null)
            {
                Guard.SetEndpoints(null);
                return;
            }
            _resolve = cts = new CancellationTokenSource();
            _lastResolve = DateTimeOffset.UtcNow + delay;
        }
        _ = ResolveAsync(address.Value, delay, cts.Token);
    }

    /// <summary>The LE Audio endpoints can appear a few seconds after the links; look for them for half a minute.</summary>
    private async Task ResolveAsync(ulong address, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            for (int attempt = 0; attempt < ResolveAttempts; attempt++)
            {
                try
                {
                    var inspection = await LeAudioClassicAudioEndpoint.InspectAsync(address, _endpoints, cancellationToken).ConfigureAwait(false);
                    if (inspection.LeAudioRenderPresent)
                    {
                        string render = WindowsAudioEndpointPlatform.ToMmDeviceId(inspection.LeAudioRender!.InstanceId);
                        var captures = await _endpoints.GetCaptureEndpointsAsync(cancellationToken).ConfigureAwait(false);
                        var capture = captures.Where(c => c is { Kind: AudioEndpointKind.LeAudioCapture, IsDisabled: false } && c.BelongsTo(address))
                            .Select(c => WindowsAudioEndpointPlatform.ToMmDeviceId(c.InstanceId))
                            .FirstOrDefault();
                        Guard.SetEndpoints(new LeAudioEndpoints(render, capture));
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "LE Audio endpoint lookup failed");
                }
                await Task.Delay(ResolveInterval, cancellationToken).ConfigureAwait(false);
            }
            Guard.SetEndpoints(null);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Done: no lookup is running any more (unless a newer one replaced this one).
            lock (_gate)
            {
                if (_resolve is not null && _resolve.Token == cancellationToken)
                {
                    _resolve.Dispose();
                    _resolve = null;
                }
            }
        }
    }

    private void OnGuardChanged(object? sender, EventArgs e)
    {
        // The held stream lost its endpoint (the guard forgets it): look again while the earbuds stay linked, at most
        // every ten seconds so a stream that keeps failing to open cannot spin.
        if (Guard.Status.State == LeAudioSyncState.WaitingForDevice)
        {
            ulong? target;
            TimeSpan wait;
            lock (_gate)
            {
                target = _resolve is null ? _target : null;
                wait = RetryAfterLoss - (DateTimeOffset.UtcNow - _lastResolve);
            }
            if (target is not null) StartResolve(target, wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        _store.StateChanged -= OnStateChanged;
        lock (_gate)
        {
            _resolve?.Cancel();
            _resolve?.Dispose();
            _resolve = null;
        }
        await Guard.DisposeAsync().ConfigureAwait(false);
    }
}
