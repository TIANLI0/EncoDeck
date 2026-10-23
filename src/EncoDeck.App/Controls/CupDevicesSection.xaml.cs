// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.Bluetooth.Cups;
using EncoDeck.Core.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// The cup part of Devices › paired devices: the connected cup (if any) and a BLE scan for nearby
/// ones. Cups need no Windows pairing; every connection is confirmed by the user first.
/// </summary>
public sealed partial class CupDevicesSection : UserControl
{
    private readonly Dictionary<(string Brand, ulong Address), ICupProfile> _modelChoice = [];
    private bool _dialogOpen;

    public CupDevicesSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Cups.Changed += OnCupsChanged;
            Cups.DevicesChanged += OnCupsChanged;
            Build();
        };
        Unloaded += (_, _) =>
        {
            Cups.Changed -= OnCupsChanged;
            Cups.DevicesChanged -= OnCupsChanged;
        };
    }

    private static CupService Cups => App.Instance.Host.Cups;

    private static Style Secondary => (Style)Application.Current.Resources["SecondaryTextStyle"];

    private static string BrandName(string brandId) => Cups.BrandOf(brandId)?.DisplayName ?? brandId;

    private void OnCupsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Build);

    private void Build()
    {
        Visibility = Cups.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        SessionHost.Children.Clear();
        DeviceHost.Children.Clear();
        if (!Cups.IsEnabled) return;

        var session = Cups.Session;
        if (session is not null) SessionHost.Children.Add(CreateSessionRow(session));
        NoSessionText.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;
        NoSessionText.Text = Cups.LastSessionError is { } ended
            ? Loc.Format("Cup_SessionEnded", ended.Length == 0 ? Loc.Get("Cup_State_Unavailable") : ended)
            : Loc.Get("Cup_NoSession");

        ScanButton.Content = Loc.Get(Cups.IsScanning ? "Cup_StopScan" : "Cup_Scan");
        ScanRing.IsActive = Cups.IsScanning;
        var devices = Cups.Devices;
        ScanStatus.Text = Cups.ScanError is { } error ? Loc.Format("Cup_ScanFailed", error)
            : Cups.IsScanning ? Loc.Get("Cup_Scanning")
            : devices.Count == 0 ? Loc.Get("Cup_ScanHint")
            : string.Empty;
        var target = session?.Target;
        foreach (var device in devices)
            DeviceHost.Children.Add(CreateDeviceRow(device, target is not null && target.Brand == device.Brand && target.Address == device.Address));
    }

    private static Grid RowGrid(ICupProfile? model, out StackPanel text)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = CupArtwork.CreateImage(model, 28);
        icon.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        text = new StackPanel();
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        return grid;
    }

    private UIElement CreateSessionRow(ICupSession session)
    {
        var grid = RowGrid(session.Profile, out var text);
        text.Children.Add(new TextBlock { Text = session.Identity.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = $"{BrandName(session.Profile.Brand)} · {session.Profile.DisplayName} · {CupDeviceView.DescribeState(session)} · {session.Identity.Address.ToRedactedString()}",
            Style = Secondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (Cups.IsShownOnHome)
        {
            actions.Children.Add(new TextBlock { Text = Loc.Get("Devices_Active"), VerticalAlignment = VerticalAlignment.Center, Style = Secondary });
        }
        else
        {
            var use = new Button { Content = Loc.Get("Devices_UseThis") };
            use.Click += (_, _) => Cups.ShowOnHome(true);
            actions.Children.Add(use);
        }
        var stop = new Button { Content = Loc.Get("Cup_StopAll/Content") };
        stop.Click += async (_, _) =>
        {
            var result = await Cups.EmergencyStopAsync();
            ScanStatus.Text = result.IsSuccess ? Loc.Get("Cup_Stopped") : Loc.Format("Cup_StopFailed", result.Detail ?? result.Status.ToString());
        };
        var disconnect = new Button { Content = Loc.Get("Cup_Disconnect/Content") };
        disconnect.Click += async (_, _) => await Cups.DisconnectAsync();
        actions.Children.Add(stop);
        actions.Children.Add(disconnect);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return grid;
    }

    private UIElement CreateDeviceRow(CupAdvertisement device, bool inSession)
    {
        var grid = RowGrid(device.Model, out var text);
        text.Children.Add(new TextBlock { Text = device.Name ?? Loc.Get("Cup_Unnamed"), TextTrimming = TextTrimming.CharacterEllipsis });
        string model = device.Model?.DisplayName ?? Loc.Get("Cup_UnknownModel");
        string address = device.Address.ToRedactedString() + (device.IsRandomAddress ? " " + Loc.Get("Cup_RandomAddress") : string.Empty);
        text.Children.Add(new TextBlock
        {
            Text = $"{BrandName(device.Brand)} · {model} · {address} · {device.Rssi} dBm",
            Style = Secondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        FrameworkElement action;
        if (inSession)
        {
            action = new TextBlock { Text = Loc.Get("Cup_InSession"), VerticalAlignment = VerticalAlignment.Center, Style = Secondary };
        }
        else
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var connect = new Button { Content = Loc.Get("Cup_Connect") };
            var key = (device.Brand, device.Address.Value);
            if (device.Model is null && Cups.BrandOf(device.Brand) is { } brand)
            {
                // The name does not identify the model; a wrong pick sends the wrong command set.
                var picker = new ComboBox { PlaceholderText = Loc.Get("Cup_ChooseModel"), MinWidth = 140 };
                AutomationProperties.SetName(picker, Loc.Get("Cup_ChooseModel"));
                foreach (var option in brand.Models) picker.Items.Add(new ComboBoxItem { Content = option.DisplayName, Tag = option });
                if (_modelChoice.TryGetValue(key, out var chosen))
                    picker.SelectedIndex = brand.Models.ToList().FindIndex(m => m.ModelId == chosen.ModelId);
                connect.IsEnabled = picker.SelectedIndex >= 0;
                picker.SelectionChanged += (_, _) =>
                {
                    if (picker.SelectedItem is ComboBoxItem { Tag: ICupProfile m }) _modelChoice[key] = m;
                    connect.IsEnabled = picker.SelectedIndex >= 0;
                };
                panel.Children.Add(picker);
            }
            connect.Click += async (_, _) => await ConfirmAndConnectAsync(device);
            panel.Children.Add(connect);
            action = panel;
        }
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);
        return grid;
    }

    private void OnScan(object sender, RoutedEventArgs e)
    {
        if (Cups.IsScanning) Cups.StopScan();
        else Cups.StartScan();
    }

    private async Task ConfirmAndConnectAsync(CupAdvertisement device)
    {
        bool manual = device.Model is null;
        ICupProfile? model = device.Model ?? (_modelChoice.TryGetValue((device.Brand, device.Address.Value), out var m) ? m : null);
        if (model is null) return;
        string message = Loc.Format("Cup_Confirm_Message", device.Name ?? Loc.Get("Cup_Unnamed"), $"{BrandName(device.Brand)} {model.DisplayName}");
        if (manual) message += "\n\n" + Loc.Get("Cup_Confirm_Manual");
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Cup_Confirm_Title"),
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get("Cup_Confirm_Accept"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            await Cups.ConnectAsync(device, model);
        }
        catch (Exception ex)
        {
            ScanStatus.Text = Loc.Format("Cup_CommandFailed", ex.Message);
        }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen || XamlRoot is null) return ContentDialogResult.None;
        _dialogOpen = true;
        try
        {
            dialog.XamlRoot = XamlRoot;
            return await dialog.ShowAsync();
        }
        catch (Exception)
        {
            return ContentDialogResult.None;
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
