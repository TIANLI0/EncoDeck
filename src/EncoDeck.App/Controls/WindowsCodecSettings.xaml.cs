// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.Platform.A2dpCodec;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Experimental: the A2DP codec from Windows instead of the earbuds. Authorizing shows the administrator prompt once
/// and registers Windows' own trace session (<see cref="WindowsA2dpCodecLog"/>); <see cref="WindowsCodecCompanion"/>
/// then reads its file. Works in the Store package too.
/// </summary>
public sealed partial class WindowsCodecSettings : UserControl
{
    private bool _busy;
    private bool _refreshQueued;

    public WindowsCodecSettings()
    {
        InitializeComponent();
        Visibility = WindowsA2dpCodecLog.IsSupported ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            Authorize.Content = Loc.Get("WindowsCodec_Authorize");
            Remove.Content = Loc.Get("WindowsCodec_Remove");
            Codec.Changed += OnChanged;
            Refresh();
        };
        Unloaded += (_, _) => Codec.Changed -= OnChanged;
    }

    private static WindowsCodecCompanion Codec => App.Instance.Host.WindowsCodec;

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() => { _refreshQueued = false; Refresh(); });
    }

    private void Refresh()
    {
        bool authorized = WindowsA2dpCodecLog.IsAuthorized;
        Status.Text = !authorized ? Loc.Get("WindowsCodec_Status_Off")
            : Codec.KnownDevices > 0 ? Loc.Get("WindowsCodec_Status_On") : Loc.Get("WindowsCodec_Status_Waiting");
        Authorize.Visibility = authorized ? Visibility.Collapsed : Visibility.Visible;
        Remove.Visibility = authorized ? Visibility.Visible : Visibility.Collapsed;
        Authorize.IsEnabled = Remove.IsEnabled = !_busy;
    }

    private void OnAuthorize(object sender, RoutedEventArgs e) => Run(WindowsA2dpCodecLog.AuthorizeAsync, expectAuthorized: true);

    private void OnRemove(object sender, RoutedEventArgs e) => Run(WindowsA2dpCodecLog.RemoveAsync, expectAuthorized: false);

    private async void Run(Func<Task> action, bool expectAuthorized)
    {
        if (_busy) return;
        _busy = true;
        CardNotice.Hide(Notice);
        Refresh();
        try
        {
            await action();
            await Codec.RefreshAsync();
            if (WindowsA2dpCodecLog.IsAuthorized != expectAuthorized)
                CardNotice.Show(Notice, InfoBarSeverity.Error, Loc.Format("WindowsCodec_Result_NotApplied", WindowsA2dpCodecLog.LogDirectory));
        }
        catch (OperationCanceledException)
        {
            CardNotice.Show(Notice, InfoBarSeverity.Warning, Loc.Get("WindowsCodec_Result_Declined"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            CardNotice.Show(Notice, InfoBarSeverity.Error, Loc.Format("WindowsCodec_Result_Failed", ex.Message));
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
