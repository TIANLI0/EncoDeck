// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Cups;
using EncoDeck.Bluetooth.DokiDoki;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// The experimental cup category: scanning every cup brand, the one session, and the last level
/// sent per channel. Separate from the headset runtime. Events are raised on arbitrary threads.
/// Output is zeroed on disconnect, sleep, exit, and when the opt-in is switched off.
/// </summary>
public sealed class CupService : IAsyncDisposable
{
    private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(3);

    private readonly ISettingsStore _settings;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly ICupBrand[] _brands;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<ActuatorChannel, int> _levels = [];
    private ICupSession? _session;
    private CancellationTokenSource? _scanTimer;
    private string? _scanError;
    private string? _lastSessionError;
    private volatile bool _shownOnHome;
    private bool _enabled;
    private int _disposed;

    public CupService(ISettingsStore settings, ILoggerFactory loggers)
    {
        _settings = settings;
        _loggers = loggers;
        _logger = loggers.CreateLogger<CupService>();
        _brands = [new DokiDokiCupBrand(loggers)];
        foreach (var brand in _brands)
        {
            brand.DevicesChanged += OnBrandDevicesChanged;
            brand.ScanAborted += OnScanAborted;
        }
        _enabled = settings.Current.EnableExperimentalCupDevices;
        _settings.Changed += OnSettingsChanged;
    }

    public bool IsEnabled => _settings.Current.EnableExperimentalCupDevices;

    public IReadOnlyList<ICupBrand> Brands => _brands;

    public bool IsScanning => _brands.Any(b => b.IsScanning);

    public string? ScanError => _scanError;

    public IReadOnlyList<CupAdvertisement> Devices => _brands.SelectMany(b => b.Devices).OrderByDescending(d => d.Rssi).ToList();

    public ICupSession? Session => _session;

    /// <summary>Why the last session ended on its own (connection gave up), until the next connect.</summary>
    public string? LastSessionError => _lastSessionError;

    /// <summary>
    /// Home shows the cup view instead of the headset (the switcher shows one device at a time).
    /// Set from the switcher and on connect; cleared when a headset is picked or the session ends.
    /// </summary>
    public bool IsShownOnHome => _shownOnHome && IsEnabled && _session is not null;

    /// <summary>Session, connection state, scan state, levels, the opt-in or the Home view changed.</summary>
    public event EventHandler? Changed;

    /// <summary>A device appeared or its name / model changed (not raised for RSSI-only updates).</summary>
    public event EventHandler? DevicesChanged;

    public ICupBrand? BrandOf(string brandId) => _brands.FirstOrDefault(b => b.Id == brandId);

    public void ShowOnHome(bool show)
    {
        if (_shownOnHome == show) return;
        _shownOnHome = show;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public int LevelOf(ActuatorChannel channel)
    {
        lock (_gate) return _levels.GetValueOrDefault(channel);
    }

    public void StartScan()
    {
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0) return;
        _scanError = null;
        try
        {
            foreach (var brand in _brands) brand.StartScan();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cup device scan could not start");
            _scanError = ex.Message;
            foreach (var brand in _brands) brand.StopScan();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        var timer = new CancellationTokenSource();
        Interlocked.Exchange(ref _scanTimer, timer)?.Cancel();
        _ = Task.Delay(ScanDuration, timer.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) StopScan();
        }, TaskScheduler.Default);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void StopScan()
    {
        Interlocked.Exchange(ref _scanTimer, null)?.Cancel();
        foreach (var brand in _brands) brand.StopScan();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces any current session. The caller must have confirmed with the user.</summary>
    public async Task ConnectAsync(CupAdvertisement target, ICupProfile model)
    {
        if (!IsEnabled || BrandOf(target.Brand) is not { } brand) return;
        StopScan();
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseSessionAsync().ConfigureAwait(false);
            if (!IsEnabled || Volatile.Read(ref _disposed) != 0) return;
            var session = brand.CreateSession(target, model, () => _settings.Current.EnableExperimentalCupDevices, _loggers);
            session.Store.StateChanged += OnSessionStateChanged;
            lock (_gate) _levels.Clear();
            _session = session;
            _shownOnHome = true;
            _lastSessionError = null;
            _logger.LogInformation("Cup session: {Device} as {Brand}/{Model}", session.Identity, brand.Id, model.ModelId);
            session.Start();
        }
        finally
        {
            _sessionGate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<CommandResult> SetLevelAsync(ActuatorChannel channel, int level)
    {
        if (_session is not { } session) return CommandResult.NotConnected;
        level = Math.Clamp(level, 0, 100);
        lock (_gate) _levels[channel] = level;
        var result = await session.SetIntensityAsync(channel, level).ConfigureAwait(false);
        if (!result.IsSuccess && result.Status != CommandStatus.Cancelled)
        {
            // The device state is unknown after a failed write; show the slider at zero, not at the request.
            lock (_gate) _levels[channel] = 0;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return result;
    }

    /// <summary>Zeroes every channel of the current session. Safe to call at any time.</summary>
    public async Task<CommandResult> EmergencyStopAsync()
    {
        lock (_gate) _levels.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        if (_session is not { } session) return CommandResult.NotConnected;
        var result = await session.StopAllAsync().ConfigureAwait(false);
        _logger.LogInformation("Cup emergency stop: {Result}", result.Status);
        return result;
    }

    /// <summary>Before sleep: end the session (stop, then disconnect). The user reconnects explicitly afterwards.</summary>
    public void NotifySuspending()
    {
        if (_session is null) return;
        _logger.LogInformation("System suspending; closing the cup session");
        try
        {
            DisconnectAsync().Wait(CloseBudget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the cup session before sleep failed");
        }
    }

    private async Task CloseSessionAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        lock (_gate) _levels.Clear();
        _shownOnHome = false;
        if (session is null) return;
        session.Store.StateChanged -= OnSessionStateChanged;
        // Disposing writes the final stop before the connection closes.
        await session.DisposeAsync().ConfigureAwait(false);
        _logger.LogInformation("Cup session closed");
    }

    private void OnSessionStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        if (e.Previous.Connection == e.Current.Connection && e.Previous.LastError == e.Current.LastError) return;
        // A new connection starts stopped (the controller zeroes every channel at start).
        if (e.Current.Connection != DeviceConnectionState.ControlReady)
            lock (_gate) _levels.Clear();
        // Retries exhausted: the device is gone, so the session ends and leaves the switcher.
        if (e.Current.Connection == DeviceConnectionState.ControlUnavailable && ReferenceEquals(sender, _session?.Store))
        {
            _lastSessionError = e.Current.LastError?.Message ?? string.Empty;
            _logger.LogInformation("Cup device unavailable; ending the session");
            _ = Task.Run(() => EndUnavailableSessionAsync((DeviceStateStore)sender!));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task EndUnavailableSessionAsync(DeviceStateStore store)
    {
        try
        {
            await _sessionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // A newer session may have replaced this one meanwhile.
                if (!ReferenceEquals(_session?.Store, store)) return;
                await CloseSessionAsync().ConfigureAwait(false);
            }
            finally
            {
                _sessionGate.Release();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ending the unavailable cup session failed");
        }
    }

    private void OnBrandDevicesChanged(object? sender, EventArgs e) => DevicesChanged?.Invoke(this, EventArgs.Empty);

    private void OnScanAborted(object? sender, string error)
    {
        _scanError = error;
        StopScan();
    }

    private void OnSettingsChanged(object? sender, EncoDeck.Core.Configuration.AppSettings settings)
    {
        bool enabled = settings.EnableExperimentalCupDevices;
        if (enabled == _enabled) return;
        _enabled = enabled;
        if (!enabled)
        {
            _shownOnHome = false;
            if (IsScanning) StopScan();
            if (_session is not null) _ = DisconnectAsync();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _settings.Changed -= OnSettingsChanged;
        Interlocked.Exchange(ref _scanTimer, null)?.Cancel();
        foreach (var brand in _brands)
        {
            brand.DevicesChanged -= OnBrandDevicesChanged;
            brand.ScanAborted -= OnScanAborted;
            brand.Dispose();
        }
        try
        {
            await DisconnectAsync().WaitAsync(CloseBudget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Cup session did not close within {Budget}", CloseBudget);
        }
    }
}
