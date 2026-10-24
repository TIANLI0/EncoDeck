// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth;
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Core.Notifications;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Platform.Cache;
using EncoDeck.Platform.Logging;
using EncoDeck.Platform.Packaging;
using EncoDeck.Platform.Paths;
using EncoDeck.Platform.Resources;
using EncoDeck.Platform.Settings;
using EncoDeck.Platform.Startup;
using EncoDeck.Platform.Updates;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Oppo.Models;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// Owns the long-lived, UI-independent services. Created once at start-up and disposed on exit.
/// There is deliberately no fake/demo device here: the app only ever shows real device state.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private AppHost(AppPaths paths, JsonFileLoggerProvider fileLog, ILoggerFactory loggers, ISettingsStore settings, IDeviceStateStore store, IDeviceManager devices, DeviceStateCache cache, FileDeviceResourceProvider resources)
    {
        Resources = resources;
        ResourceClient = new EncoApiResourceClient(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(60) }, resources.DownloadRoot, resources.Invalidate);
        ResourceSync = new DeviceResourceSync(store, ResourceClient, loggers.CreateLogger<DeviceResourceSync>());
        Updates = new UpdateManager(
            new GitHubUpdateClient(new HttpClient(), paths.UpdatesDirectory, SemanticVersion.FromAssembly(typeof(AppHost).Assembly)),
            settings, Path.Combine(paths.UpdatesDirectory, "last-check.txt"), loggers.CreateLogger<UpdateManager>());
        StoreUpdates = new StoreUpdateService(loggers.CreateLogger<StoreUpdateService>());
        Paths = paths;
        FileLog = fileLog;
        Loggers = loggers;
        Settings = settings;
        Store = store;
        Devices = devices;
        Cache = cache;
        Cups = new CupService(settings, loggers);
    }

    /// <summary>Experimental cup (BLE actuator) devices; idle until the user opts in and scans.</summary>
    public CupService Cups { get; }

    public AppPaths Paths { get; }

    public JsonFileLoggerProvider FileLog { get; }

    public ILoggerFactory Loggers { get; }

    public ISettingsStore Settings { get; }

    public IDeviceStateStore Store { get; }

    public IDeviceManager Devices { get; }

    public DeviceStateCache Cache { get; }

    public FileDeviceResourceProvider Resources { get; }

    private EncoApiResourceClient ResourceClient { get; }
    public DeviceResourceSync ResourceSync { get; }

    /// <summary>GitHub Releases check and background download; installs only when the user asks.</summary>
    public UpdateManager Updates { get; }

    /// <summary>Store updates for the MSIX build (see <see cref="PackageIdentity"/>).</summary>
    public StoreUpdateService StoreUpdates { get; }

    public StartupRegistration Startup { get; } = new();

    public ConnectionPopupPolicy PopupPolicy { get; } = new();

    public LowBatteryMonitor LowBattery { get; } = new();

    /// <param name="logDirectory">Where to write the log (diagnostics runs keep the user's log clean).</param>
    public static AppHost Create(string? logDirectory = null, string? rootOverride = null)
    {
        var paths = new AppPaths(rootOverride);
        paths.EnsureCreated();
        var fileLog = new JsonFileLoggerProvider(logDirectory ?? paths.LogDirectory, LogLevel.Information);
        var loggers = LoggerFactory.Create(builder =>
        {
            // Avoid formatting Debug messages when the only release sink writes Information and up.
#if DEBUG
            builder.SetMinimumLevel(LogLevel.Debug);
#else
            builder.SetMinimumLevel(LogLevel.Information);
#endif
            builder.AddProvider(fileLog);
#if DEBUG
            builder.AddDebug();
#endif
        });

        var settings = new JsonSettingsStore(paths.SettingsFile, loggers.CreateLogger<JsonSettingsStore>());
        var store = new DeviceStateStore();
        var cache = new DeviceStateCache(paths.DeviceCacheFile, store, loggers.CreateLogger<DeviceStateCache>());
        var registry = new DeviceProfileRegistry(BuiltInProfiles.Create());
        var devices = new DeviceManager(
            registry,
            store,
            new RfcommChannelConnector(registry, loggers.CreateLogger<RfcommChannelConnector>()),
            preferredDeviceId: () => settings.Current.PreferredDeviceId,
            // Experimental MOONDROP commands remain opt-in and read this preference dynamically.
            allowExperimental: () => store.Current.Profile is EncoDeck.Protocol.Brands.Moondrop.MoondropProfile && settings.Current.EnableExperimentalFeatures,
            loggers,
            trace: new RawProtocolLog(loggers.CreateLogger("EncoDeck.Protocol.Raw"), () => settings.Current.RawProtocolLogging));

        loggers.CreateLogger<AppHost>().LogInformation(
            "EncoDeck {Version} starting on {OS}",
            SemanticVersion.FromAssembly(typeof(AppHost).Assembly),
            Environment.OSVersion.VersionString);
        var resources = new FileDeviceResourceProvider(
            Path.Combine(paths.Root, "Resources"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "packs"),
            loggers.CreateLogger<FileDeviceResourceProvider>());
        return new AppHost(paths, fileLog, loggers, settings, store, devices, cache, resources);
    }

    public void Start()
    {
        Cache.Start();
        Resources.StartWatching();
        ResourceSync.Start();
        Devices.Start();
        // The Store updates the MSIX build; GitHub Releases are for the installer build only.
        if (!PackageIdentity.IsPackaged) Updates.Start();
    }

    public async ValueTask DisposeAsync()
    {
        // First: stop any actuator output before anything else shuts down.
        await Cups.DisposeAsync();
        await Devices.DisposeAsync();
        await ResourceSync.DisposeAsync();
        await Updates.DisposeAsync();
        ResourceClient.Dispose();
        Cache.Dispose();
        Resources.Dispose();
        Loggers.Dispose();
        FileLog.Dispose();
    }
}
