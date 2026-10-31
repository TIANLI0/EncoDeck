// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.Platform.LeAudioSync;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Experimental: holds the LE Audio link of PCs whose Intel Bluetooth runs LE Audio through Smart Sound Technology
/// offload, and lets the user check by ear (and rebuild) its left/right alignment. Only offered there
/// (see <see cref="LeAudioSyncEligibility"/>).
/// </summary>
public sealed partial class LeAudioSyncSettings : UserControl
{
    private bool _syncing;
    private bool _refreshQueued;

    public LeAudioSyncSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Check.Content = Loc.Get("LeAudioSync_Check");
            Centered.Content = Loc.Get("LeAudioSync_Centered");
            Offset.Content = Loc.Get("LeAudioSync_Offset");
            CheckPrompt.Text = Loc.Get("LeAudioSync_CheckPrompt");
            Companion.Changed += OnChanged;
            Refresh();
        };
        Unloaded += (_, _) => Companion.Changed -= OnChanged;
    }

    private static LeAudioSyncCompanion Companion => App.Instance.Host.LeAudioSync;

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() => { _refreshQueued = false; Refresh(); });
    }

    private void Refresh()
    {
        LeAudioSyncSupport? support = Companion.Support;
        LeAudioSyncStatus status = Companion.Guard.Status;
        bool on = App.Instance.Host.Settings.Current.LeAudioSyncGuard;
        bool supported = support == LeAudioSyncSupport.Supported;
        // Only where it applies: an affected PC with LE Audio (LC3) earbuds connected.
        Visibility = supported && Companion.LeAudioConnected ? Visibility.Visible : Visibility.Collapsed;

        _syncing = true;
        Enable.IsOn = on;
        // Off is always allowed; on only where the offset occurs.
        Enable.IsEnabled = supported || on;
        _syncing = false;

        string? requirement = support switch
        {
            null => Loc.Get("LeAudioSync_Support_Checking"),
            LeAudioSyncSupport.Supported => null,
            _ => Loc.Get($"LeAudioSync_Support_{support}"),
        };
        Requirement.Text = requirement ?? "";
        Requirement.Visibility = requirement is null ? Visibility.Collapsed : Visibility.Visible;

        string? text = on && supported ? StatusText(status) : null;
        Status.Text = text ?? "";
        Status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (status.Detail is { Length: > 0 } detail && on) CardNotice.Show(Notice, InfoBarSeverity.Warning, detail);
        else CardNotice.Hide(Notice);

        bool checking = on && status.State == LeAudioSyncState.Checking;
        CheckPrompt.Visibility = checking ? Visibility.Visible : Visibility.Collapsed;
        Centered.Visibility = checking ? Visibility.Visible : Visibility.Collapsed;
        Offset.Visibility = checking ? Visibility.Visible : Visibility.Collapsed;
        Check.Visibility = on && status.State is LeAudioSyncState.NeedsCheck or LeAudioSyncState.Aligned
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string StatusText(LeAudioSyncStatus status) => status.State switch
    {
        LeAudioSyncState.Off => "",
        LeAudioSyncState.Unsupported => Loc.Get($"LeAudioSync_Support_{status.Support}"),
        LeAudioSyncState.Rebuilding => Loc.Format("LeAudioSync_Status_Rebuilding", status.Rebuilds),
        _ => Loc.Get($"LeAudioSync_Status_{status.State}"),
    };

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool on = Enable.IsOn;
        if (on && Companion.Support != LeAudioSyncSupport.Supported)
        {
            Refresh();
            return;
        }
        App.Instance.Host.Settings.Update(s => s with { LeAudioSyncGuard = on });
    }

    private void OnCheck(object sender, RoutedEventArgs e) => Companion.Guard.StartCheck();

    private void OnCentered(object sender, RoutedEventArgs e) => Companion.Guard.ConfirmAligned();

    private void OnOffset(object sender, RoutedEventArgs e) => Companion.Guard.ReportOffset();
}
