// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using EncoDeck.App.Services;
using EncoDeck.Platform.A2dpBridge;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Experimental: BlueCaster on a dedicated USB adapter (LDAC, LHDC V5, aptX HD… without test signing).
/// EncoDeck downloads the pinned component, starts and quits the core, shows its adapter and stream and hands
/// adapters to it; earbuds, connection profiles and volume stay in BlueCaster's own window. See docs/A2DP_BRIDGE.md.
/// </summary>
public sealed partial class A2dpBridgeSettings : UserControl
{
    private CancellationTokenSource? _download;
    private bool _busy, _syncing, _refreshQueued;
    private string? _windowsAdapter;

    public A2dpBridgeSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Cancel.Content = Loc.Get("Common_Cancel");
            CheckUpdates.Content = Loc.Get("Component_CheckUpdates");
            Uninstall.Content = Loc.Get("Component_Uninstall");
            StartCore.Content = Loc.Get("Bridge_Start");
            QuitCore.Content = Loc.Get("Bridge_Quit");
            OpenWindow.Content = Loc.Get("Bridge_OpenWindow");
            RefreshAdapters.Content = Loc.Get("Bridge_RefreshAdapters");
            OpenSettingsFolder.Content = Loc.Get("Bridge_OpenSettingsFolder");
            Bridge.Changed += OnBridgeChanged;
            Bridge.RefreshInstallations();
            Bridge.Monitor.Wake();
            _ = LoadWindowsAdapterAsync();
            Refresh();
            // At most once a day, quietly: a later component release can arrive without an app update.
            _ = CheckForUpdateQuietlyAsync();
        };
        Unloaded += (_, _) =>
        {
            Bridge.Changed -= OnBridgeChanged;
            _download?.Cancel();
        };
    }

    private static A2dpBridgeCompanion Bridge => App.Instance.Host.Bridge;

    private void OnBridgeChanged(object? sender, EventArgs e)
    {
        // Coalesce bursts of core events into one refresh.
        if (_refreshQueued) return;
        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() => { _refreshQueued = false; Refresh(); });
    }

    private async Task LoadWindowsAdapterAsync()
    {
        try { _windowsAdapter = await WindowsAdapterGuard.DefaultAdapterInstanceIdAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _windowsAdapter = null; }
        Refresh();
    }

    private static async Task CheckForUpdateQuietlyAsync()
    {
        try { await Bridge.CheckForUpdateAsync(force: false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private void Refresh()
    {
        var status = Bridge.Status;
        var installation = Bridge.Installation;
        var component = Bridge.Component;
        bool manage = A2dpBridgeCompanion.CanManage;
        bool connected = status.IsConnected;
        bool managedUpdate = Bridge.ManagedUpdateAvailable;
        var target = Bridge.DownloadTarget;
        double megabytes = (target is null ? 0 : component.PackageOf(target)?.Size ?? 0) / 1048576.0;

        string text = status.Link switch
        {
            BridgeLink.Connected => Loc.Format("Bridge_Running", status.ServerVersion ?? "?", Origin(installation?.Origin)),
            BridgeLink.Incompatible => Loc.Format("Bridge_Incompatible", status.Detail),
            BridgeLink.Connecting => Loc.Get("Bridge_Connecting"),
            _ when installation is not null => Loc.Format("Bridge_Stopped", installation.Version ?? "?", Origin(installation.Origin)),
            _ when !manage => Loc.Get("Bridge_StoreMissing"),
            _ => target is not null ? Loc.Format("Bridge_Missing", megabytes) : Loc.Get("Bridge_Unpublished"),
        };
        if (Bridge.Managed is { } managed) text += Environment.NewLine + Loc.Format("Bridge_ManagedVersion", managed.Version);
        if (Bridge.UpdateTarget is { } update) text += Environment.NewLine + Loc.Format("Bridge_UpdateAvailable", update.Version);
        if (!manage && installation is not null && !connected) text += Environment.NewLine + Loc.Get("Bridge_StoreStart");
        // An operation in progress keeps its own status line until it ends.
        if (_download is null && !_busy) Status.Text = text;

        bool downloading = _download is not null;
        StandaloneLink.Visibility = Show(!manage && installation is null);
        // BlueCaster's own installation updates itself; EncoDeck's copy is only offered without one.
        Download.Visibility = Show(manage && target is not null && (installation is null || managedUpdate) && !downloading);
        Download.Content = managedUpdate ? Loc.Format("Bridge_Update", megabytes) : Loc.Format("Bridge_Download", megabytes);
        Download.IsEnabled = !_busy;
        StartCore.Visibility = Show(manage && installation is not null && status.Link == BridgeLink.NotRunning);
        QuitCore.Visibility = Show(connected);
        OpenWindow.Visibility = Show(manage && installation is not null);
        StartCore.IsEnabled = QuitCore.IsEnabled = OpenWindow.IsEnabled = !_busy;
        bool ownOnly = installation is { Origin: BridgeOrigin.Installed } && Bridge.Managed is null;
        CheckUpdates.Visibility = Show(manage && !ownOnly && !downloading);
        Uninstall.Visibility = Show(manage && Bridge.Managed is not null && !downloading);
        CheckUpdates.IsEnabled = Uninstall.IsEnabled = !_busy;
        OwnInstallNote.Visibility = Show(manage && installation is { Origin: BridgeOrigin.Installed });
        AutoStart.Visibility = Show(manage && installation is not null);
        _syncing = true;
        try { AutoStart.IsOn = App.Instance.Host.Settings.Current.A2dpBridgeAutoStart; }
        finally { _syncing = false; }

        Live.Visibility = AdaptersPanel.Visibility = Show(connected);
        if (connected) ShowLive(status.Snapshot!);
    }

    private void ShowLive(BridgeSnapshot snapshot)
    {
        AdapterText.Text = DescribeAdapters(snapshot);
        StreamText.Text = DescribeStream(snapshot);
        ShowAdapters(snapshot);
    }

    private static string DescribeAdapters(BridgeSnapshot snapshot)
    {
        var lines = new List<string>();
        if (snapshot.V1Running) lines.Add(Loc.Get("Bridge_V1Running"));
        var usable = snapshot.Adapters.Where(a => a.Driver == BridgeDriver.WinUsb).ToArray();
        if (usable.Length == 0) lines.Add(Loc.Get("Bridge_NoAdapter"));
        foreach (var adapter in usable)
        {
            lines.Add(Loc.Format("Bridge_AdapterLine", adapter.DisplayName, Loc.Get($"Bridge_AdapterStatus_{snapshot.AdapterStatus}")));
            if (adapter.FirmwareInvalid is { } invalid) lines.Add(Loc.Format("Bridge_FirmwareInvalid", invalid));
            else if (adapter.FirmwarePresent == false) lines.Add(Loc.Format("Bridge_FirmwareMissing", string.Join(", ", adapter.FirmwareFiles)));
            if (adapter.VendorNote is { } note) lines.Add(A2dpBridgeText.Format(note));
        }
        if (snapshot.AdapterError is { } error) lines.Add(A2dpBridgeText.Format(error));
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeStream(BridgeSnapshot snapshot)
    {
        var stream = snapshot.Stream;
        string state = stream.Message.IsEmpty ? Loc.Get($"Bridge_Stream_{stream.State}") : A2dpBridgeText.Format(stream.Message);
        if (stream.State != BridgeStreamState.Streaming || stream.Info is not { } info) return Loc.Format("Bridge_StreamLine", state);
        var parts = new List<string> { A2dpBridgeText.Codec(info.Codec) };
        if (info.BitrateKbps > 0) parts.Add(info.Abr ? Loc.Format("Bridge_BitrateAbr", info.BitrateKbps) : $"{info.BitrateKbps} kbps");
        if (info.SampleRate > 0) parts.Add((info.SampleRate / 1000.0).ToString(info.SampleRate % 1000 == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " kHz");
        if (info.LeSummary is { Length: > 0 } le) parts.Add(le);
        string source = info.SourceKind switch
        {
            "virtual" => Loc.Format("Bridge_SourceVirtual", info.SourceName),
            "app" => Loc.Format(info.SourceWaiting ? "Bridge_SourceAppWaiting" : "Bridge_SourceApp", info.SourceName),
            "tone" => Loc.Get("Bridge_SourceTone"),
            _ => Loc.Get("Bridge_SourceLoopback"),
        };
        string line = Loc.Format("Bridge_StreamingLine", state, string.Join(" · ", parts), source);
        if (info.RequestedCodec is { } requested && !info.RequestedInUse) line += Environment.NewLine + Loc.Format("Bridge_CodecFallback", A2dpBridgeText.Codec(requested));
        return line;
    }

    private void ShowAdapters(BridgeSnapshot snapshot)
    {
        AdapterList.Children.Clear();
        if (snapshot.Adapters.Count == 0)
        {
            AdapterList.Children.Add(new TextBlock { Text = Loc.Get("Bridge_NoUsbAdapters"), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
            return;
        }
        foreach (var adapter in snapshot.Adapters)
        {
            bool isWindows = WindowsAdapterGuard.IsSameDevice(adapter.InstanceId, _windowsAdapter);
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string driver = adapter.Driver switch
            {
                BridgeDriver.WinUsb => Loc.Get("Bridge_DriverWinUsb"),
                BridgeDriver.Bluetooth => Loc.Get(isWindows ? "Bridge_DriverWindowsInUse" : "Bridge_DriverBluetooth"),
                BridgeDriver.None => Loc.Get("Bridge_DriverNone"),
                _ => Loc.Format("Bridge_DriverOther", adapter.DriverService),
            };
            var label = new TextBlock
            {
                Text = $"{adapter.DisplayName}{Environment.NewLine}{driver}",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(label);
            var button = new Button
            {
                Content = Loc.Get(adapter.Driver == BridgeDriver.WinUsb ? "Bridge_RestoreDriver" : "Bridge_AssignWinUsb"),
                // Never offer Windows' own adapter: the earbuds' control, and every other paired device, run on it.
                IsEnabled = !_busy && !(adapter.Driver != BridgeDriver.WinUsb && isWindows),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (isWindows && adapter.Driver != BridgeDriver.WinUsb) ToolTipService.SetToolTip(button, Loc.Get("Bridge_WindowsAdapterTip"));
            Grid.SetColumn(button, 1);
            button.Click += async (_, _) => await ChangeDriver(adapter);
            row.Children.Add(button);
            AdapterList.Children.Add(row);
        }
    }

    private async Task ChangeDriver(BridgeAdapter adapter)
    {
        bool assign = adapter.Driver != BridgeDriver.WinUsb;
        if (_busy) return;
        // Checked again at the moment of the click: the default adapter can change (one was plugged in).
        try { _windowsAdapter = await WindowsAdapterGuard.DefaultAdapterInstanceIdAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unknown which adapter Windows uses: taking one could cut off every paired device.
            if (assign)
            {
                CardNotice.ShowError(Notice, ex);
                return;
            }
        }
        if (assign && WindowsAdapterGuard.IsSameDevice(adapter.InstanceId, _windowsAdapter))
        {
            CardNotice.Show(Notice, InfoBarSeverity.Warning, Loc.Get("Bridge_WindowsAdapterTip"));
            return;
        }
        var dialog = new ContentDialog
        {
            Title = Loc.Format(assign ? "Bridge_AssignTitle" : "Bridge_RestoreTitle", adapter.DisplayName),
            Content = new TextBlock { Text = Loc.Get(assign ? "Bridge_AssignBody" : "Bridge_RestoreBody"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get(assign ? "Bridge_AssignWinUsb" : "Bridge_RestoreDriver"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await CardNotice.ShowDialogAsync(dialog, XamlRoot) != ContentDialogResult.Primary) return;
        await Operate(async () =>
        {
            Status.Text = Loc.Get("Bridge_DriverChanging");
            if (assign) await Bridge.AssignWinUsbAsync(adapter.InstanceId);
            else await Bridge.RestoreDriverAsync(adapter.InstanceId);
        });
    }

    /// <summary>
    /// Runs one operation at a time. Its progress shows in the status line, its result or failure in the message
    /// bar; the status line returns to the bridge's state afterwards whatever happened.
    /// </summary>
    private async Task Operate(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        CardNotice.Hide(Notice);
        Refresh();
        try { await action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowError(ex); }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void ShowError(Exception ex) => CardNotice.ShowError(Notice, ex, ex switch
    {
        BridgeRequestException request => request.BridgeMessage.IsEmpty ? request.Code : A2dpBridgeText.Format(request.BridgeMessage),
        BridgeUnavailableException => Loc.Get("Bridge_NotConnected"),
        _ => null,
    });

    private async void OnStartCore(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        Status.Text = Loc.Get("Bridge_Starting");
        if (!await Bridge.StartCoreAsync()) CardNotice.Show(Notice, InfoBarSeverity.Error, Loc.Get("Bridge_StartTimeout"));
    });

    private async void OnQuitCore(object sender, RoutedEventArgs e) => await Operate(Bridge.QuitCoreAsync);

    private void OnOpenWindow(object sender, RoutedEventArgs e)
    {
        if (Bridge.Installation is not { } installation) return;
        try
        {
            A2dpBridgeComponent.OpenWindow(installation);
            Bridge.Monitor.Wake();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowError(ex); }
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool on = AutoStart.IsOn;
        App.Instance.Host.Settings.Update(s => s with { A2dpBridgeAutoStart = on });
    }

    private async void OnRefreshAdapters(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        await LoadWindowsAdapterAsync();
        await Bridge.RefreshAdaptersAsync();
    });

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        string? folder = Bridge.Status.Snapshot?.ConfigDirectory;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try { using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ShowError(ex); }
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        Status.Text = Loc.Get("Component_Checking");
        var outcome = await Bridge.CheckForUpdateAsync(force: true);
        CardNotice.Show(Notice, outcome is ComponentUpdateOutcome.Available or ComponentUpdateOutcome.UpToDate ? InfoBarSeverity.Success : InfoBarSeverity.Warning, outcome switch
        {
            ComponentUpdateOutcome.Available => Loc.Format("Component_UpdateFound", Bridge.UpdateTarget?.Version ?? Bridge.OnlineRelease?.Version),
            ComponentUpdateOutcome.UpToDate => Loc.Get("Component_UpToDate"),
            ComponentUpdateOutcome.RateLimited => Loc.Get("Component_RateLimited"),
            ComponentUpdateOutcome.Offline => Loc.Get("Component_Offline"),
            _ => Loc.Get("Component_CheckFailed"),
        });
    });

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        int assigned = Bridge.AssignedAdapterCount;
        bool own = Bridge.Installation is { Origin: BridgeOrigin.Installed } || A2dpBridgeComponent.FindInstalled(Bridge.Component.Root) is not null;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("Bridge_UninstallBody"), TextWrapping = TextWrapping.Wrap });
        // Adapters still on WinUSB stay unusable for Windows unless given back; BlueCaster's own installation may still use them.
        var restore = new CheckBox { Content = Loc.Format("Bridge_UninstallRestore", assigned), IsChecked = !own };
        if (assigned > 0) panel.Children.Add(restore);
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Bridge_UninstallTitle"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Component_Uninstall"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await CardNotice.ShowDialogAsync(dialog, XamlRoot) != ContentDialogResult.Primary) return;
        bool restoreDrivers = assigned > 0 && restore.IsChecked == true;
        await Operate(async () =>
        {
            Status.Text = Loc.Get(restoreDrivers ? "Bridge_UninstallingRestore" : "Bridge_Uninstalling");
            await Bridge.UninstallAsync(restoreDrivers);
            CardNotice.Show(Notice, InfoBarSeverity.Success, Loc.Get("Bridge_Uninstalled"));
        });
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _download?.Cancel();

    private async void OnDownload(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        using var cancellation = new CancellationTokenSource();
        _download = cancellation;
        Progress.Visibility = Cancel.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        Status.Text = Loc.Get("Bridge_Downloading");
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                if (_download != cancellation) return;
                Progress.IsIndeterminate = false;
                Progress.Value = p.Total > 0 ? 100.0 * p.Received / p.Total : 0;
                Status.Text = Loc.Format("Bridge_DownloadProgress", p.Received / 1048576.0, p.Total / 1048576.0);
            });
            // A managed core of an older version is restarted from the new one unless it streams (then at its next start).
            if (await Bridge.InstallAsync(progress, cancellation.Token)) CardNotice.Show(Notice, InfoBarSeverity.Informational, Loc.Get("Bridge_UpdateAfterRestart"));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CardNotice.Show(Notice, InfoBarSeverity.Informational, Loc.Get("Bridge_Cancelled"));
        }
        finally
        {
            _download = null;
            Progress.Visibility = Cancel.Visibility = Visibility.Collapsed;
        }
    });

    private static string Origin(BridgeOrigin? origin) => Loc.Get(origin switch
    {
        BridgeOrigin.Managed => "Bridge_OriginManaged",
        BridgeOrigin.Installed => "Bridge_OriginInstalled",
        _ => "Bridge_OriginRunning",
    });

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
