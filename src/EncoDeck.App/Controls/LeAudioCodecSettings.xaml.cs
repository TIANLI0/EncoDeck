// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using EncoDeck.App.Services;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Platform.LeAudioCodec;
using EncoDeck.Platform.LeAudioSync;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Experimental: lets the user pick the LC3 preset and the transport (latency first or reliability first) Windows asks
/// LE Audio earbuds for. Applying shows the administrator prompt and hooks Windows' Bluetooth audio service
/// (<see cref="LeAudioCodecHook"/>); it takes effect with the next LE Audio stream, which the card and a notification
/// report (<see cref="LeAudioCodecCompanion"/>). The hook lasts until that service restarts.
/// </summary>
public sealed partial class LeAudioCodecSettings : UserControl
{
    /// <summary>Earbud models the Intel sync schedule was verified on; no other earbuds are offered it.</summary>
    private static readonly HashSet<string> SyncScheduleModels = new(StringComparer.OrdinalIgnoreCase) { "oppo-enco-x4" };

    private LeAudioCodecSupport? _support;
    private bool _busy;
    private bool _refreshQueued;

    public LeAudioCodecSettings()
    {
        InitializeComponent();
        foreach (var preset in LeAudioCodecPreset.All)
            Preset.Items.Add(new ComboBoxItem { Content = LeAudioCodecText.Preset(preset), Tag = preset });
        foreach (var mode in Enum.GetValues<LeAudioLatencyMode>())
            Mode.Items.Add(new ComboBoxItem { Content = LeAudioCodecText.Mode(mode), Tag = mode });
        Loaded += async (_, _) =>
        {
            Apply.Content = Loc.Get("LeAudioCodec_Apply");
            LeAudio.Changed += OnChanged;
            Codec.Changed += OnChanged;
            Refresh(selectApplied: true);
            if (!LeAudioCodecCompanion.Available || _support is not null) return;
            _support = await Task.Run(() => LeAudioCodecHook.CheckSupportAsync());
            Refresh(selectApplied: true);
        };
        Unloaded += (_, _) =>
        {
            LeAudio.Changed -= OnChanged;
            Codec.Changed -= OnChanged;
        };
    }

    /// <summary>Tracks whether LE Audio (LC3) earbuds are connected; the card is only offered then.</summary>
    private static LeAudioSyncCompanion LeAudio => App.Instance.Host.LeAudioSync;

    private static LeAudioCodecCompanion Codec => App.Instance.Host.LeAudioCodec;

    private LeAudioCodecPreset? SelectedPreset => (Preset.SelectedItem as ComboBoxItem)?.Tag as LeAudioCodecPreset;

    private LeAudioLatencyMode SelectedMode => (Mode.SelectedItem as ComboBoxItem)?.Tag is LeAudioLatencyMode mode ? mode : LeAudioLatencyMode.Auto;

    /// <summary>
    /// The LE Audio earbuds' address when they may get the Intel sync schedule: Intel LE Audio offload on this PC and a
    /// model it was verified on.
    /// </summary>
    private static ulong? SyncScheduleAddress =>
        LeAudio.Support == LeAudioSyncSupport.Supported
        && App.Instance.Host.Store.Current.Profile?.ModelId is { } model && SyncScheduleModels.Contains(model)
            ? LeAudio.LeAudioAddress
            : null;

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() => { _refreshQueued = false; Refresh(selectApplied: false); });
    }

    private void OnChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedPreset is not { } preset) return;
        string codec = preset.CodecDelayMs.ToString("0.#", CultureInfo.CurrentCulture);
        Latency.Text = SelectedMode switch
        {
            LeAudioLatencyMode.LowLatency => Loc.Format("LeAudioCodec_Latency_Fixed", codec, preset.GameLatencyLimitMs, 5, LeAudioCodecPreset.PresentationDelayMs),
            LeAudioLatencyMode.HighReliability => Loc.Format("LeAudioCodec_Latency_Fixed", codec, preset.MediaLatencyLimitMs, 13, LeAudioCodecPreset.PresentationDelayMs),
            _ => Loc.Format("LeAudioCodec_Latency", codec, preset.GameLatencyLimitMs, preset.MediaLatencyLimitMs, LeAudioCodecPreset.PresentationDelayMs),
        };
    }

    private void Refresh(bool selectApplied)
    {
        // Only where it applies: a supported installation with LE Audio (LC3) earbuds connected. While applying, stay
        // visible even if the link drops for a moment.
        bool supported = LeAudioCodecCompanion.Available && _support == LeAudioCodecSupport.Supported;
        Visibility = supported && (LeAudio.LeAudioConnected || _busy) ? Visibility.Visible : Visibility.Collapsed;
        if (Visibility == Visibility.Collapsed) return;

        LeAudioCodecApplied applied = LeAudioCodecHook.ReadApplied();
        ulong? syncAddress = SyncScheduleAddress;
        SyncPanel.Visibility = syncAddress is null ? Visibility.Collapsed : Visibility.Visible;
        if (selectApplied)
        {
            Preset.SelectedItem = Preset.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, applied.Preset));
            Mode.SelectedItem = Mode.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, applied.Mode));
            NoCall.IsOn = applied.NoCallMode;
            // On by default for eligible earbuds, unless the hook already serves other earbuds.
            SyncSchedule.IsOn = applied.SyncTarget == 0 || applied.SyncTarget == syncAddress;
        }
        Preset.IsEnabled = Mode.IsEnabled = NoCall.IsEnabled = SyncSchedule.IsEnabled = Apply.IsEnabled = !_busy;

        string choice = $"{LeAudioCodecText.Preset(applied.Preset)} · {Loc.Get($"LeAudioCodec_ModeName_{applied.Mode}")}"
            + (applied.NoCallMode ? $" · {Loc.Get("LeAudioCodec_NoCallName")}" : "")
            + (applied.SyncTarget != 0 ? $" · {Loc.Get("LeAudioCodec_SyncName")}" : "");
        Status.Text = Codec.Pending ? Loc.Get("LeAudioCodec_Status_Pending")
            : applied.IsWindowsDefault ? Loc.Get("LeAudioCodec_Status_Default")
            : Loc.Format(applied.Active ? "LeAudioCodec_Status_Active" : "LeAudioCodec_Status_Lost", choice);
        Link.Text = Codec.Link is { } link
            ? Loc.Format("LeAudioCodec_Link", LeAudioCodecText.Link(link), link.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
            : Loc.Get("LeAudioCodec_LinkUnknown");
    }

    /// <summary>Enables or disables the earbuds' LE Audio microphone endpoint; returns an error text, or null.</summary>
    private static async Task<string?> SetMicrophoneAsync(bool enabled)
    {
        if (LeAudio.LeAudioAddress is not { } address) return null;
        try
        {
            var result = await LeAudioMicrophone.SetEnabledAsync(address, enabled);
            return result.Success ? null : result.Detail ?? "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ex.Message;
        }
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedPreset is not { } preset) return;
        LeAudioLatencyMode mode = SelectedMode;
        bool noCall = NoCall.IsOn;
        bool hadNoCall = LeAudioCodecHook.ReadApplied() is { NoCallMode: true, Active: true };
        ulong? syncAddress = SyncSchedule.IsOn ? SyncScheduleAddress : null;
        _busy = true;
        Apply.Content = Loc.Get("LeAudioCodec_Applying");
        CardNotice.Hide(Notice);
        Refresh(selectApplied: false);
        try
        {
            // Windows prepares a stream earbud by earbud: the schedule has to name every earbud of the set.
            IReadOnlyList<ulong>? syncTargets = null;
            if (syncAddress is { } address)
            {
                try { syncTargets = await LeAudioLink.GetSetAddressesAsync(address); }
                catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    CardNotice.Show(Notice, InfoBarSeverity.Error, Loc.Format("LeAudioCodec_Result_SyncAddresses", ex.Message));
                    return;
                }
            }
            int result = await LeAudioCodecCompanion.ApplyAsync(preset, mode, noCall, syncTargets);
            if (result is LeAudioCodecHook.Result.Ok or LeAudioCodecHook.Result.SyncUnsupported)
            {
                // Confirmed (card and notification) once Windows sets up the next LE Audio stream with it. The sync
                // schedule sets its own transport, so only the preset has to match then.
                bool syncApplied = syncTargets is { Count: > 0 } && result == LeAudioCodecHook.Result.Ok;
                Codec.Expect(preset, syncApplied ? LeAudioLatencyMode.Auto : mode);
                // Rebuild now when the calibration holds the link (and check it again, since every rebuild draws a new
                // left/right alignment).
                var sync = LeAudio.Guard;
                bool rebuild = App.Instance.Host.Settings.Current.LeAudioSyncGuard
                    && sync.Status.State is LeAudioSyncState.NeedsCheck or LeAudioSyncState.Aligned or LeAudioSyncState.Checking;
                if (rebuild) sync.ReportOffset();
                string message = Loc.Get(rebuild ? "LeAudioCodec_Result_OkRebuilt" : "LeAudioCodec_Result_Ok");
                // Without the call link the earbud microphone has no use: disable it so programs do not find it.
                if (noCall != hadNoCall && await SetMicrophoneAsync(enabled: !noCall) is { } error)
                    message += " " + Loc.Format("LeAudio_MicFailed", error);
                if (noCall && !hadNoCall) message += " " + Loc.Get("LeAudioCodec_Result_NoCallReconnect");
                if (result == LeAudioCodecHook.Result.SyncUnsupported) message += " " + Loc.Get("LeAudioCodec_Result_SyncUnsupported");
                CardNotice.Show(Notice, result == LeAudioCodecHook.Result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning, message);
            }
            else
            {
                string key = result switch
                {
                    LeAudioCodecHook.Result.ServiceNotRunning => "LeAudioCodec_Result_ServiceNotRunning",
                    LeAudioCodecHook.Result.UnsupportedWindows => "LeAudioCodec_Support_UnsupportedWindows",
                    LeAudioCodecHook.Result.SymbolsUnavailable => "LeAudioCodec_Result_SymbolsUnavailable",
                    LeAudioCodecHook.Result.ComponentMissing => "LeAudioCodec_Support_ComponentMissing",
                    _ => "LeAudioCodec_Result_Failed",
                };
                CardNotice.Show(Notice, InfoBarSeverity.Error, Loc.Format(key, result, LeAudioCodecHook.HookDirectory));
            }
        }
        catch (OperationCanceledException)
        {
            CardNotice.Show(Notice, InfoBarSeverity.Warning, Loc.Get("LeAudioCodec_Result_Declined"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            CardNotice.Show(Notice, InfoBarSeverity.Error, Loc.Format("LeAudioCodec_Result_Failed", ex.Message, LeAudioCodecHook.HookDirectory));
        }
        finally
        {
            _busy = false;
            Apply.Content = Loc.Get("LeAudioCodec_Apply");
            Refresh(selectApplied: false);
        }
    }
}
