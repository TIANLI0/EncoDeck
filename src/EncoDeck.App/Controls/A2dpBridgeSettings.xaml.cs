// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using EncoDeck.App.Services;
using EncoDeck.Platform.A2dpBridge;
using EncoDeck.Platform.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Experimental: BlueCaster on a dedicated USB adapter (LDAC, LHDC V5, aptX HD… without test signing). EncoDeck only
/// connects to it: it can run BlueCaster's own installer once, start the core (also with EncoDeck) and open its window,
/// and shows what it streams. Updates, uninstalling, adapters, earbuds, connection profiles and volume are BlueCaster's
/// own. See docs/A2DP_BRIDGE.md.
/// </summary>
public sealed partial class A2dpBridgeSettings : UserControl
{
    private CancellationTokenSource? _download;
    private bool _busy, _syncing, _refreshQueued;

    public A2dpBridgeSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Cancel.Content = Loc.Get("Common_Cancel");
            StartCore.Content = Loc.Get("Bridge_Start");
            OpenWindow.Content = Loc.Get("Bridge_OpenWindow");
            Download.Content = Loc.Get("Bridge_Install");
            Bridge.Changed += OnBridgeChanged;
            Bridge.RefreshInstallations();
            Bridge.Monitor.Wake();
            Refresh();
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

    private void Refresh()
    {
        var status = Bridge.Status;
        var installation = Bridge.Installation;
        bool manage = A2dpBridgeCompanion.CanManage;
        bool connected = status.IsConnected;

        string text = status.Link switch
        {
            BridgeLink.Connected => Loc.Format("Bridge_Running", status.ServerVersion ?? "?", Origin(installation?.Origin)),
            BridgeLink.Incompatible => Loc.Format("Bridge_Incompatible", status.Detail),
            BridgeLink.Connecting => Loc.Get("Bridge_Connecting"),
            _ when installation is not null => Loc.Format("Bridge_Stopped", installation.Version ?? "?", Origin(installation.Origin)),
            _ when !manage => Loc.Get("Bridge_StoreMissing"),
            _ => Loc.Get("Bridge_Missing"),
        };
        if (!manage && installation is not null && !connected) text += Environment.NewLine + Loc.Get("Bridge_StoreStart");
        // An operation in progress keeps its own status line until it ends.
        if (_download is null && !_busy) Status.Text = text;

        bool downloading = _download is not null;
        StandaloneLink.Visibility = Show(!manage && installation is null);
        // Only while BlueCaster's own installation is missing: once installed, it updates and uninstalls itself.
        Download.Visibility = Show(Bridge.CanInstall && !connected && !downloading);
        Download.IsEnabled = !_busy;
        StartCore.Visibility = Show(manage && installation is not null && status.Link == BridgeLink.NotRunning);
        OpenWindow.Visibility = Show(manage && installation is not null);
        StartCore.IsEnabled = OpenWindow.IsEnabled = !_busy;
        AutoStart.Visibility = Show(manage && installation is not null);
        _syncing = true;
        try { AutoStart.IsOn = App.Instance.Host.Settings.Current.A2dpBridgeAutoStart; }
        finally { _syncing = false; }

        Live.Visibility = Show(connected);
        if (connected) StreamText.Text = DescribeStream(status.Snapshot!);
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
                Progress.IsIndeterminate = p.Received >= p.Total;
                // The installer itself is never cut short.
                if (p.Received >= p.Total) Cancel.Visibility = Visibility.Collapsed;
                Progress.Value = p.Total > 0 ? 100.0 * p.Received / p.Total : 0;
                // Once downloaded, BlueCaster's own installer runs (with its own progress window).
                Status.Text = p.Received >= p.Total ? Loc.Get("Bridge_Installing") : Loc.Format("Bridge_DownloadProgress", p.Received / 1048576.0, p.Total / 1048576.0);
            });
            await Bridge.InstallAsync(progress, cancellation.Token);
            CardNotice.Show(Notice, InfoBarSeverity.Success, Loc.Get("Bridge_Installed"));
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
