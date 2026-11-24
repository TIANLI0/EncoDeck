// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EncoDeck.App.Services;
using EncoDeck.Core.Configuration;
using EncoDeck.Platform.Resources;
using EncoDeck.Platform.Settings;
using EncoDeck.Platform.Shell;
using EncoDeck.Platform.Startup;
using EncoDeck.Platform.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace EncoDeck.App.ViewModels;

/// <summary>
/// Two-way projection of <see cref="AppSettings"/>; every change is persisted immediately, except the slider-bound
/// values, which are saved once the slider rests (<see cref="SliderPersistDelay"/>; see <see cref="FlushPendingSettings"/>). Also shows the
/// device artwork download and app updates, whose status arrives on background threads and is applied here
/// on the UI thread (coalesced, so a fast download never floods the dispatcher).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] Languages = ["", "zh-CN", "en-US", "ja-JP"];

    /// <summary>
    /// A dragged slider changes its value at every step; saving each step wrote settings.json on the UI thread and
    /// woke every settings subscriber. The value is kept here and saved once it rests this long.
    /// </summary>
    private static readonly TimeSpan SliderPersistDelay = TimeSpan.FromMilliseconds(400);

    private readonly ISettingsStore _settings;
    private readonly StartupRegistration _startup;
    private readonly DeviceResourceSync _artwork;
    private readonly string _artworkRoot;
    private readonly UpdateManager _updates;
    private readonly StoreUpdateService _storeUpdates;
    private readonly Func<nint> _ownerWindow;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _exit;
    private readonly ILogger _logger;
    private readonly string _executablePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "EncoDeck.exe");
    private int _artworkQueued;
    private int _updatesQueued;
    private Debouncer? _sliderPersist;
    // Slider values not saved yet (null: the stored value is current).
    private double? _pendingPopupScalePercent;
    private double? _pendingPopupDurationSeconds;
    private int? _pendingLowBatteryThreshold;

    /// <param name="artworkRoot">Opened by "Open folder" while nothing is cached for the device yet.</param>
    /// <param name="exit">The app's clean shutdown, used after the installer has been started.</param>
    public SettingsViewModel(ISettingsStore settings, StartupRegistration startup, DeviceResourceSync artwork, string artworkRoot,
        UpdateManager updates, StoreUpdateService storeUpdates, Func<nint> ownerWindow, DispatcherQueue dispatcher, Action exit, ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _startup = startup;
        _artwork = artwork;
        _artworkRoot = artworkRoot;
        _updates = updates;
        _storeUpdates = storeUpdates;
        _ownerWindow = ownerWindow;
        _dispatcher = dispatcher;
        _exit = exit;
        _logger = logger;
        _artwork.StatusChanged += (_, _) =>
        {
            if (Interlocked.Exchange(ref _artworkQueued, 1) == 0)
                _dispatcher.TryEnqueue(() => { Volatile.Write(ref _artworkQueued, 0); ApplyArtwork(); });
        };
        _updates.StatusChanged += (_, _) =>
        {
            if (Interlocked.Exchange(ref _updatesQueued, 1) == 0)
                _dispatcher.TryEnqueue(() => { Volatile.Write(ref _updatesQueued, 0); ApplyUpdates(); });
        };
        ApplyArtwork();
        ApplyUpdates();
    }

    private AppSettings S => _settings.Current;

    private void Update(Func<AppSettings, AppSettings> change, string property)
    {
        _settings.Update(change);
        OnPropertyChanged(property);
    }

    /// <summary>A slider value: shown at once (the getter returns the pending value), saved once the slider rests.</summary>
    private void UpdateLater(Action keep, string property)
    {
        keep();
        OnPropertyChanged(property);
        _sliderPersist ??= new Debouncer(_dispatcher, SliderPersistDelay, _ =>
        {
            PersistSliders();
            return Task.CompletedTask;
        });
        _sliderPersist.Restart();
    }

    /// <summary>
    /// Saves slider values still waiting for their delay. Called when the settings page unloads and before anything
    /// reads them from the store (the popup preview); the app's exit flushes every <see cref="Debouncer"/> too.
    /// </summary>
    public void FlushPendingSettings() => _sliderPersist?.Flush();

    private void PersistSliders()
    {
        double? scale = _pendingPopupScalePercent, duration = _pendingPopupDurationSeconds;
        int? threshold = _pendingLowBatteryThreshold;
        _pendingPopupScalePercent = _pendingPopupDurationSeconds = null;
        _pendingLowBatteryThreshold = null;
        if (scale is null && duration is null && threshold is null) return;
        _settings.Update(s => s with
        {
            PopupScale = scale is { } percent ? percent / 100.0 : s.PopupScale,
            PopupDurationSeconds = duration ?? s.PopupDurationSeconds,
            LowBatteryThreshold = threshold ?? s.LowBatteryThreshold,
        });
    }

    public bool StartWithWindows
    {
        get => _startup.IsEnabled(_executablePath);
        set
        {
            try
            {
                _startup.SetEnabled(_executablePath, value);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                _logger.LogWarning(ex, "Could not change start-up registration");
            }
            Update(s => s with { StartWithWindows = value }, nameof(StartWithWindows));
        }
    }

    public bool KeepRunningInTray
    {
        get => S.KeepRunningInTray;
        set => Update(s => s with { KeepRunningInTray = value }, nameof(KeepRunningInTray));
    }

    /// <summary>The panel on top of Quick Settings needs Windows 11; the switch is disabled elsewhere.</summary>
    public bool IsQuickSettingsPanelSupported => QuickSettingsWatcher.IsSupported;

    public bool ShowAboveQuickSettings
    {
        get => S.ShowAboveQuickSettings != false && IsQuickSettingsPanelSupported;
        set => Update(s => s with { ShowAboveQuickSettings = value }, nameof(ShowAboveQuickSettings));
    }

    public bool HideQuickSettingsPanelWhenBluetoothOff
    {
        get => S.HideQuickSettingsPanelWhenBluetoothOff != false;
        set => Update(s => s with { HideQuickSettingsPanelWhenBluetoothOff = value }, nameof(HideQuickSettingsPanelWhenBluetoothOff));
    }

    public int QuickSettingsPanelThemeIndex
    {
        get => (int)S.QuickSettingsPanelTheme;
        set => Update(s => s with { QuickSettingsPanelTheme = (AppTheme)Math.Clamp(value, 0, 2) }, nameof(QuickSettingsPanelThemeIndex));
    }

    /// <summary>The action's hotkey as stored (<see cref="Hotkey"/> text), or null.</summary>
    public string? GetHotkey(HotkeyAction action) => S.Hotkeys?.Get(action);

    /// <summary>Sets or (null) removes a hotkey. A combination does one thing: any other action using it loses it.</summary>
    public void SetHotkey(HotkeyAction action, Hotkey? hotkey)
    {
        string? text = hotkey?.ToString();
        _settings.Update(s =>
        {
            var bindings = s.Hotkeys ?? new HotkeyBindings();
            if (text is not null)
            {
                foreach (var other in Enum.GetValues<HotkeyAction>())
                {
                    if (other != action && bindings.Get(other) == text) bindings = bindings.With(other, null);
                }
            }
            return s with { Hotkeys = bindings.With(action, text) };
        });
    }

    public bool ShowConnectionPopup
    {
        get => S.ShowConnectionPopup;
        set => Update(s => s with { ShowConnectionPopup = value }, nameof(ShowConnectionPopup));
    }

    /// <summary>Index into <see cref="PopupPosition"/> (combo box order matches the enum).</summary>
    public int PopupPositionIndex
    {
        get => (int)S.PopupPosition;
        set => Update(s => s with { PopupPosition = (PopupPosition)Math.Clamp(value, 0, (int)PopupPosition.TopRight) }, nameof(PopupPositionIndex));
    }

    /// <summary>Popup size in percent of its standard size (60–160).</summary>
    public double PopupScalePercent
    {
        get => _pendingPopupScalePercent ?? Math.Round(S.PopupScale * 100);
        set => UpdateLater(() => _pendingPopupScalePercent = Math.Round(value), nameof(PopupScalePercent));
    }

    public double PopupDurationSeconds
    {
        get => _pendingPopupDurationSeconds ?? S.PopupDurationSeconds;
        set => UpdateLater(() => _pendingPopupDurationSeconds = Math.Round(value, 1), nameof(PopupDurationSeconds));
    }

    public bool SuppressPopupWhenFullscreen
    {
        get => S.SuppressPopupWhenFullscreen;
        set => Update(s => s with { SuppressPopupWhenFullscreen = value }, nameof(SuppressPopupWhenFullscreen));
    }

    public bool LowBatteryNotification
    {
        get => S.LowBatteryNotification;
        set => Update(s => s with { LowBatteryNotification = value }, nameof(LowBatteryNotification));
    }

    public double LowBatteryThreshold
    {
        get => _pendingLowBatteryThreshold ?? S.LowBatteryThreshold;
        set => UpdateLater(() => _pendingLowBatteryThreshold = (int)Math.Round(value), nameof(LowBatteryThreshold));
    }

    public bool RawProtocolLogging
    {
        get => S.RawProtocolLogging;
        set => Update(s => s with { RawProtocolLogging = value }, nameof(RawProtocolLogging));
    }

    public int ThemeIndex
    {
        get => (int)S.Theme;
        set => Update(s => s with { Theme = (AppTheme)Math.Clamp(value, 0, 2) }, nameof(ThemeIndex));
    }

    public int LanguageIndex
    {
        get => Math.Max(0, Array.IndexOf(Languages, S.Language ?? string.Empty));
        set => Update(s => s with { Language = value > 0 && value < Languages.Length ? Languages[value] : null }, nameof(LanguageIndex));
    }

    /// <summary>The informational version (VersionPrefix-VersionSuffix), e.g. "0.5.0" or "0.6.0-beta.1".</summary>
    public string Version => _updates.CurrentVersion.ToString();

    public string LogDirectory => App.Instance.Host.Paths.LogDirectory;

    // ---- Device artwork -------------------------------------------------------------------------

    [ObservableProperty] public partial string ArtworkDevice { get; set; } = string.Empty;
    [ObservableProperty] public partial string ArtworkStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsArtworkRunning { get; set; }
    [ObservableProperty] public partial bool IsArtworkProgressIndeterminate { get; set; } = true;
    [ObservableProperty] public partial double ArtworkProgress { get; set; }
    [ObservableProperty] public partial string ArtworkUpdated { get; set; } = string.Empty;
    [ObservableProperty] public partial string ArtworkCache { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedownloadArtworkCommand))]
    public partial bool CanRedownloadArtwork { get; set; }

    private void ApplyArtwork()
    {
        var status = _artwork.Status;
        var key = status.Key;
        ArtworkDevice = key is { ProductId: { } pid, ColorId: { } color }
            ? Loc.Format("Artwork_Device", string.IsNullOrWhiteSpace(status.DeviceName) ? key.ModelId : status.DeviceName, pid.ToString("X6", CultureInfo.InvariantCulture), color)
            : key is { CanDownload: true } ? key.ModelName ?? status.DeviceName ?? key.ModelId : Loc.Get("Artwork_NoDevice");
        ArtworkStatus = status.Stage switch
        {
            ResourceSyncStage.Checking => Loc.Get("Artwork_Checking"),
            ResourceSyncStage.Downloading when status.Total is > 0 => Loc.Format("Artwork_DownloadingBytes", status.Package, status.Packages, Bytes(status.Received), Bytes(status.Total.Value)),
            ResourceSyncStage.Downloading when status.Received > 0 => Loc.Format("Artwork_DownloadingBytes", status.Package, status.Packages, Bytes(status.Received), "?"),
            ResourceSyncStage.Downloading => Loc.Format("Artwork_Downloading", status.Package, status.Packages),
            ResourceSyncStage.Extracting => Loc.Format("Artwork_Extracting", status.Package, status.Packages),
            ResourceSyncStage.Completed => Loc.Get("Artwork_Completed"),
            ResourceSyncStage.UpToDate => Loc.Get("Artwork_UpToDate"),
            ResourceSyncStage.Failed => Loc.Get(status.Failure switch
            {
                ResourceSyncFailure.Offline => "Artwork_Error_Offline",
                ResourceSyncFailure.Server => "Artwork_Error_Server",
                ResourceSyncFailure.NotAvailable => "Artwork_Error_NotAvailable",
                ResourceSyncFailure.InvalidData => "Artwork_Error_InvalidData",
                ResourceSyncFailure.Storage => "Artwork_Error_Storage",
                _ => "Artwork_Error_Other",
            }) + (status.Failure != ResourceSyncFailure.NotAvailable && status.Cache is not null ? " " + Loc.Get("Artwork_KeepingCache") : string.Empty),
            _ => key is null ? string.Empty : status.Cache is null ? Loc.Get("Artwork_NotDownloaded") : Loc.Get("Artwork_Cached"),
        };
        IsArtworkRunning = status.IsRunning;
        IsArtworkProgressIndeterminate = !(status.Stage == ResourceSyncStage.Downloading && status.Total is > 0);
        ArtworkProgress = status.Total is > 0 ? Math.Clamp(100.0 * status.Received / status.Total.Value, 0, 100) : 0;
        ArtworkUpdated = status.Cache?.UpdatedAt is { } updated ? updated.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "—";
        ArtworkCache = status.Cache is { } cache ? Loc.Format("Artwork_CacheSummary", cache.Files, Bytes(cache.Bytes)) : "—";
        CanRedownloadArtwork = key is { CanDownload: true } && !status.IsRunning;
    }

    [RelayCommand(CanExecute = nameof(CanRedownloadArtwork))]
    private async Task RedownloadArtworkAsync()
    {
        if (_artwork.Status.Key is not { } key) return;
        CanRedownloadArtwork = false;
        try
        {
            await _artwork.RefreshAsync(key, force: true);
        }
        catch (Exception ex)
        {
            // The status line explains the failure; nothing else to show.
            _logger.LogInformation("Manual artwork download failed ({Reason})", ex.GetType().Name);
        }
        ApplyArtwork();
    }

    [RelayCommand]
    private void OpenArtworkFolder()
    {
        string folder = _artwork.Status.Cache?.Folder is { } cached && Directory.Exists(cached) ? cached : _artworkRoot;
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning("Could not open the artwork folder ({Reason})", ex.GetType().Name);
        }
    }

    // ---- Updates --------------------------------------------------------------------------------

    /// <summary>False for the MSIX (Store) build, which the Store keeps up to date.</summary>
    public bool HasSelfUpdate { get; } = !EncoDeck.Platform.Packaging.PackageIdentity.IsPackaged;

    public string CurrentVersionText => Loc.Format("Update_CurrentVersion", Version);

    public bool AutoCheckUpdates
    {
        get => S.AutoCheckUpdates != false;
        set => Update(s => s with { AutoCheckUpdates = value }, nameof(AutoCheckUpdates));
    }

    public bool IncludePrereleaseUpdates
    {
        get => S.IncludePrereleaseUpdates;
        set => Update(s => s with { IncludePrereleaseUpdates = value }, nameof(IncludePrereleaseUpdates));
    }

    [ObservableProperty] public partial string UpdateStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsUpdateDownloading { get; set; }
    [ObservableProperty] public partial double UpdateProgress { get; set; }
    [ObservableProperty] public partial string UpdateActionText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsUpdateActionVisible { get; set; }
    [ObservableProperty] public partial Uri? ReleaseNotesUrl { get; set; }
    [ObservableProperty] public partial bool HasReleaseNotes { get; set; }

    /// <summary>
    /// A newer version was found (a GitHub release, or the Store's update): the About entry shows a red
    /// dot until it is installed.
    /// </summary>
    [ObservableProperty] public partial bool IsUpdateAvailable { get; set; }

    /// <summary>The offered release's notes from GitHub, as plain text; empty when none is offered.</summary>
    [ObservableProperty] public partial string UpdateReleaseNotes { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(RunUpdateActionCommand))]
    public partial bool IsUpdateIdle { get; set; } = true;

    private void ApplyUpdates()
    {
        var status = _updates.Status;
        string? version = status.Release?.Version.ToString();
        string text = status.Stage switch
        {
            UpdateStage.Checking => Loc.Get("Update_Checking"),
            UpdateStage.UpToDate => Loc.Get("Update_UpToDate"),
            UpdateStage.Available => Loc.Format("Update_Available", version),
            UpdateStage.Downloading => Loc.Format("Update_Downloading", version, Bytes(status.Received), Bytes(status.Total)),
            UpdateStage.Ready => Loc.Format("Update_Ready", version),
            UpdateStage.Installing => Loc.Get("Update_Installing"),
            UpdateStage.Failed => Loc.Get(status.Failure switch
            {
                UpdateFailure.Offline => "Update_Error_Offline",
                UpdateFailure.RateLimited => "Update_Error_RateLimited",
                UpdateFailure.NotFound => "Update_Error_NotFound",
                UpdateFailure.Verification => "Update_Error_Verification",
                UpdateFailure.Storage => "Update_Error_Storage",
                _ => "Update_Error_Other",
            }),
            _ => status.LastChecked is null ? Loc.Get("Update_NeverChecked") : string.Empty,
        };
        if (!status.IsBusy && status.LastChecked is { } last)
            text = (text.Length > 0 ? text + " " : string.Empty) + Loc.Format("Update_LastChecked", last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        UpdateStatus = text;
        IsUpdateDownloading = status.Stage == UpdateStage.Downloading;
        UpdateProgress = status.Total > 0 ? Math.Clamp(100.0 * status.Received / status.Total, 0, 100) : 0;
        // A failed download keeps its release, so the same button retries it.
        bool canDownload = status.Release is not null && status.Stage is UpdateStage.Available or UpdateStage.Failed;
        IsUpdateActionVisible = canDownload || status.Stage == UpdateStage.Ready;
        // Kept while a later check runs, so the dot does not blink; a failed download still offers the release.
        if (HasSelfUpdate && status.Stage != UpdateStage.Checking)
            IsUpdateAvailable = status.Release is not null
                && status.Stage is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.Ready or UpdateStage.Installing or UpdateStage.Failed;
        UpdateActionText = Loc.Get(status.Stage == UpdateStage.Ready ? "Update_InstallNow" : "Update_DownloadAndInstall");
        ReleaseNotesUrl = status.Release?.NotesUrl;
        HasReleaseNotes = ReleaseNotesUrl is not null;
        UpdateReleaseNotes = status.Release?.Notes ?? string.Empty;
        IsUpdateIdle = !status.IsBusy;
    }

    [RelayCommand(CanExecute = nameof(IsUpdateIdle))]
    private async Task CheckForUpdatesAsync()
    {
        IsUpdateIdle = false;
        try { await _updates.CheckNowAsync(); }
        catch (Exception ex) { _logger.LogWarning("Update check failed ({Reason})", ex.GetType().Name); }
        ApplyUpdates();
    }

    /// <summary>"Download and install" (downloads, then installs) or "Restart and update" (installs the downloaded version).</summary>
    [RelayCommand(CanExecute = nameof(IsUpdateIdle))]
    private async Task RunUpdateActionAsync()
    {
        IsUpdateIdle = false;
        try
        {
            if (_updates.Status.Stage != UpdateStage.Ready && await _updates.DownloadAsync() is null) return;
            // The user asked for it: start the installer, then leave the way "Exit" does so it can replace the files.
            if (_updates.TryStartInstaller()) _exit();
        }
        catch (Exception ex) { _logger.LogWarning("Update failed ({Reason})", ex.GetType().Name); }
        finally { ApplyUpdates(); }
    }

    // ---- Store updates (MSIX build) ------------------------------------------------------------------

    [ObservableProperty] public partial string StoreUpdateStatus { get; set; } = Loc.Get("Update_NeverChecked");
    [ObservableProperty] public partial bool IsStoreUpdateAvailable { get; set; }

    partial void OnIsStoreUpdateAvailableChanged(bool value)
    {
        if (!HasSelfUpdate) IsUpdateAvailable = value;
    }

    /// <summary>The listing's "What's new" text while an update is available; empty otherwise.</summary>
    [ObservableProperty] public partial string StoreReleaseNotes { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckStoreUpdatesCommand), nameof(InstallStoreUpdateCommand))]
    public partial bool IsStoreUpdateIdle { get; set; } = true;

    [RelayCommand(CanExecute = nameof(IsStoreUpdateIdle))]
    private async Task CheckStoreUpdatesAsync()
    {
        IsStoreUpdateIdle = false;
        StoreUpdateStatus = Loc.Get("Update_Checking");
        try
        {
            IsStoreUpdateAvailable = await _storeUpdates.CheckAsync() > 0;
            StoreUpdateStatus = Loc.Get(IsStoreUpdateAvailable ? "StoreUpdate_Available" : "Update_UpToDate");
            StoreReleaseNotes = IsStoreUpdateAvailable ? await _storeUpdates.GetReleaseNotesAsync() ?? string.Empty : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Store update check failed ({Reason})", ex.GetType().Name);
            StoreUpdateStatus = Loc.Get("Update_Error_Other");
        }
        finally { IsStoreUpdateIdle = true; }
    }

    /// <summary>Hands the download and install to the Store, which restarts EncoDeck when it is done.</summary>
    [RelayCommand(CanExecute = nameof(IsStoreUpdateIdle))]
    private async Task InstallStoreUpdateAsync()
    {
        IsStoreUpdateIdle = false;
        StoreUpdateStatus = Loc.Get("Update_Installing");
        try
        {
            bool installed = await _storeUpdates.InstallAsync(_ownerWindow());
            if (!installed) await CheckStoreUpdatesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Store update failed ({Reason})", ex.GetType().Name);
            StoreUpdateStatus = Loc.Get("Update_Error_Other");
        }
        finally { IsStoreUpdateIdle = true; }
    }

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString(CultureInfo.CurrentCulture) + " B",
    };
}
