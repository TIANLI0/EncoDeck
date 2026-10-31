// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Notifications;
using EncoDeck.Core.State;
using EncoDeck.Platform.Input;
using EncoDeck.Platform.Instance;
using EncoDeck.Platform.Packaging;
using EncoDeck.Platform.Shell;
using EncoDeck.Platform.Startup;
using EncoDeck.Platform.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.System.Power;

namespace EncoDeck.App;

public partial class App : Application
{
    /// <summary>Asks a running instance to shut down cleanly (removes the tray icon, closes the control channel).</summary>
    public const string ExitArgument = "--exit";

    private SingleInstanceGuard? _instance;
    private TrayIconService? _tray;
    private QuickSettingsCompanion? _quickSettings;
    private DispatcherQueue? _dispatcher;
    private ILogger? _logger;
    private UpdateRelease? _pendingUpdateNotice;
    private DispatcherQueueTimer? _noticeTimer;
    private bool _exiting;
    private DateTimeOffset _lastStoreCheck = DateTimeOffset.MinValue;
    private bool _storeUpdateNotified;
    private BrandDemo? _brandDemo;

    public App()
    {
        InitializeComponent();
        // Tray-resident: closing the last window must not end the process.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    public static App Instance => (App)Current;

    public AppHost Host { get; private set; } = null!;

    public DeviceViewModel Device { get; private set; } = null!;

    public FeaturesViewModel Features { get; private set; } = null!;

    public SettingsViewModel Settings { get; private set; } = null!;

    public WindowManager Windows { get; internal set; } = null!;

    /// <summary>The noise-control hotkeys; null in the diagnostic modes.</summary>
    public HotkeyService? Hotkeys { get; private set; }

    /// <summary>Diagnostics only (<see cref="PopupSelfTest"/>): a host that is never started.</summary>
    internal void SetHostForDiagnostics(AppHost host) => Host = host;

    /// <summary>Diagnostics only: the view models and windows, without tray, single instance or Bluetooth.</summary>
    internal void CreateViewModelsForDiagnostics(DispatcherQueue dispatcher, IDeviceCommandService? commands = null)
    {
        Device = new DeviceViewModel(Host.Store, commands ?? Host.Devices, Host.Settings, Host.Resources, dispatcher);
        Features = new FeaturesViewModel(Host.Store, commands ?? Host.Devices, commands as IAdvancedDeviceCommands ?? Host.Devices, Host.Settings, Device, dispatcher);
        Settings = new SettingsViewModel(Host.Settings, Host.Startup, Host.ResourceSync, Host.Resources.DownloadRoot, Host.Updates,
            Host.StoreUpdates, static () => 0, dispatcher,
            static () => { }, Host.Loggers.CreateLogger<SettingsViewModel>());
        Windows = new WindowManager(new ThemeService(Host.Settings), static () => false, static () => { });
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        // Elevated child of the Labs LE Audio bitrate card: apply and leave, without a window or the single-instance check.
        int codec = Array.FindIndex(commandLine, a => string.Equals(a, LeAudioCodecCompanion.ApplyArgument, StringComparison.OrdinalIgnoreCase));
        if (codec >= 0)
        {
            Environment.Exit(LeAudioCodecCompanion.RunElevatedApply(
                codec + 1 < commandLine.Length ? commandLine[codec + 1] : null,
                codec + 2 < commandLine.Length ? commandLine[codec + 2] : null,
                codec + 3 < commandLine.Length ? commandLine[codec + 3] : null));
            return;
        }
        // The MSIX StartupTask launches without arguments; treat it like the Run entry's --background.
        if (PackageIdentity.IsPackaged
            && Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind
                == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask)
        {
            commandLine = [.. commandLine, StartupRegistration.BackgroundArgument];
        }
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        int demo = Array.FindIndex(commandLine, a => string.Equals(a, "--brand-demo", StringComparison.OrdinalIgnoreCase));
        if (demo >= 0 && demo + 1 < commandLine.Length)
        {
            _brandDemo = new BrandDemo(this, commandLine[demo + 1], _dispatcher);
            int demoBrand = Array.FindIndex(commandLine, a => string.Equals(a, "--demo-brand", StringComparison.OrdinalIgnoreCase));
            _brandDemo.Show(commandLine.Contains("--demo-check", StringComparer.OrdinalIgnoreCase),
                demoBrand >= 0 && demoBrand + 1 < commandLine.Length ? commandLine[demoBrand + 1] : null,
                commandLine.Contains("--demo-exit", StringComparer.OrdinalIgnoreCase));
            return;
        }

        int modelTest = Array.FindIndex(commandLine, a => string.Equals(a, ModelSelfTest.Argument, StringComparison.OrdinalIgnoreCase));
        if (modelTest >= 0 && modelTest + 2 < commandLine.Length)
        {
            _ = ModelSelfTest.RunAsync(commandLine[modelTest + 1], commandLine[modelTest + 2]).ContinueWith(
                _ => _dispatcher.TryEnqueue(Exit), TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        int modelPreview = Array.FindIndex(commandLine, a => string.Equals(a, ModelSelfTest.PreviewArgument, StringComparison.OrdinalIgnoreCase));
        if (modelPreview >= 0 && modelPreview + 4 < commandLine.Length)
        {
            _ = ModelSelfTest.ShowPreviewAsync(this, commandLine, modelPreview, _dispatcher).ContinueWith(
                t => t.Result.Closed += (_, _) => { Host?.Loggers.Dispose(); Host?.FileLog.Dispose(); Exit(); },
                TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        // Diagnostics: off-screen pop-up check; never touches a running instance or the Bluetooth runtime.
        int selfTest = Array.FindIndex(commandLine, a => string.Equals(a, PopupSelfTest.Argument, StringComparison.OrdinalIgnoreCase));
        if (selfTest >= 0)
        {
            string outDir = selfTest + 1 < commandLine.Length ? commandLine[selfTest + 1] : Path.Combine(Path.GetTempPath(), "EncoDeck-popup-selftest");
            bool quick = commandLine.Contains("--quick", StringComparer.OrdinalIgnoreCase);
            _ = PopupSelfTest.RunAsync(this, outDir, _dispatcher, quick).ContinueWith(
                t => _dispatcher.TryEnqueue(() => { Host?.Loggers.Dispose(); Host?.FileLog.Dispose(); Exit(); }),
                TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        // Diagnostics: the pop-up for any pack video with sample levels; no device, no Bluetooth runtime.
        int preview = Array.FindIndex(commandLine, a => string.Equals(a, PopupPreview.Argument, StringComparison.OrdinalIgnoreCase));
        if (preview >= 0 && preview + 1 < commandLine.Length)
        {
            _ = PopupPreview.RunAsync(this, commandLine, _dispatcher).ContinueWith(
                t => _dispatcher.TryEnqueue(() => { Host?.Loggers.Dispose(); Host?.FileLog.Dispose(); Exit(); }),
                TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        int snapshot = Array.FindIndex(commandLine, a => string.Equals(a, UiSnapshot.Argument, StringComparison.OrdinalIgnoreCase));
        if (snapshot >= 0)
        {
            string outDir = snapshot + 1 < commandLine.Length ? commandLine[snapshot + 1] : Path.Combine(Path.GetTempPath(), "EncoDeck-ui-snapshot");
            int modelArg = Array.FindIndex(commandLine, a => string.Equals(a, "--model", StringComparison.OrdinalIgnoreCase));
            string? modelFile = modelArg >= 0 && modelArg + 1 < commandLine.Length ? commandLine[modelArg + 1] : null;
            int artworkArg = Array.FindIndex(commandLine, a => string.Equals(a, "--artwork-dir", StringComparison.OrdinalIgnoreCase));
            string? artworkDirectory = artworkArg >= 0 && artworkArg + 1 < commandLine.Length ? commandLine[artworkArg + 1] : null;
            int profileArg = Array.FindIndex(commandLine, a => string.Equals(a, "--profile", StringComparison.OrdinalIgnoreCase));
            string? profileId = profileArg >= 0 && profileArg + 1 < commandLine.Length ? commandLine[profileArg + 1] : null;
            int bridgeArg = Array.FindIndex(commandLine, a => string.Equals(a, "--bridge-instance", StringComparison.OrdinalIgnoreCase));
            string? bridgeInstance = bridgeArg >= 0 && bridgeArg + 1 < commandLine.Length ? commandLine[bridgeArg + 1] : null;
            // --language ja-JP: render in that UI language (a normal start re-applies the user's setting).
            int languageArg = Array.FindIndex(commandLine, a => string.Equals(a, "--language", StringComparison.OrdinalIgnoreCase));
            if (languageArg >= 0 && languageArg + 1 < commandLine.Length) ApplyLanguage(commandLine[languageArg + 1]);
            _ = UiSnapshot.RunAsync(this, outDir, _dispatcher, modelFile, artworkDirectory, profileId, bridgeInstance).ContinueWith(
                t =>
                {
                    if (t.Exception is { } ex) File.WriteAllText(Path.Combine(outDir, "fatal.txt"), ex.ToString());
                    _dispatcher.TryEnqueue(() => { Host?.Loggers.Dispose(); Host?.FileLog.Dispose(); Exit(); });
                },
                TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        int probe = Array.FindIndex(commandLine, a => string.Equals(a, BackgroundProbe.Argument, StringComparison.OrdinalIgnoreCase));
        if (probe >= 0)
        {
            string outDir = probe + 1 < commandLine.Length ? commandLine[probe + 1] : Path.Combine(Path.GetTempPath(), "EncoDeck-background-probe");
            _ = BackgroundProbe.RunAsync(this, outDir, _dispatcher).ContinueWith(
                t =>
                {
                    if (t.Exception is { } ex) File.WriteAllText(Path.Combine(outDir, "fatal.txt"), ex.ToString());
                    _dispatcher.TryEnqueue(() => { Host?.Loggers.Dispose(); Host?.FileLog.Dispose(); Exit(); });
                },
                TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        _instance = SingleInstanceGuard.Acquire();
        if (_instance.IsPrimary && commandLine.Contains(ExitArgument, StringComparer.OrdinalIgnoreCase))
        {
            // "--exit" with no running instance: nothing to do.
            _instance.Dispose();
            Exit();
            return;
        }
        if (!_instance.IsPrimary)
        {
            // Wake the running instance and leave without creating any window.
            _ = _instance.SignalPrimaryAsync(commandLine, TimeSpan.FromSeconds(2))
                .ContinueWith(_ => _dispatcher.TryEnqueue(Exit), TaskScheduler.Default);
            return;
        }

        Host = AppHost.Create();
        _logger = Host.Loggers.CreateLogger<App>();
        ApplyLanguage(Host.Settings.Current.Language);
        SyncStartupRegistration();

        var theme = new ThemeService(Host.Settings);
        Windows = new WindowManager(theme, () => Host.Settings.Current.KeepRunningInTray, ExitApp, Host.Settings);
        Device = new DeviceViewModel(Host.Store, Host.Devices, Host.Settings, Host.Resources, _dispatcher);
        Features = new FeaturesViewModel(Host.Store, Host.Devices, Host.Devices, Host.Settings, Device, _dispatcher);
        Settings = new SettingsViewModel(Host.Settings, Host.Startup, Host.ResourceSync, Host.Resources.DownloadRoot, Host.Updates,
            Host.StoreUpdates, () => Windows.MainHandle, _dispatcher,
            ExitApp, Host.Loggers.CreateLogger<SettingsViewModel>());

        _tray = new TrayIconService(Device, Host.Settings, Windows, ExitApp);
        _tray.Create();
        var radio = new BluetoothRadioMonitor();
        _ = radio.StartAsync();
        _quickSettings = new QuickSettingsCompanion(Host.Settings, Windows, Device,
            new QuickSettingsWatcher(Host.Loggers.CreateLogger<QuickSettingsWatcher>()), radio, _dispatcher);
        Hotkeys = new HotkeyService(Host.Settings, Device, new GlobalHotkeys(Host.Loggers.CreateLogger<GlobalHotkeys>()), _dispatcher,
            Host.Loggers.CreateLogger<HotkeyService>());

        _instance.ActivationRequested += (_, forwarded) => _dispatcher.TryEnqueue(() => HandleCommandLine(forwarded, isLaunch: false));
        _instance.StartListening();

        Host.Devices.ActiveDeviceLinkConnected += OnDeviceLinkConnected;
        Host.Devices.ActiveDeviceCaseLidOpened += OnCaseLidOpened;
        Host.Store.StateChanged += OnStateChanged;
        Host.Updates.UpdateReady += OnUpdateReady;
        Host.LeAudioCodec.Switched += OnLeAudioCodecSwitched;
        PowerManager.SystemSuspendStatusChanged += OnSuspendStatusChanged;
        Host.Start();

        HandleCommandLine(commandLine, isLaunch: true);
        if (PackageIdentity.IsPackaged)
        {
            Host.StoreUpdates.RegisterRestart(UpdateManager.RelaunchArgument);
            Host.StoreUpdates.UpdateStarting += (_, _) => _dispatcher.TryEnqueue(ExitForStoreUpdate);
            Host.StoreUpdates.WatchForUpdates();
            Windows.MainOpened += (_, _) => _ = CheckStoreUpdatesAsync();
            _ = CheckStoreUpdatesAsync(TimeSpan.FromSeconds(30));
        }
        else
        {
            // The start-up check runs in UpdateManager (started by the host).
            Windows.MainOpened += (_, _) => _ = Task.Run(Host.Updates.CheckIfDueAsync);
        }
        // Launched into the tray (e.g. with Windows): give back what start-up touched.
        Windows.TrimWhenIdle();
    }

    /// <summary>
    /// Command line of this launch, or one forwarded by a second instance. Besides --background and
    /// --exit, diagnostic switches open a specific surface: --page &lt;Home|Sound|Controls|Devices|Settings&gt;,
    /// --show-panel (tray quick panel) and --show-popup (connection popup preview).
    /// </summary>
    private void HandleCommandLine(string[] args, bool isLaunch)
    {
        bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);
        string? Value(string flag)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Has(ExitArgument))
        {
            ExitApp();
            return;
        }
        if (Has("--show-panel"))
        {
            Windows.ToggleTrayPanel(_tray?.Id ?? Guid.Empty);
            return;
        }
        if (Has("--show-popup"))
        {
            // Same rules as a real connection: nothing over exclusive fullscreen, click-through over a game.
            var interruption = FullscreenDetector.GetState();
            if (interruption is InterruptionState.ExclusiveFullscreen or InterruptionState.Away) return;
            Windows.ShowConnectionPopup(
                TimeSpan.FromSeconds(Host.Settings.Current.PopupDurationSeconds),
                clickThrough: interruption == InterruptionState.Fullscreen);
            return;
        }
        if (Has(UpdateManager.RelaunchArgument))
        {
            // Started by the installer after an update the user chose: back to the About page (updates),
            // unless a game or presentation took over meanwhile (then only a notification).
            if (FullscreenDetector.GetState() == InterruptionState.Available)
                Windows.ShowMain("About");
            else
                _tray?.Notify(Loc.Format("Notify_Updated_Title", Settings.Version), Loc.Get("Notify_Updated_Message"));
            return;
        }
        if (Value("--page") is { } page)
        {
            Windows.ShowMain(page);
            return;
        }
        if (!isLaunch || !Has(StartupRegistration.BackgroundArgument))
        {
            Windows.ShowMain();
        }
    }

    /// <summary>
    /// Keeps the HKCU Run entry and the setting in agreement. The installer's "start with Windows"
    /// task writes the registry directly, and an upgrade may move the executable.
    /// </summary>
    private void SyncStartupRegistration()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "EncoDeck.exe");
            bool registered = Host.Startup.IsEnabled(exe);
            if (Host.Settings.Current.StartWithWindows && !registered)
            {
                Host.Startup.SetEnabled(exe, true);
            }
            else if (!Host.Settings.Current.StartWithWindows && registered)
            {
                Host.Settings.Update(s => s with { StartWithWindows = true });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger?.LogWarning(ex, "Could not synchronise start-up registration");
        }
    }

    private static void ApplyLanguage(string? language)
    {
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language ?? string.Empty;
        }
        catch (Exception)
        {
            // Not supported in this configuration; the system language is used.
        }
    }

    private void OnDeviceLinkConnected(object? sender, DeviceIdentity device) => TryShowConnectionPopup(device, PopupTrigger.Connected);

    private void OnCaseLidOpened(object? sender, DeviceIdentity device) => TryShowConnectionPopup(device, PopupTrigger.CaseLidOpened);

    /// <summary>
    /// HeyMelody's pop-up triggers (connection, case lid opened). Never over exclusive fullscreen;
    /// over a borderless fullscreen app or game only as a click-through overlay, if allowed at all.
    /// </summary>
    private void TryShowConnectionPopup(DeviceIdentity device, PopupTrigger trigger)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            var interruption = FullscreenDetector.GetState();
            var decision = Host.PopupPolicy.Evaluate(
                device.DeviceId,
                DateTimeOffset.UtcNow,
                Host.Settings.Current,
                isFullscreen: interruption == InterruptionState.Fullscreen,
                isAway: interruption == InterruptionState.Away,
                isExclusiveFullscreen: interruption == InterruptionState.ExclusiveFullscreen,
                trigger: trigger);
            _logger?.LogInformation("Connection popup for {Device} ({Trigger}): {Decision}", device, trigger, decision);
            if (decision is not (PopupDecision.Show or PopupDecision.ShowPassThrough)) return;
            Host.PopupPolicy.RecordShown(device.DeviceId, DateTimeOffset.UtcNow);
            Windows.ShowConnectionPopup(
                TimeSpan.FromSeconds(Host.Settings.Current.PopupDurationSeconds),
                clickThrough: decision == PopupDecision.ShowPassThrough);
        });
    }

    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        var settings = Host.Settings.Current;
        if (!settings.LowBatteryNotification || e.Current.Connection != DeviceConnectionState.ControlReady) return;
        var low = Host.LowBattery.Evaluate(e.Current.Battery, settings.LowBatteryThreshold);
        if (low.Count == 0) return;
        string parts = string.Join(" · ", low.Select(l => $"{Loc.Get(l.Component == BatteryComponent.Left ? "Battery_Left" : "Battery_Right")} {l.Level}%"));
        _dispatcher?.TryEnqueue(() => _tray?.Notify(Loc.Format("Notify_LowBattery_Title", e.Current.Device?.Name), parts));
    }

    /// <summary>Labs LE Audio bitrate: the applied choice is now the link Windows uses.</summary>
    private void OnLeAudioCodecSwitched(object? sender, Platform.LeAudioCodec.LeAudioLinkConfig link) =>
        _dispatcher?.TryEnqueue(() => _tray?.Notify(Loc.Get("Notify_LeAudioSwitched_Title"), LeAudioCodecText.Link(link)));

    /// <summary>
    /// Store check for the MSIX build, once after start-up and when the main window opens (at most once an hour);
    /// only a tray notification (once per session), installing stays the user's choice.
    /// </summary>
    private async Task CheckStoreUpdatesAsync(TimeSpan delay = default)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            if (_exiting || Host.Settings.Current.AutoCheckUpdates == false || !Settings.IsStoreUpdateIdle
                || DateTimeOffset.UtcNow - _lastStoreCheck < TimeSpan.FromHours(1)) return;
            _lastStoreCheck = DateTimeOffset.UtcNow;
            await Settings.CheckStoreUpdatesCommand.ExecuteAsync(null);
            if (!_storeUpdateNotified && Settings.IsStoreUpdateAvailable && !_exiting && FullscreenDetector.GetState() == InterruptionState.Available)
            {
                _tray?.Notify(Loc.Get("Notify_StoreUpdate_Title"), Loc.Get("Notify_StoreUpdate_Message"));
                _storeUpdateNotified = true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Store update check failed ({Reason})", ex.GetType().Name);
        }
    }

    /// <summary>The Store is replacing this package (its own Update button or an automatic update): leave so it is not "in use".</summary>
    private void ExitForStoreUpdate()
    {
        if (_exiting) return;
        _logger?.LogInformation("Exiting for a Store update");
        ExitApp();
    }

    /// <summary>A background download finished: one tray notification, never over a game (retried later instead).</summary>
    private void OnUpdateReady(object? sender, UpdateRelease release) => _dispatcher?.TryEnqueue(() =>
    {
        _pendingUpdateNotice = release;
        TryShowUpdateNotice();
    });

    private void TryShowUpdateNotice()
    {
        if (_pendingUpdateNotice is not { } release || _exiting) return;
        if (FullscreenDetector.GetState() != InterruptionState.Available)
        {
            if (_noticeTimer is null && _dispatcher is not null)
            {
                _noticeTimer = _dispatcher.CreateTimer();
                _noticeTimer.Interval = TimeSpan.FromMinutes(5);
                _noticeTimer.Tick += (_, _) => TryShowUpdateNotice();
            }
            _noticeTimer?.Start();
            return;
        }
        _noticeTimer?.Stop();
        _pendingUpdateNotice = null;
        // Still current? The user may have installed or a newer one replaced it meanwhile.
        if (Host.Updates.Status is { Stage: UpdateStage.Ready, Release: { } ready } && ready.Version.Equals(release.Version))
            _tray?.Notify(Loc.Format("Notify_UpdateReady_Title", release.Version), Loc.Get("Notify_UpdateReady_Message"));
    }

    private void OnSuspendStatusChanged(object? sender, object e)
    {
        if (PowerManager.SystemSuspendStatus == SystemSuspendStatus.AutoResume || PowerManager.SystemSuspendStatus == SystemSuspendStatus.ManualResume)
        {
            _logger?.LogInformation("System resumed ({Status})", PowerManager.SystemSuspendStatus);
            Host.LowBattery.Reset();
            Host.Devices.NotifySystemResumed();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled UI exception");
        // Device or communication problems must never take the app down.
        e.Handled = true;
    }

    public async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _logger?.LogInformation("Exiting");
        PowerManager.SystemSuspendStatusChanged -= OnSuspendStatusChanged;
        _noticeTimer?.Stop();
        _tray?.Dispose();
        _quickSettings?.Dispose();
        Hotkeys?.Dispose();
        Windows?.CloseAll();
        try
        {
            if (Host is not null) await Host.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Shutdown error");
        }
        _instance?.Dispose();
        Exit();
    }
}
