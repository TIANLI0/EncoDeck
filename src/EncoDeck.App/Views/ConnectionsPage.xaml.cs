// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using EncoDeck.App.Controls;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Views;

/// <summary>
/// How the earbuds connect to phones and computers: dual connection with the paired-source list and the
/// preferred device, then the link switches (Swift Pair, do not disturb). Controls show what the earbuds
/// last confirmed; a change snaps back until the earbuds acknowledge it.
/// </summary>
public sealed partial class ConnectionsPage : Page
{
    private readonly List<byte[]?> _preferredItems = [];
    private string _sourceSignature = string.Empty;
    private bool _syncing;

    public ConnectionsPage()
    {
        InitializeComponent();
        ConnectionGroup.Title = Loc.Get("Group_Connection");
        ConnectionGroup.HasRowsChanged += (_, _) => Sync();
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelChanged;
            Features.Changed += OnFeaturesChanged;
            _sourceSignature = string.Empty;
            Sync();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            Features.Changed -= OnFeaturesChanged;
        };
    }

    public DeviceViewModel ViewModel => App.Instance.Device;

    private FeaturesViewModel Features => App.Instance.Features;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnFeaturesChanged(object? sender, EventArgs e) => Sync();

    private void Sync()
    {
        var f = Features;
        bool ready = f.IsReady;
        _syncing = true;
        try
        {
            NotReadyBar.IsOpen = !ready && ViewModel.HasDevice;
            SyncDual(f, ready);
            EmptyCard.Visibility = Show(ready && DualCard.Visibility == Visibility.Collapsed && !ConnectionGroup.HasRows);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncDual(FeaturesViewModel f, bool ready)
    {
        bool available = f.HasDualConnectionCard;
        DualCard.Visibility = Show(available);
        if (!available) return;
        // vivo and Sennheiser report their source list without a device-side switch.
        bool hasSwitch = f.HasSwitch(DeviceToggle.DualDeviceConnection);
        DualSwitch.Visibility = Show(hasSwitch);
        bool? enabled = f.Toggle(DeviceToggle.DualDeviceConnection);
        DualSwitch.IsOn = enabled == true;
        DualSwitch.IsEnabled = hasSwitch && ready && enabled is not null && !f.IsPending("toggle:DualDeviceConnection");

        var devices = f.SourceDevices;
        string signature = string.Join('|', devices.Select(d => $"{d.AddressText}/{d.State}/{d.IsThisDevice}/{d.IsAudioActive}/{d.Name}"))
            + $"#{ready}#{f.AreSourceDevicesCached}#{f.CanManageSourceDevices}#{f.CanConnectSourceDevices}#{f.IsPending("multi-connect")}#{f.IsPending("preferred-device")}#{Convert.ToHexString(f.PreferredAddress ?? [])}#{f.IsPreferredKnown}";
        if (signature == _sourceSignature) return;
        _sourceSignature = signature;

        SourceHost.Children.Clear();
        foreach (var device in devices) SourceHost.Children.Add(SourceRow(device, ready && !f.IsPending("multi-connect") && !f.AreSourceDevicesCached));
        // Brands that report only the multipoint switch have no list to manage.
        if (!f.CanManageSourceDevices && devices.Count > 0)
            SourceHost.Children.Add(new TextBlock { Text = Loc.Get(f.CanConnectSourceDevices ? "Dual_ConnectOnly" : "Dual_ManagementUnavailable"), TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });

        // Official options: auto select, the connected devices, and the current preferred device.
        _preferredItems.Clear();
        PreferredBox.Items.Clear();
        _preferredItems.Add(null);
        PreferredBox.Items.Add(Loc.Get("Dual_AutoSelect"));
        foreach (var device in devices.Where(d => d.IsConnected || (f.PreferredAddress is { } p && d.Address.AsSpan().SequenceEqual(p))))
        {
            _preferredItems.Add(device.Address);
            PreferredBox.Items.Add(DisplayName(device));
        }
        int selected = f.PreferredAddress is { } preferred
            ? _preferredItems.FindIndex(a => a is not null && a.AsSpan().SequenceEqual(preferred))
            : 0;
        PreferredBox.SelectedIndex = f.IsPreferredKnown ? selected : -1;
        PreferredSection.Visibility = Show(f.HasPreferredDevice && (f.IsPreferredKnown || devices.Count > 0));
        PreferredBox.IsEnabled = ready && f.HasPreferredDevice && f.IsPreferredKnown && !f.IsPending("preferred-device");
    }

    private Grid SourceRow(SourceDevice device, bool ready)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string symbol = FluentIcons.ForDeviceType(device.DeviceType);
        grid.Children.Add(new FluentIcon(symbol, 20) { VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = DisplayName(device), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            // A list restored from last session carries no addresses.
            // Index-only lists (Sennheiser) carry a one-byte key, not an address.
            Text = string.IsNullOrWhiteSpace(device.Name) || device.Address.Length != 6 ? DescribeSource(device) : $"{DescribeSource(device)} · {device.AddressText}",
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            IsTextSelectionEnabled = true,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        bool busy = device.State is 1 or 4;
        if (Features.CanConnectSourceDevices && !device.IsConnected && !busy)
        {
            actions.Children.Add(ActionButton("Dual_Connect", device, ready && Features.CanConnectAnother, OnConnectSourceClick));
            if (Features.CanRemoveSourceDevices) actions.Children.Add(ActionButton("Dual_Remove", device, ready, OnRemoveSourceClick));
        }
        else if (Features.CanManageSourceDevices && device.IsConnected && !device.IsThisDevice)
        {
            actions.Children.Add(ActionButton("Dual_Disconnect", device, ready, OnDisconnectSourceClick));
        }
        else if (device.IsThisDevice && device.IsConnected && App.Instance.Device.CanDisconnect)
        {
            // This PC: EncoDeck's own Disconnect, which also keeps it from reconnecting the earbuds.
            actions.Children.Add(ActionButton("Dual_Disconnect", device, !App.Instance.Device.IsChangingConnection, OnDisconnectThisPcClick));
        }
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return grid;
    }

    private static Button ActionButton(string key, SourceDevice device, bool ready, RoutedEventHandler handler)
    {
        var button = new Button { Content = Loc.Get(key), Tag = device, IsEnabled = ready };
        button.Click += handler;
        return button;
    }

    // ---- handlers ------------------------------------------------------------------------------

    private async void OnDualToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool requested = DualSwitch.IsOn;
        Sync();
        await Features.SetToggleAsync(DeviceToggle.DualDeviceConnection, requested);
        Sync();
    }

    private async void OnConnectSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SourceDevice device }) return;
        // Dual connection off: the earbuds leave this PC for the other device, and EncoDeck disconnects with them.
        if (Features.ConnectHandsOverThisPc)
        {
            if (!await ConfirmAsync("Dual_HandOver_Title", Loc.Format("Dual_HandOver_Message", DisplayName(device)), "Dual_Connect")) return;
        }
        // Official: with two devices already connected, connecting a third drops one of them.
        // Connect-only lists (Sennheiser) refuse instead of dropping a device, so there is nothing to confirm.
        else if (Features.CanManageSourceDevices && Features.SourceDevices.Count(d => d.IsConnected) >= 2
            && !await ConfirmAsync("Dual_ConnectNew_Title", Loc.Format("Dual_ConnectNew_Message", DisplayName(device)), "Dual_Connect"))
        {
            return;
        }
        await Features.ConnectSourceAsync(device);
        Sync();
    }

    private async void OnDisconnectThisPcClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Dual_DisconnectThisPc_Title", Loc.Get("Dual_DisconnectThisPc_Message"), "Dual_Disconnect")) return;
        await Features.DisconnectThisPcAsync();
        Sync();
    }

    private async void OnDisconnectSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SourceDevice device }) return;
        if (!await ConfirmAsync("Dual_Disconnect_Title", Loc.Format("Dual_Disconnect_Message", DisplayName(device)), "Dual_Disconnect")) return;
        await Features.MultiConnectAsync(MultiConnectOperation.Disconnect, device);
        Sync();
    }

    private async void OnRemoveSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SourceDevice device }) return;
        if (!await ConfirmAsync("Dual_Remove_Title", DisplayName(device), "Dual_Remove")) return;
        await Features.MultiConnectAsync(MultiConnectOperation.Remove, device);
        Sync();
    }

    private async void OnPreferredChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || PreferredBox.SelectedIndex < 0 || PreferredBox.SelectedIndex >= _preferredItems.Count) return;
        var address = _preferredItems[PreferredBox.SelectedIndex];
        _sourceSignature = string.Empty; // re-render from the confirmed state afterwards
        await Features.SetPreferredDeviceAsync(address);
        Sync();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<bool> ConfirmAsync(string titleKey, string message, string primaryKey)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get(titleKey),
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get(primaryKey),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string DisplayName(SourceDevice device) => string.IsNullOrWhiteSpace(device.Name) ? device.AddressText : device.Name;

    /// <summary>Row summaries as in the official dual-connection page.</summary>
    private static string DescribeSource(SourceDevice device) => device switch
    {
        { State: 1 } => Loc.Get("Dual_Connecting"),
        { IsThisDevice: true, IsConnected: true, IsAudioActive: true } => Loc.Get("Dual_InUseThis"),
        { IsThisDevice: true, IsConnected: true } => Loc.Get("Dual_ConnectedThis"),
        { IsThisDevice: true } => Loc.Get("Dual_This"),
        { IsConnected: true, IsAudioActive: true } => Loc.Get("Dual_InUse"),
        { IsConnected: true } => Loc.Get("Dual_Connected"),
        _ => Loc.Get("Dual_NotConnected"),
    };

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
