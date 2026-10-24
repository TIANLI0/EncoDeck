// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Controls;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EncoDeck.App.Views;

/// <summary>
/// HeyMelody's device page laid out for a landscape window: the device stage (name, levels,
/// rotatable 3D model) on the left; noise control, device management and sound on the right.
/// </summary>
public sealed partial class HomePage : Page
{
    private string _devicesSignature = string.Empty;
    private bool _syncing;

    public HomePage()
    {
        InitializeComponent();
        AutomationProperties.SetName(Model, Loc.Get("Home_Model_Name"));
        Loaded += (_, _) =>
        {
            Features.Changed += OnFeaturesChanged;
            App.Instance.Host.Devices.KnownDevicesChanged += OnKnownDevicesChanged;
            App.Instance.Host.Cups.Changed += OnKnownDevicesChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            Sync();
            SyncSwitcher();
        };
        Unloaded += (_, _) =>
        {
            Features.Changed -= OnFeaturesChanged;
            App.Instance.Host.Devices.KnownDevicesChanged -= OnKnownDevicesChanged;
            App.Instance.Host.Cups.Changed -= OnKnownDevicesChanged;
            ViewModel.PropertyChanged -= OnViewModelChanged;
        };
    }

    public DeviceViewModel ViewModel => App.Instance.Device;

    private FeaturesViewModel Features => App.Instance.Features;

    private void OnFeaturesChanged(object? sender, EventArgs e) => Sync();

    private void Sync()
    {
        var f = Features;
        bool ready = f.IsReady;
        _syncing = true;
        try
        {
            SyncDevices(f, ready);
            bool noise = f.Has(DeviceCapability.NoiseCancellation) &&
                (ViewModel.HasAncMode || ViewModel.HasTransparencyMode || ViewModel.HasAdaptiveMode);
            NoiseCard.Visibility = noise ? Visibility.Visible : Visibility.Collapsed;
            bool eq = f.Has(DeviceCapability.Equalizer) || f.Has(DeviceCapability.CustomEqualizer);
            EqRow.Visibility = eq ? Visibility.Visible : Visibility.Collapsed;

            // Rows follow the (cached) model capabilities only, not whether the live value has been
            // read yet, so the layout is the same right after a restart as once connected.
            bool spatial = f.Has(DeviceCapability.SpatialAudio);
            SpatialRow.Visibility = spatial ? Visibility.Visible : Visibility.Collapsed;
            SpatialValue.Text = f.State.Spatial is null ? string.Empty : f.SpatialMode switch
            {
                SpatialAudioMode.Fixed => Loc.Get("Spatial_Fixed"),
                SpatialAudioMode.HeadTracking => Loc.Get("Spatial_HeadTracking"),
                _ => Loc.Get("Spatial_Off"),
            };

            bool game = f.Has(DeviceCapability.GamingMode) && f.HasSwitch(DeviceToggle.GamingMode);
            GameRow.Visibility = game ? Visibility.Visible : Visibility.Collapsed;
            bool? on = f.Toggle(DeviceToggle.GamingMode);
            GameSwitch.IsOn = on == true;
            GameSwitch.IsEnabled = ready && on is not null && !f.IsPending("toggle:");
            SoundCard.Visibility = eq || spatial || game ? Visibility.Visible : Visibility.Collapsed;
            UpdateLayoutForWidth(PageRoot.ActualWidth);
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---- device management (HeyMelody's 设备管理 card) --------------------------------------------

    private void SyncDevices(FeaturesViewModel f, bool ready)
    {
        // The connected-device list is read with OPPO's multi-connect commands.
        bool list = f.Has(DeviceCapability.DualDeviceConnection) && f.Model is not null;
        bool toggle = f.Has(DeviceCapability.DualDeviceConnection) && f.HasSwitch(DeviceToggle.DualDeviceConnection);
        bool available = list || toggle;
        DevicesCard.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        DualRow.Visibility = toggle ? Visibility.Visible : Visibility.Collapsed;
        bool? enabled = f.Toggle(DeviceToggle.DualDeviceConnection);
        DualSwitch.IsOn = enabled == true;
        DualSwitch.IsEnabled = ready && enabled is not null && !f.IsPending("toggle:DualDeviceConnection");
        DeviceHost.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        if (!list || f.State.SourceDevices is null)
        {
            _devicesSignature = string.Empty;
            DeviceHost.Children.Clear();
            DeviceHost.ColumnDefinitions.Clear();
            return;
        }

        var devices = f.SourceDevices;
        string signature = string.Join('|', devices.Select(d => $"{d.AddressText}/{d.State}/{d.IsAudioActive}/{d.Name}")) + $"#{ready}#{f.State.Device?.DeviceId}#{f.State.Profile?.ModelId}";
        if (signature == _devicesSignature) return;
        _devicesSignature = signature;

        DeviceHost.Children.Clear();
        DeviceHost.ColumnDefinitions.Clear();
        int column = 0;
        foreach (var device in devices)
        {
            DeviceHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = DeviceCell(device, ready);
            Grid.SetColumn(cell, column++);
            DeviceHost.Children.Add(cell);
        }
    }

    /// <summary>
    /// A round device icon (accent when connected) with its name and state. Tapping a disconnected
    /// device connects it, tapping a connected one disconnects it (after a confirmation); this PC is
    /// disconnected from Windows Bluetooth settings, as the official client does for the phone.
    /// </summary>
    private Button DeviceCell(SourceDevice device, bool ready)
    {
        // Device type icons: 1 phone, 2 computer, 3 tablet, 4 watch.
        string glyph = device.DeviceType switch { 1 => "", 2 => "", 3 => "", 4 => "", _ => "" };
        bool connected = device.IsConnected;
        var circle = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)Application.Current.Resources[connected ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush"],
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 22,
                Foreground = (Brush)Application.Current.Resources[connected ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
            },
        };
        var name = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(device.Name) ? device.AddressText : device.Name,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[connected ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
        };
        bool busy = device.State is 1 or 4;
        var status = new TextBlock
        {
            Text = Loc.Get(device.IsThisDevice ? "Home_DeviceThisPc" : busy ? "Home_DeviceBusy" : connected ? "Home_DeviceConnected" : "Home_DeviceTapToConnect"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
        };
        var content = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(circle);
        var label = new StackPanel { Spacing = 2 };
        label.Children.Add(name);
        label.Children.Add(status);
        content.Children.Add(label);
        var cell = new Button
        {
            Content = content,
            Tag = device,
            // Subtle: transparent at rest and when disabled (connected devices are not tappable).
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Every cell is as tall as the row and starts at the top, so the circles line up even
            // when one name wraps to two lines.
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(4, 8, 4, 8),
            IsEnabled = ready && !busy,
        };
        AutomationProperties.SetName(cell, $"{name.Text}, {status.Text}");
        ToolTipService.SetToolTip(cell, Loc.Get(device.IsThisDevice ? "Home_DeviceThisPc_Hint" : connected ? "Dual_Disconnect" : "Dual_Connect"));
        cell.Click += OnDeviceClick;
        return cell;
    }

    private async void OnDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SourceDevice device }) return;
        string name = string.IsNullOrWhiteSpace(device.Name) ? device.AddressText : device.Name;
        if (device.IsThisDevice)
        {
            var hint = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("Home_DeviceThisPc_Title"),
                Content = new TextBlock { Text = Loc.Get("Home_DeviceThisPc_Message"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("Home_OpenBluetoothSettings"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await hint.ShowAsync() == ContentDialogResult.Primary) await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));
            return;
        }
        if (device.IsConnected)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Format("Home_DeviceDisconnect_Title", name),
                Content = new TextBlock { Text = Loc.Get("Home_DeviceDisconnect_Message"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("Dual_Disconnect"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            _devicesSignature = string.Empty;
            await Features.MultiConnectAsync(MultiConnectOperation.Disconnect, device);
            Sync();
            return;
        }
        // Official: with two devices already connected, connecting a third drops one of them.
        if (Features.SourceDevices.Count(d => d.IsConnected) >= 2)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("Dual_ConnectNew_Title"),
                Content = new TextBlock { Text = Loc.Format("Dual_ConnectNew_Message", name), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("Dual_Connect"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        _devicesSignature = string.Empty;
        await Features.MultiConnectAsync(MultiConnectOperation.Connect, device);
        Sync();
    }

    // ---- device switcher ---------------------------------------------------------------------------

    // EncoDeck controls one headset at a time; this switches which one (same as Devices › Use this device).
    private void OnKnownDevicesChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(SyncSwitcher);

    private void SyncSwitcher()
    {
        bool several = DeviceSwitcherMenu.EntryCount > 1;
        DeviceSwitcher.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        DeviceNameText.Visibility = several ? Visibility.Collapsed : Visibility.Visible;

        // Experimental cup devices, only while one is connected: picked in the switcher, or the only device.
        var cups = App.Instance.Host.Cups;
        bool cup = cups is { IsEnabled: true, Session: not null } && (cups.IsShownOnHome || !ViewModel.HasDevice);
        CupView.Visibility = cup ? Visibility.Visible : Visibility.Collapsed;
        PageRoot.Visibility = cup ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceViewModel.HasDevice)) SyncSwitcher();
    }

    /// <summary>Built on every open, so the list and the active mark are always current.</summary>
    private void OnDeviceMenuOpening(object? sender, object e) => DeviceSwitcherMenu.Populate(DeviceMenu, cupShown: false);

    // ---- sound -----------------------------------------------------------------------------------

    private async void OnGameToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool requested = GameSwitch.IsOn;
        Sync(); // the device decides; snap back until it confirms
        await Features.SetGameModeAsync(requested);
        Sync();
    }

    private async void OnDualToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool requested = DualSwitch.IsOn;
        Sync();
        await Features.SetToggleAsync(DeviceToggle.DualDeviceConnection, requested);
        Sync();
    }

    // ---- layout ----------------------------------------------------------------------------------

    /// <summary>Landscape: the device stage on the left and its controls on the right; narrow: stacked.</summary>
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateLayoutForWidth(e.NewSize.Width);

    private void OnMainScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height != e.PreviousSize.Height) UpdateLayoutForWidth(PageRoot.ActualWidth);
    }

    private void UpdateLayoutForWidth(double width)
    {
        bool controls = NoiseCard.Visibility == Visibility.Visible || DevicesCard.Visibility == Visibility.Visible || SoundCard.Visibility == Visibility.Visible;
        bool wide = width >= 820 && controls;

        // Wide: SidePanel scrolls on its own beside the fixed stage; narrow: it follows the stage in MainScroll.
        if (wide && SidePanel.Parent != SideHost)
        {
            MainColumn.Children.Remove(SidePanel);
            SideHost.Child = SidePanel;
        }
        else if (!wide && SidePanel.Parent == SideHost)
        {
            SideHost.Child = null;
            MainColumn.Children.Add(SidePanel);
        }
        SideScroll.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(MainScroll, wide ? 1 : 2);
        MainScroll.VerticalScrollMode = wide ? ScrollMode.Disabled : ScrollMode.Enabled;
        MainScroll.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;

        // Keep the artwork within the fixed column: leave room for the name, levels and captions below it.
        double available = MainScroll.ActualHeight > 0 ? MainScroll.ActualHeight : 600;
        Model.Height = wide ? Math.Clamp(available - 220, 200, 400) : 280;
    }

    // ---- navigation ------------------------------------------------------------------------------

    private async void OnOpenBluetoothSettings(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));

    private void OnOpenDiagnostics(object sender, RoutedEventArgs e) => App.Instance.Windows.ShowMain("Devices");

    private void OnOpenSound(object sender, RoutedEventArgs e) => App.Instance.Windows.ShowMain("Sound");

    private void OnOpenControls(object sender, RoutedEventArgs e) => App.Instance.Windows.ShowMain("Controls");
}
