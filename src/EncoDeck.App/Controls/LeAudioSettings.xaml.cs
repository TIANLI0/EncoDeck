// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Experimental: switches OPPO earbuds whose firmware answers the LE/BR switch query (or that are
/// already bonded over LE) between Windows' Classic audio and LE Audio (LC3). See docs/LE_AUDIO.md.
/// </summary>
public sealed partial class LeAudioSettings : UserControl
{
    private CancellationTokenSource? _operation;
    private LeAudioBondState? _bond;
    private string? _deviceId;
    private ulong _address;
    private bool _busy;
    private bool _dialogOpen;
    private long _bondRequest;
    private LeAudioLinkState? _lastLinks;

    public LeAudioSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Switch.Content = Loc.Get("LeAudio_Switch");
            Restore.Content = Loc.Get("LeAudio_Restore");
            DisableClassic.Content = Loc.Get("LeAudio_DisableClassic");
            EnableClassic.Content = Loc.Get("LeAudio_EnableClassic");
            Cancel.Content = Loc.Get("Common_Cancel");
            Features.Changed += OnChanged;
            _deviceId = null;
            Refresh();
        };
        Unloaded += (_, _) => Features.Changed -= OnChanged;
    }

    private static FeaturesViewModel Features => App.Instance.Features;

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        // Unpairing removes the device from the app mid-operation; keep the card until it finishes.
        if (_busy) return;
        var state = Features.State;
        if (state.Profile is not OppoDeviceProfile || state.Device is not { Transport: DeviceTransport.Bluetooth } device)
        {
            // After an operation the earbuds may have left the app (pairing removed): keep the card,
            // its result and its buttons, which still work on the remembered address.
            Visibility = _address != 0 && Notice.IsOpen ? Visibility.Visible : Visibility.Collapsed;
            Sync();
            return;
        }
        if (device.DeviceId != _deviceId)
        {
            _deviceId = device.DeviceId;
            _address = device.Address.Value;
            _bond = null;
            _lastLinks = state.LeAudioLinks;
            _ = LoadBondAsync();
        }
        else if (_lastLinks != state.LeAudioLinks)
        {
            _lastLinks = state.LeAudioLinks;
            _ = LoadBondAsync();
        }
        Visibility = state.LeAudioSwitch is not null || _bond?.PrimaryBonded == true
            || state.Profile is OppoDeviceProfile { Definition.SupportsLeAudioPairing: true } ? Visibility.Visible : Visibility.Collapsed;
        NoiseNote.Visibility = state.Profile is OppoDeviceProfile { Definition.RejectsNoiseChangeOnLeAudio: true } ? Visibility.Visible : Visibility.Collapsed;
        Sync();
    }

    private async Task LoadBondAsync()
    {
        ulong address = _address;
        long request = ++_bondRequest;
        try
        {
            var bond = await LeAudioPairing.GetStateAsync(address);
            if (address != _address || request != _bondRequest) return;
            _bond = bond;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (address != _address || request != _bondRequest) return;
            _bond = null;
        }
        if (!_busy) Refresh();
    }

    private void Sync()
    {
        bool le = _bond?.PrimaryBonded == true;
        bool both = le && _bond!.SecondaryBonded;
        // Nothing bonded (e.g. an earlier attempt failed): offer both ways back.
        bool unpaired = _bond is { PrimaryBonded: false, ClassicBonded: false };
        var endpoints = _bond?.AudioEndpoints;
        bool classicDisabled = endpoints?.ClassicA2dpDisabled == true;
        bool bothLinks = _bond is { PrimaryConnected: true, SecondaryConnected: true };

        bool endpointUnknown = _bond is not null && endpoints is { State: LeAudioAudioEndpointState.Unknown };
        string key = unpaired ? "LeAudio_StatusUnpaired" : both ? "LeAudio_StatusLe" : le ? "LeAudio_StatusLeSingle" : "LeAudio_StatusClassic";
        Status.Text = _bond is null ? "" : Loc.Get(key)
            + (endpointUnknown ? " " + Loc.Get("LeAudio_StatusEndpointUnknown") : "");
        Status.Visibility = Status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Switch.Visibility = both ? Visibility.Collapsed : Visibility.Visible;
        Restore.Visibility = le || unpaired ? Visibility.Visible : Visibility.Collapsed;
        // Disabling is only offered for an A2DP render endpoint, after both LE links were observed.
        DisableClassic.Visibility = endpoints?.ClassicA2dpPresent == true && bothLinks && !_busy && !_dialogOpen
            ? Visibility.Visible : Visibility.Collapsed;
        EnableClassic.Visibility = classicDisabled && !_busy && !_dialogOpen ? Visibility.Visible : Visibility.Collapsed;
        Switch.IsEnabled = Restore.IsEnabled = !_busy && !_dialogOpen && _bond is not null;
        DisableClassic.IsEnabled = EnableClassic.IsEnabled = !_busy && !_dialogOpen;
        OpenBluetooth.IsEnabled = !_busy && !_dialogOpen;
        Progress.Visibility = Cancel.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        Message.Visibility = _busy && Message.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnSwitch(object sender, RoutedEventArgs e)
    {
        ulong target = _address;
        if (await ConfirmAsync("LeAudio_ConfirmSwitch", "LeAudio_Switch") && target == _address)
            await RunAsync((address, progress, ct) => LeAudioPairing.SwitchToLeAudioAsync(address, progress, ct,
                (on, token) => Features.State.Device?.Address.Value == address ? Features.SetLeBrSwitchAsync(on, token) : Task.FromResult(CommandResult.NotConnected),
                token => Features.State.Device?.Address.Value == address ? Features.DisableMultipointForLeAudioAsync(token) : Task.FromResult(CommandResult.NotConnected),
                reportLeTransport: token => Features.State.Device?.Address.Value == address ? Features.ReportLeAudioTransportAsync(token) : Task.FromResult(CommandResult.NotConnected)));
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        ulong target = _address;
        if (await ConfirmAsync("LeAudio_ConfirmRestore", "LeAudio_Restore") && target == _address)
            await RunAsync((address, progress, ct) => LeAudioPairing.RemoveLeAudioAsync(address, progress, ct,
                (on, token) => Features.State.Device?.Address.Value == address ? Features.SetLeBrSwitchAsync(on, token) : Task.FromResult(CommandResult.NotConnected)));
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private async void OnDisableClassic(object sender, RoutedEventArgs e)
    {
        ulong address = _address;
        await RunEndpointAsync(async ct =>
        {
            // Re-read both LE links instead of trusting the last UI snapshot.
            var bond = await LeAudioPairing.GetStateAsync(address, ct);
            return await LeAudioClassicAudioEndpoint.SuppressAsync(
                address, bond.PrimaryConnected && bond.SecondaryConnected, ct);
        });
    }

    private async void OnEnableClassic(object sender, RoutedEventArgs e) =>
        await RunEndpointAsync(ct => LeAudioClassicAudioEndpoint.RestoreAsync(_address, ct));

    private async Task RunEndpointAsync(Func<CancellationToken, Task<LeAudioEndpointResult>> operation)
    {
        if (_busy) return;
        _busy = true;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        CardNotice.Hide(Notice);
        Message.Text = "";
        Sync();
        try
        {
            var result = await operation(cancellation.Token);
            CardNotice.Show(Notice, Severity(result.Outcome), result.Outcome == LeAudioEndpointOutcome.Failed
                ? Loc.Format("LeAudio_Endpoint_Failed", result.Detail ?? "")
                : Loc.Get($"LeAudio_Endpoint_{result.Outcome}") + (result.Detail is { } detail ? $" ({detail})" : ""));
        }
        catch (OperationCanceledException)
        {
            CardNotice.Show(Notice, InfoBarSeverity.Informational, Loc.Get("LeAudio_Cancelled"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CardNotice.ShowError(Notice, ex, Loc.Format("LeAudio_Error", ex.Message));
        }
        finally
        {
            _operation = null;
            _busy = false;
            _bond = null;
            Sync();
            _ = LoadBondAsync();
        }
    }

    private async Task RunAsync(Func<ulong, IProgress<LeAudioPairingStage>, CancellationToken, Task<LeAudioPairingResult>> operation)
    {
        if (_busy) return;
        _busy = true;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        CardNotice.Hide(Notice);
        Message.Text = "";
        Sync();
        try
        {
            var progress = new Progress<LeAudioPairingStage>(stage =>
            {
                Message.Text = Loc.Get($"LeAudio_Stage_{stage}");
                Message.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
            });
            var result = await operation(_address, progress, cancellation.Token);
            CardNotice.Show(Notice, Severity(result.Outcome),
                Loc.Get($"LeAudio_Result_{result.Outcome}") + (result.Detail is { } detail ? $" ({detail})" : ""));
        }
        catch (OperationCanceledException)
        {
            CardNotice.Show(Notice, InfoBarSeverity.Informational, Loc.Get("LeAudio_Cancelled"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CardNotice.ShowError(Notice, ex, Loc.Format("LeAudio_Error", ex.Message));
        }
        finally
        {
            _operation = null;
            _busy = false;
            _bond = null;
            Sync();
            _ = LoadBondAsync();
        }
    }

    private async Task<bool> ConfirmAsync(string messageKey, string primaryKey)
    {
        // A second ContentDialog while one is open throws; a double click must not open two.
        if (_dialogOpen || _busy) return false;
        _dialogOpen = true;
        Sync();
        try
        {
            return await ShowConfirmAsync(messageKey, primaryKey);
        }
        finally
        {
            _dialogOpen = false;
            Sync();
        }
    }

    private async Task<bool> ShowConfirmAsync(string messageKey, string primaryKey)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Get("LeAudio_ConfirmTitle"),
            Content = new TextBlock { Text = Loc.Get(messageKey), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get(primaryKey),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await CardNotice.ShowDialogAsync(dialog, XamlRoot) == ContentDialogResult.Primary;
    }

    private static InfoBarSeverity Severity(LeAudioPairingOutcome outcome) => outcome switch
    {
        LeAudioPairingOutcome.Paired => InfoBarSeverity.Success,
        // Pairings removed on purpose: the next step is pairing again in Windows.
        LeAudioPairingOutcome.Removed => InfoBarSeverity.Informational,
        LeAudioPairingOutcome.PairedWithClassicAudio or LeAudioPairingOutcome.PairedButLeAudioEndpointMissing
            or LeAudioPairingOutcome.PairedWaitingForLinks or LeAudioPairingOutcome.PrimaryOnly => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Error,
    };

    private static InfoBarSeverity Severity(LeAudioEndpointOutcome outcome) => outcome switch
    {
        LeAudioEndpointOutcome.Disabled or LeAudioEndpointOutcome.Enabled => InfoBarSeverity.Success,
        LeAudioEndpointOutcome.AlreadyLeAudioOnly or LeAudioEndpointOutcome.NoClassicEndpoint
            or LeAudioEndpointOutcome.NothingToRestore => InfoBarSeverity.Informational,
        LeAudioEndpointOutcome.Failed => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Warning,
    };
}
