// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Bluetooth;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
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
            BuildInfo();
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
        if (_syncingSwitch || !HasExperimentalSwitch(ViewModel.Profile)) return;
        bool on = MoondropExperimentalSwitch.IsOn;
        App.Instance.Host.Settings.Update(s => s with { EnableExperimentalFeatures = on });
    }

    /// <summary>Brands whose unverified commands are gated by the shared experimental preference.</summary>
    private static bool HasExperimentalSwitch(EncoDeck.Core.Profiles.IDeviceProfile? profile) => FeaturesViewModel.OffersExperimental(profile);

    private void OnDevicesChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Build);

    private void OnSettingsChanged(object? sender, EncoDeck.Core.Configuration.AppSettings settings) => DispatcherQueue.TryEnqueue(BuildCapabilities);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceViewModel.ProfileKey)) BuildCapabilities();
        if (e.PropertyName is nameof(DeviceViewModel.FirmwareVersionText) or nameof(DeviceViewModel.ProductIdText) or nameof(DeviceViewModel.CanFactoryReset)) BuildInfo();
    }

    private void BuildInfo()
    {
        FirmwareRow.Visibility = ViewModel.FirmwareVersionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProductIdRow.Visibility = ViewModel.ProductIdText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetRow.Visibility = ViewModel.CanFactoryReset ? Visibility.Visible : Visibility.Collapsed;
        InfoCard.Visibility = FirmwareRow.Visibility == Visibility.Visible || ProductIdRow.Visibility == Visibility.Visible
            || ResetRow.Visibility == Visibility.Visible || ResetResult.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _resetting;

    private async void OnFactoryResetClick(object sender, RoutedEventArgs e)
    {
        if (_resetting || XamlRoot is null) return;
        _resetting = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("Devices_FactoryReset_Title"),
                Content = new TextBlock { Text = Loc.Format("Devices_FactoryReset_Message", ViewModel.DeviceName), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("Devices_FactoryReset_Confirm"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await App.Instance.Features.FactoryResetAsync();
            ResetResult.Text = result.IsSuccess ? Loc.Get("Devices_FactoryReset_Done") : ViewModel.ErrorMessage;
            ResetResult.Visibility = Visibility.Visible;
            BuildInfo();
        }
        catch (Exception)
        {
            // A dialog that cannot open (another one is showing) leaves the earbuds untouched.
        }
        finally
        {
            _resetting = false;
        }
    }

    private void BuildCapabilities()
    {
        bool optIn = HasExperimentalSwitch(ViewModel.Profile);
        bool experimental = optIn && App.Instance.Host.Settings.Current.EnableExperimentalFeatures;
        MoondropCard.Visibility = optIn ? Visibility.Visible : Visibility.Collapsed;
        string key = ViewModel.Profile switch
        {
            EncoDeck.Protocol.Brands.Akg.AkgHybridProfile => "Settings_AkgExperimental",
            EncoDeck.Protocol.Brands.Moondrop.MoondropProfile => "Settings_MoondropExperimental",
            _ => "Settings_BrandExperimental", // vivo, EDIFIER
        };
        ExperimentalRow.Header = Loc.Get(key + "/Header");
        ExperimentalRow.Description = Loc.Get(key + "/Description");
        if (MoondropExperimentalSwitch.IsOn != experimental && optIn)
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
                DeviceCapability.TouchCustomization or DeviceCapability.Equalizer or DeviceCapability.CustomEqualizer)) continue;
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
        string status = device.IsDisconnectedByUser ? Loc.Get("Connection_DisconnectedByUser")
            : device.IsConnected ? Loc.Get("Devices_StatusConnected") : Loc.Get("Devices_StatusNotConnected");
        string model = device.Profile.DisplayName;
        text.Children.Add(new TextBlock
        {
            // USB receivers carry no Bluetooth address; a redacted zero address would look real.
            Text = $"{model} · {status} · {(device.Identity.Transport == EncoDeck.Core.Devices.DeviceTransport.Usb ? "USB" : device.Identity.Address.ToRedactedString())}",
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
        });
        Grid.SetColumn(text, 1);

        var action = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        bool bluetooth = device.Identity.Transport == EncoDeck.Core.Devices.DeviceTransport.Bluetooth;
        if (bluetooth && (device.IsDisconnectedByUser || !device.IsConnected))
        {
            // Pages the earbuds and has Windows' audio drivers connect; the supervisor attaches control once the link is up.
            action.Children.Add(ConnectionButton(Loc.Get("DeviceConnect/Content"), devices =>
            {
                App.Instance.Host.Settings.Update(s => s with { PreferredDeviceId = device.Identity.DeviceId });
                return devices.ConnectDeviceAsync(device.Identity.DeviceId);
            }));
        }
        else if (!device.IsConnected)
        {
            var button = new Button { Content = Loc.Get("Devices_OpenBluetooth/Content") };
            button.Click += OnOpenBluetoothSettings;
            action.Children.Add(button);
        }
        else
        {
            if (device.IsActive)
            {
                action.Children.Add(new TextBlock { Text = Loc.Get("Devices_Active"), VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
            }
            else
            {
                var button = new Button { Content = Loc.Get("Devices_UseThis") };
                button.Click += async (_, _) =>
                {
                    App.Instance.Host.Settings.Update(s => s with { PreferredDeviceId = device.Identity.DeviceId });
                    await App.Instance.Host.Devices.SelectDeviceAsync(device.Identity.DeviceId);
                };
                action.Children.Add(button);
            }
            if (bluetooth)
                action.Children.Add(ConnectionButton(Loc.Get("DeviceDisconnect/Content"), devices => devices.DisconnectDeviceAsync(device.Identity.DeviceId)));
        }
        Grid.SetColumn(action, 2);

        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(action);
        return grid;
    }

    /// <summary>Connect or Disconnect for one row; disabled while it runs, and the list rebuilds when the link changes.</summary>
    private static Button ConnectionButton(string label, Func<IDeviceManager, Task<CommandResult>> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            await App.Instance.Device.RunCommandAsync(() => action(App.Instance.Host.Devices));
            button.IsEnabled = true;
        };
        return button;
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
