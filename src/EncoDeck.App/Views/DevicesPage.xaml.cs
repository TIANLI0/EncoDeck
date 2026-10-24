// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Bluetooth;
using EncoDeck.Core.Capabilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Views;

public sealed partial class DevicesPage : Page
{
    public DevicesPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            App.Instance.Host.Devices.KnownDevicesChanged += OnDevicesChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            App.Instance.Host.Settings.Changed += OnSettingsChanged;
            Build();
            BuildCapabilities();
        };
        Unloaded += (_, _) =>
        {
            App.Instance.Host.Devices.KnownDevicesChanged -= OnDevicesChanged;
            ViewModel.PropertyChanged -= OnViewModelChanged;
            App.Instance.Host.Settings.Changed -= OnSettingsChanged;
        };
    }

    public DeviceViewModel ViewModel => App.Instance.Device;

    private bool _syncingSwitch;

    private void OnMoondropExperimentalToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingSwitch || ViewModel.Profile is not EncoDeck.Protocol.Brands.Moondrop.MoondropProfile) return;
        bool on = MoondropExperimentalSwitch.IsOn;
        App.Instance.Host.Settings.Update(s => s with { EnableExperimentalFeatures = on });
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Build);

    private void OnSettingsChanged(object? sender, EncoDeck.Core.Configuration.AppSettings settings) => DispatcherQueue.TryEnqueue(BuildCapabilities);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceViewModel.ProfileKey)) BuildCapabilities();
    }

    private void BuildCapabilities()
    {
        bool moondrop = ViewModel.Profile is EncoDeck.Protocol.Brands.Moondrop.MoondropProfile;
        bool experimental = moondrop && App.Instance.Host.Settings.Current.EnableExperimentalFeatures;
        MoondropCard.Visibility = moondrop ? Visibility.Visible : Visibility.Collapsed;
        if (MoondropExperimentalSwitch.IsOn != experimental && moondrop)
        {
            _syncingSwitch = true;
            try { MoondropExperimentalSwitch.IsOn = experimental; }
            finally { _syncingSwitch = false; }
        }
        CapabilityHost.Children.Clear();
        CapabilityCard.Visibility = ViewModel.Profile is null ? Visibility.Collapsed : Visibility.Visible;
        if (ViewModel.Profile is not { } profile) return;
        foreach (var (capability, entry) in profile.Capabilities.All)
        {
            if (entry.Support == CapabilitySupport.Unknown) continue;
            if (profile is EncoDeck.Protocol.Brands.Moondrop.MoondropProfile && capability is not (
                DeviceCapability.BatteryLevel or DeviceCapability.NoiseCancellation or
                DeviceCapability.AdaptiveNoiseCancellation or DeviceCapability.NoiseCancellationLevel or
                DeviceCapability.PromptVolume or DeviceCapability.DualDeviceConnection or
                DeviceCapability.SpatialAudio or DeviceCapability.HeadTracking or DeviceCapability.InEarDetection or
                DeviceCapability.BassEngine or DeviceCapability.ChannelSwap or DeviceCapability.HiResCodec or
                DeviceCapability.Ldac or DeviceCapability.Lc3 or
                DeviceCapability.Gain or DeviceCapability.IndicatorLight or DeviceCapability.PromptTone or
                DeviceCapability.TouchCustomization)) continue;
            // Experimental features read as supported once opted in, never as unsupported before.
            string support = entry.Support switch
            {
                CapabilitySupport.Supported => "Support_Supported",
                CapabilitySupport.Experimental => experimental ? "Support_Supported" : "Support_Experimental",
                _ => "Support_Unsupported",
            };
            var row = new Controls.SettingRow
            {
                Header = Loc.Get($"Cap_{capability}"),
                Action = new TextBlock
                {
                    Text = Loc.Get(support),
                    Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            if (!string.IsNullOrWhiteSpace(entry.Evidence)) ToolTipService.SetToolTip(row, entry.Evidence);
            CapabilityHost.Children.Add(row);
        }
        CapabilityCard.Visibility = CapabilityHost.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Build()
    {
        DeviceHost.Children.Clear();
        var devices = App.Instance.Host.Devices.KnownDevices;
        NoDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var device in devices)
        {
            DeviceHost.Children.Add(CreateRow(device));
        }
    }

    private UIElement CreateRow(KnownDevice device)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon { Glyph = "", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = device.Identity.Name });
        string status = device.IsConnected ? Loc.Get("Devices_StatusConnected") : Loc.Get("Devices_StatusNotConnected");
        string model = device.Profile.DisplayName;
        text.Children.Add(new TextBlock
        {
            // USB receivers carry no Bluetooth address; a redacted zero address would look real.
            Text = $"{model} · {status} · {(device.Identity.Transport == EncoDeck.Core.Devices.DeviceTransport.Usb ? "USB" : device.Identity.Address.ToRedactedString())}",
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
        });
        Grid.SetColumn(text, 1);

        UIElement action;
        if (!device.IsConnected)
        {
            // Windows connects audio and the link together; the supervisor attaches control once the link is up.
            var button = new Button { Content = Loc.Get("DeviceConnect/Content"), VerticalAlignment = VerticalAlignment.Center };
            button.Click += OnOpenBluetoothSettings;
            action = button;
        }
        else if (device.IsActive)
        {
            action = new TextBlock { Text = Loc.Get("Devices_Active"), VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] };
        }
        else
        {
            var button = new Button { Content = Loc.Get("Devices_UseThis"), VerticalAlignment = VerticalAlignment.Center };
            button.Click += async (_, _) =>
            {
                App.Instance.Host.Settings.Update(s => s with { PreferredDeviceId = device.Identity.DeviceId });
                await App.Instance.Host.Devices.SelectDeviceAsync(device.Identity.DeviceId);
            };
            action = button;
        }
        Grid.SetColumn((FrameworkElement)action, 2);

        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(action);
        return grid;
    }

    private async void OnOpenBluetoothSettings(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));

    private async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = await DiagnosticsExporter.ExportAsync(App.Instance.Host);
            ExportResult.Text = Loc.Format("Devices_ExportDone", path);
            DiagnosticsExporter.RevealInExplorer(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportResult.Text = Loc.Format("Devices_ExportFailed", ex.Message);
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(App.Instance.Host.Paths.LogDirectory) { UseShellExecute = true });
}
