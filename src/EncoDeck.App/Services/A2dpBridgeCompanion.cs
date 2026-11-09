// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.Json.Nodes;
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;
using EncoDeck.Platform.A2dpBridge;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Packaging;
using EncoDeck.Platform.Settings;
using EncoDeck.Platform.Updates;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// EncoDeck's side of BlueCaster (A2DP Windows Bridge up to 2.0.x): installs the pinned component, finds the user's own copy, starts and
/// quits the core, follows it over its pipe and marks the active device as connected through the bridge (Windows
/// cannot see that link). Costs nothing while no BlueCaster exists beyond a mutex check per interval.
/// </summary>
public sealed class A2dpBridgeCompanion : IAsyncDisposable
{
    private readonly IDeviceStateStore _store;
    private readonly ISettingsStore _settings;
    private readonly ILogger _logger;
    private readonly string? _instance;
    private BridgeInstallation? _installed;
    // Read from disk when installations may have changed, not on every core event.
    private InstalledComponent? _managed;
    private string? _runningDirectory;
    private bool _wasConnected;
    private bool _started;
    private DateTimeOffset _lastUpdateCheck = DateTimeOffset.MinValue;

    /// <param name="instance">A side-by-side test core (BLUECASTER_INSTANCE); null for the user's own.</param>
    public A2dpBridgeCompanion(string componentsRoot, IDeviceStateStore store, ISettingsStore settings, ILogger logger, string? instance = null)
    {
        _instance = instance;
        _store = store;
        _settings = settings;
        _logger = logger;
        Component = A2dpBridgeComponent.CreateInstaller(new HttpClient(), Path.Combine(componentsRoot, "A2DPWB"));
        Monitor = new A2dpBridgeMonitor(logger, instance);
        Monitor.Changed += (_, _) => OnMonitorChanged();
        _store.StateChanged += OnStateChanged;
    }

    /// <summary>The installed EncoDeck-managed copy, as of the last <see cref="RefreshInstallations"/>.</summary>
    public InstalledComponent? Managed => _managed;

    /// <summary>A later BlueCaster release found online (null until checked, or when there is none).</summary>
    public ComponentRelease? OnlineRelease { get; private set; }

    /// <summary>What an update of the managed copy installs: this app's pin or a later release found online.</summary>
    public ComponentRelease? UpdateTarget => _managed is null ? null : Component.UpdateTarget(OnlineRelease);

    /// <summary>The managed copy is installed and a later release (pinned or online) is available.</summary>
    public bool ManagedUpdateAvailable => UpdateTarget is not null;

    /// <summary>What a download installs: the update, or the newest release for a first install.</summary>
    public ComponentRelease? DownloadTarget => UpdateTarget ?? Component.Newest(OnlineRelease);

    /// <summary>The EncoDeck-managed copy (the mirrored official build).</summary>
    public ComponentInstaller Component { get; }

    public A2dpBridgeMonitor Monitor { get; }

    public BridgeStatus Status => Monitor.Status;

    /// <summary>Raised on a background thread when the core's state, the installation or the link changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The Store build cannot download or start programs outside its package; it only connects to a running core.</summary>
    public static bool CanManage => !PackageIdentity.IsPackaged;

    /// <summary>
    /// The copy to start: a running core's folder, then BlueCaster's own installation (it updates itself), then the
    /// EncoDeck-managed copy.
    /// </summary>
    public BridgeInstallation? Installation
    {
        get
        {
            if (Status.IsConnected && _runningDirectory is { } running)
            {
                var managed = _managed;
                var origin = managed is not null && SamePath(running, managed.Directory) ? BridgeOrigin.Managed
                    : _installed is not null && SamePath(running, _installed.Directory) ? BridgeOrigin.Installed : BridgeOrigin.Running;
                return new BridgeInstallation(running, origin, A2dpBridgeComponent.FileVersion(Path.Combine(running, (A2dpBridgeComponent.NamesIn(running) ?? A2dpBridgeComponent.Current).Core)));
            }
            if (_installed is { } own && A2dpBridgeComponent.IsBridgeFolder(own.Directory)) return own;
            return _managed is { } m && A2dpBridgeComponent.IsBridgeFolder(m.Directory)
                ? new BridgeInstallation(m.Directory, BridgeOrigin.Managed, ManagedVersion(m.Version)) : null;
        }
    }

    // A device switch resets the state: mark the new device again if the bridge streams to it.
    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        if (e.Current.Bridge != BridgeDeviceMatch.LinkFor(e.Current.Device, Monitor.Status)) Project();
    }

    private void OnMonitorChanged()
    {
        bool connected = Monitor.Status.IsConnected;
        if (connected != _wasConnected)
        {
            _wasConnected = connected;
            // Once per session: which copy runs (process enumeration is not free).
            _runningDirectory = connected ? A2dpBridgeComponent.RunningCoreDirectory() : null;
        }
        Project();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Upstream version of a component version ("3.0.0-beta.3-r0" → "3.0.0-beta.3").</summary>
    private static string ManagedVersion(string version) => version.Split("-r", 2)[0];

    public void Start()
    {
        if (_started) return;
        _started = true;
        RefreshInstallations();
        Monitor.Start();
        _ = Task.Run(async () =>
        {
            // Old versions and unfinished downloads; a copy whose core is running is kept.
            Component.CleanUp();
            if (_settings.Current.A2dpBridgeAutoStart && CanManage && !A2dpBridgeMonitor.IsCoreRunning(_instance) && Installation is { } installation)
            {
                try
                {
                    _logger.LogInformation("Starting BlueCaster ({Origin})", installation.Origin);
                    A2dpBridgeComponent.StartCore(installation);
                    await Monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _logger.LogWarning("Could not start BlueCaster: {Error}", ex.Message);
                }
            }
        });
    }

    /// <summary>Looks again for BlueCaster's own installation (after installing it, or when the Labs page opens).</summary>
    public void RefreshInstallations()
    {
        _installed = A2dpBridgeComponent.FindInstalled(Component.Root);
        _managed = Component.Installed;
        // A user without any BlueCaster pays for a mutex check every half minute; with one, a new core is found sooner.
        Monitor.IdleInterval = _installed is not null || _managed is not null ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Installs or updates the managed copy. A managed core that runs an older version and is not streaming is
    /// restarted from the new one; while it streams, the new version takes over at its next start.
    /// </summary>
    public async Task<bool> InstallAsync(IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        string? running = Status.IsConnected && Installation is { Origin: BridgeOrigin.Managed } current ? current.Directory : null;
        var target = DownloadTarget ?? Component.Release;
        var installed = await Component.InstallAsync(target, progress, ct).ConfigureAwait(false);
        RefreshInstallations();
        if (running is null || SamePath(running, installed.Directory)) return false;
        if (Status.Snapshot?.Stream.IsActive == true) return true;
        await QuitCoreAsync().ConfigureAwait(false);
        for (int i = 0; i < 60 && A2dpBridgeMonitor.IsCoreRunning(_instance); i++) await Task.Delay(250, ct).ConfigureAwait(false);
        if (!A2dpBridgeMonitor.IsCoreRunning(_instance) && Installation is { Origin: BridgeOrigin.Managed } fresh)
        {
            // The old version was in use until now: remove it before starting the new one.
            Component.CleanUp();
            A2dpBridgeComponent.StartCore(fresh);
            await Monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Looks for the newest official BlueCaster build (betas included, any major version) on the project's own
    /// releases, at most once a day unless <paramref name="force"/>; it is downloaded from there too. Only the managed
    /// copy uses it: BlueCaster's own installation updates itself.
    /// </summary>
    public async Task<ComponentUpdateOutcome?> CheckForUpdateAsync(bool force, CancellationToken ct = default)
    {
        if (!CanManage || (!force && DateTimeOffset.UtcNow - _lastUpdateCheck < TimeSpan.FromHours(24))) return null;
        var app = SemanticVersion.FromAssembly(typeof(A2dpBridgeCompanion).Assembly);
        using var http = new HttpClient();
        string floor = _managed is { } managed && ComponentVersion.IsNewer(managed.Version, Component.Release.Version) ? managed.Version : Component.Release.Version;
        var result = await new ComponentUpdateClient(http, app).CheckUpstreamAsync(Component.Release, floor, ct).ConfigureAwait(false);
        if (result.Outcome is ComponentUpdateOutcome.Available or ComponentUpdateOutcome.UpToDate)
        {
            OnlineRelease = result.Release;
            _lastUpdateCheck = DateTimeOffset.UtcNow;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return result.Outcome;
    }

    /// <summary>Adapters BlueCaster took from Windows and has not given back, from its records.</summary>
    public int AssignedAdapterCount => A2dpBridgeComponent.DriverRecordCount(A2dpBridgeComponent.DriverRecordsDirectory(Status.Snapshot?.ConfigDirectory));

    /// <summary>
    /// Removes the EncoDeck-managed copy: quits its core if it runs, optionally gives every adapter BlueCaster took back
    /// to Windows (one administrator prompt, BlueCaster's own helper), drops the autostart entries that name this copy,
    /// then deletes its files. BlueCaster's settings folder (pairings, firmware) is left alone; so is its own installation.
    /// </summary>
    public async Task UninstallAsync(bool restoreDrivers, CancellationToken ct = default)
    {
        if (_managed is not { } managed) return;
        var copy = new BridgeInstallation(managed.Directory, BridgeOrigin.Managed, ManagedVersion(managed.Version));
        string records = A2dpBridgeComponent.DriverRecordsDirectory(Status.Snapshot?.ConfigDirectory);
        if (Status.IsConnected && Installation is { Origin: BridgeOrigin.Managed })
        {
            await QuitCoreAsync().ConfigureAwait(false);
            for (int i = 0; i < 80 && A2dpBridgeMonitor.IsCoreRunning(_instance); i++) await Task.Delay(250, ct).ConfigureAwait(false);
            if (A2dpBridgeMonitor.IsCoreRunning(_instance)) throw new IOException(Loc.Get("Bridge_UninstallCoreRunning"));
        }
        if (restoreDrivers && A2dpBridgeComponent.DriverRecordCount(records) > 0)
        {
            int exit;
            try { exit = await A2dpBridgeComponent.RestoreRecordedDriversAsync(copy, records, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new OperationCanceledException(Loc.Get("Bridge_UninstallUacDeclined")); }
            if (exit != 0) throw new IOException(Loc.Format("Bridge_UninstallRestoreFailed", exit.ToString(CultureInfo.InvariantCulture)));
        }
        A2dpBridgeComponent.RemoveStartupEntryUnder(Component.Root);
        if (_settings.Current.A2dpBridgeAutoStart && _installed is null) _settings.Update(s => s with { A2dpBridgeAutoStart = false });
        if (!Component.Uninstall()) throw new IOException(Loc.Get("Bridge_UninstallBusy"));
        OnlineRelease = null;
        RefreshInstallations();
    }

    /// <summary>Starts the core in the background and waits until EncoDeck is connected to it.</summary>
    public async Task<bool> StartCoreAsync(CancellationToken ct = default)
    {
        if (Status.IsConnected) return true;
        if (!A2dpBridgeMonitor.IsCoreRunning(_instance))
        {
            var installation = Installation ?? throw new InvalidOperationException("BlueCaster is not installed.");
            A2dpBridgeComponent.StartCore(installation);
        }
        return await Monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    /// <summary>Ends the core as its tray's Exit does (the stream stops, the adapter is reset).</summary>
    public async Task QuitCoreAsync()
    {
        try { await Monitor.RequestAsync("app.quit", timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        // The core may close the pipe before it answers.
        catch (IOException) { }
        Monitor.Wake();
    }

    /// <summary>Assigns WinUSB (the core asks for administrator rights): the adapter leaves Windows' Bluetooth.</summary>
    public Task AssignWinUsbAsync(string instanceId) =>
        Monitor.RequestAsync("adapter.assign_winusb", new JsonObject { ["instance_id"] = instanceId }, TimeSpan.FromMinutes(5));

    /// <summary>Gives the adapter back to Windows' Bluetooth driver (administrator rights).</summary>
    public Task RestoreDriverAsync(string instanceId) =>
        Monitor.RequestAsync("adapter.restore_driver", new JsonObject { ["instance_id"] = instanceId }, TimeSpan.FromMinutes(5));

    public Task RefreshAdaptersAsync() => Monitor.RequestAsync("adapter.list", timeout: TimeSpan.FromSeconds(20));

    /// <summary>BlueCaster's diagnostic text with Bluetooth addresses masked, for EncoDeck's diagnostics package.</summary>
    public async Task<string?> DiagnosticsAsync()
    {
        if (!Status.IsConnected) return null;
        try
        {
            var result = await Monitor.RequestAsync("diag.info", new JsonObject { ["mask"] = true }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return result?["text"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException) { return null; }
    }

    /// <summary>Marks the active device as connected through the bridge while the bridge streams to its address.</summary>
    private void Project()
    {
        var status = Monitor.Status;
        _store.TryUpdate(null, s =>
        {
            var link = BridgeDeviceMatch.LinkFor(s.Device, status);
            return s.Bridge == link ? s : s with { Bridge = link };
        });
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        _store.StateChanged -= OnStateChanged;
        await Monitor.DisposeAsync().ConfigureAwait(false);
    }
}
