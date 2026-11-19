// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Platform.A2dpBridge;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Packaging;
using EncoDeck.Platform.Settings;
using EncoDeck.Platform.Updates;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// EncoDeck's side of BlueCaster (A2DP Windows Bridge up to 2.0.x): runs BlueCaster's own installer once on request,
/// finds the installed copy, starts the core, follows it over its pipe and marks the earbuds it streams to as connected
/// through the bridge (Windows cannot see that link). BlueCaster updates and uninstalls itself and manages adapters,
/// earbuds and streams in its own window; EncoDeck only connects. Costs nothing while no BlueCaster exists beyond a
/// mutex check per interval.
/// </summary>
public sealed class A2dpBridgeCompanion : IAsyncDisposable, EncoDeck.Bluetooth.IBridgeLinkControl
{
    private readonly IDeviceStateStore _store;
    private readonly ISettingsStore _settings;
    private readonly ILogger _logger;
    private readonly string? _instance;
    private BridgeInstallation? _installed;
    // Read from disk when installations may have changed, not on every core event.
    private BridgeInstallation? _managed;
    private readonly string _componentsRoot;
    private string? _runningDirectory;
    private bool _wasConnected;
    private bool _started;
    private readonly HttpClient _http = new();

    /// <param name="instance">A side-by-side test core (BLUECASTER_INSTANCE); null for the user's own.</param>
    public A2dpBridgeCompanion(string componentsRoot, IDeviceStateStore store, ISettingsStore settings, ILogger logger, string? instance = null)
    {
        _instance = instance;
        _store = store;
        _settings = settings;
        _logger = logger;
        _componentsRoot = componentsRoot;
        Monitor = new A2dpBridgeMonitor(logger, instance);
        Monitor.Changed += (_, _) => OnMonitorChanged();
        _store.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// The portable copy EncoDeck up to 1.15 downloaded (still started, never updated), as of the last
    /// <see cref="RefreshInstallations"/>.
    /// </summary>
    public BridgeInstallation? Managed => _managed;

    /// <summary>Where EncoDeck up to 1.15 kept its portable copy (the folder name is A2DP Windows Bridge's).</summary>
    private string DownloadedRoot => Path.Combine(_componentsRoot, "A2DPWB");

    public A2dpBridgeMonitor Monitor { get; }

    public BridgeStatus Status => Monitor.Status;

    /// <summary>Raised on a background thread when the core's state, the installation or the link changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The Store build cannot install or start programs outside its package; it only connects to a running core.</summary>
    public static bool CanManage => !PackageIdentity.IsPackaged;

    /// <summary>
    /// The copy to start: a running core's folder, then BlueCaster's own installation (it updates itself), then the
    /// portable copy an earlier EncoDeck downloaded.
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
            return _managed is { } m && A2dpBridgeComponent.IsBridgeFolder(m.Directory) ? m : null;
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

    public void Start()
    {
        if (_started) return;
        _started = true;
        RefreshInstallations();
        Monitor.Start();
        _ = Task.Run(async () =>
        {
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
        _installed = A2dpBridgeComponent.FindInstalled(DownloadedRoot);
        _managed = A2dpBridgeComponent.FindDownloaded(DownloadedRoot);
        // A core started anywhere (its own window, the tray, Windows' startup, here) is found within this interval, and
        // the earbuds it streams to are then followed. Without an installation found, a portable copy may still start.
        Monitor.IdleInterval = _installed is not null || _managed is not null ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(15);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>BlueCaster's own installation is missing: offer to install it (the Store build cannot).</summary>
    public bool CanInstall => CanManage && _installed is null;

    /// <summary>
    /// Looks up the newest official BlueCaster release (betas included) on the project's own releases at this moment
    /// (nothing about it is built into EncoDeck), downloads its installer held to GitHub's SHA-256 digest, and runs it
    /// unattended. BlueCaster then updates and uninstalls itself;
    /// its window opens for the first-run setup (adapter, firmware, earbuds).
    /// </summary>
    public async Task InstallAsync(IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        var app = SemanticVersion.FromAssembly(typeof(A2dpBridgeCompanion).Assembly);
        var found = await new ComponentUpdateClient(_http, app).FindUpstreamSetupAsync(A2dpBridgeComponent.Repository, A2dpBridgeComponent.SetupArchitecture, ct).ConfigureAwait(false);
        if (found.Setup is not { } setup)
        {
            throw new IOException(Loc.Get(found.Outcome switch
            {
                ComponentUpdateOutcome.RateLimited => "Component_RateLimited",
                ComponentUpdateOutcome.Offline => "Component_Offline",
                _ => "Bridge_SetupNotFound",
            }));
        }
        string folder = Path.Combine(_componentsRoot, "BlueCaster-setup");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, setup.File);
        await A2dpBridgeComponent.DownloadSetupAsync(_http, setup, path, progress, ct).ConfigureAwait(false);
        _logger.LogInformation("Running BlueCaster's installer {Version} ({File})", setup.Version, setup.File);
        int exit = await A2dpBridgeComponent.RunSetupAsync(path).ConfigureAwait(false);
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        RefreshInstallations();
        if (exit != 0 || _installed is not { } installed)
            throw new IOException(Loc.Format("Bridge_SetupFailed", exit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Monitor.Wake();
        try { A2dpBridgeComponent.OpenWindow(installed); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { _logger.LogWarning("Could not open BlueCaster: {Error}", ex.Message); }
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

    /// <summary>
    /// Connect in EncoDeck for earbuds BlueCaster carries: starts its connection profile for them (the active one when it
    /// is theirs), starting the core first when it is installed but not running.
    /// </summary>
    public async Task<CommandResult> ConnectAsync(BluetoothAddress address, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Status.IsConnected && !(CanManage && Installation is not null && await StartCoreAsync(cancellationToken).ConfigureAwait(false)))
                return new CommandResult(CommandStatus.NotConnected, "BlueCaster is not running.");
            var snapshot = Status.Snapshot!;
            if (snapshot.ActiveStreamProfile is { } streaming && BridgeDeviceMatch.ParseAddress(streaming.DeviceAddress) == address.Value)
                return CommandResult.Ok;
            var theirs = snapshot.Profiles.Where(p => BridgeDeviceMatch.ParseAddress(p.DeviceAddress) == address.Value).ToArray();
            if ((theirs.FirstOrDefault(p => p.Name == snapshot.ActiveProfile) ?? theirs.FirstOrDefault()) is not { } profile)
                return CommandResult.Unsupported("BlueCaster has no connection profile for these earbuds.");
            _logger.LogInformation("Starting BlueCaster's connection profile for the earbuds");
            await Monitor.RequestAsync("stream.start", new JsonObject { ["profile"] = profile.Name }, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            return CommandResult.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { return Failure(ex); }
    }

    /// <summary>Disconnect in EncoDeck for earbuds BlueCaster streams to: stops that stream.</summary>
    public async Task<CommandResult> DisconnectAsync(BluetoothAddress address, CancellationToken cancellationToken = default)
    {
        if (Status is not { IsConnected: true, Snapshot: { } snapshot }
            || snapshot.ActiveStreamProfile is not { } profile || BridgeDeviceMatch.ParseAddress(profile.DeviceAddress) != address.Value)
            return CommandResult.Ok;
        try
        {
            _logger.LogInformation("Stopping BlueCaster's stream to the earbuds");
            await Monitor.RequestAsync("stream.stop", timeout: TimeSpan.FromSeconds(20), ct: cancellationToken).ConfigureAwait(false);
            return CommandResult.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { return Failure(ex); }
    }

    private CommandResult Failure(Exception ex)
    {
        _logger.LogWarning("BlueCaster request failed: {Error}", ex.Message);
        return ex switch
        {
            BridgeRequestException request => new CommandResult(CommandStatus.Rejected,
                request.BridgeMessage.IsEmpty ? request.Code : A2dpBridgeText.Format(request.BridgeMessage)),
            BridgeUnavailableException => new CommandResult(CommandStatus.NotConnected, "BlueCaster is not running."),
            TimeoutException => new CommandResult(CommandStatus.Timeout, ex.Message),
            _ => new CommandResult(CommandStatus.TransportError, ex.Message),
        };
    }

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
        _http.Dispose();
    }
}
