// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using EncoDeck.App.ViewModels;
using EncoDeck.Core.State;
using EncoDeck.Platform.Settings;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Services;

/// <summary>
/// Notification-area icon: left click toggles the quick panel, right click opens a native menu.
/// The menu is a native Win32 popup (H.NotifyIcon PopupMenu mode), so it follows the system theme
/// and never steals focus from the foreground app until the user interacts with it.
/// </summary>
public sealed partial class TrayIconService : IDisposable
{
    private readonly DeviceViewModel _device;
    private readonly ISettingsStore _settings;
    private readonly WindowManager _windows;
    private readonly Action _exit;
    private readonly CupService? _cups;
    private TaskbarIcon? _icon;
    private MenuFlyoutItem? _cupStopItem;
    private MenuFlyoutItem? _statusItem;
    private ToggleMenuFlyoutItem? _popupItem;
    private ToggleMenuFlyoutItem? _offItem;
    private ToggleMenuFlyoutItem? _ancItem;
    private ToggleMenuFlyoutItem? _adaptiveItem;
    private ToggleMenuFlyoutItem? _transparencyItem;
    private string? _lastToolTip;

    public TrayIconService(DeviceViewModel device, ISettingsStore settings, WindowManager windows, Action exit, CupService? cups = null)
    {
        _device = device;
        _settings = settings;
        _windows = windows;
        _exit = exit;
        _cups = cups;
    }

    public Guid Id => _icon?.Id ?? Guid.Empty;

    public void Create()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.Get("Tray_Open"), Command = new RelayCommand(() => _windows.ShowMain()) });
        menu.Items.Add(new MenuFlyoutSeparator());
        _statusItem = new MenuFlyoutItem { Text = _device.DeviceName, IsEnabled = false };
        menu.Items.Add(_statusItem);

        var noise = new MenuFlyoutSubItem { Text = Loc.Get("Tray_NoiseControl") };
        _ancItem = NoiseItem("NoiseMode_NoiseCancellation", "NoiseCancellation");
        _adaptiveItem = NoiseItem("NoiseMode_Adaptive", "Adaptive");
        _transparencyItem = NoiseItem("NoiseMode_Transparency", "Transparency");
        _offItem = NoiseItem("NoiseMode_Off", "Off");
        noise.Items.Add(_ancItem);
        noise.Items.Add(_adaptiveItem);
        noise.Items.Add(_transparencyItem);
        noise.Items.Add(_offItem);
        menu.Items.Add(noise);

        _popupItem = new ToggleMenuFlyoutItem
        {
            Text = Loc.Get("Tray_ConnectionPopup"),
            IsChecked = _settings.Current.ShowConnectionPopup,
            Command = new RelayCommand(() => _settings.Update(s => s with { ShowConnectionPopup = !s.ShowConnectionPopup })),
        };
        menu.Items.Add(_popupItem);
        if (_cups is { } cups)
        {
            // Always reachable while a cup session exists, even with the main window closed.
            _cupStopItem = new MenuFlyoutItem
            {
                Text = Loc.Get("Tray_CupStop"),
                Command = new RelayCommand(() => _ = cups.EmergencyStopAsync()),
                Visibility = cups.Session is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible,
            };
            menu.Items.Add(_cupStopItem);
        }
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.Get("Tray_Settings"), Command = new RelayCommand(() => _windows.ShowMain("Settings")) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.Get("Tray_Exit"), Command = new RelayCommand(_exit) });

        _icon = new TaskbarIcon
        {
            ToolTipText = "EncoDeck",
            Icon = LoadIcon(),
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(() => _windows.ToggleTrayPanel(Id)),
            NoLeftClickDelay = true,
        };
        // Default ForceCreate() enables Efficiency Mode for the whole process (EcoQoS + idle priority).
        _icon.ForceCreate(enablesEfficiencyMode: false);

        _device.PropertyChanged += OnDeviceChanged;
        if (_cups is not null) _cups.Changed += OnCupsChanged;
        _settings.Changed += (_, s) => _icon?.DispatcherQueue.TryEnqueue(() => { if (_popupItem is not null) _popupItem.IsChecked = s.ShowConnectionPopup; });
        Refresh();
    }

    private void OnCupsChanged(object? sender, EventArgs e) => _icon?.DispatcherQueue.TryEnqueue(() =>
    {
        if (_cupStopItem is not null && _cups is not null)
            _cupStopItem.Visibility = _cups.Session is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    });

    private ToggleMenuFlyoutItem NoiseItem(string key, string mode) => new()
    {
        Text = Loc.Get(key),
        Command = new RelayCommand(() => _device.SetNoiseModeCommand.Execute(mode)),
    };

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is
            nameof(DeviceViewModel.TrayToolTip) or nameof(DeviceViewModel.HasDevice) or
            nameof(DeviceViewModel.DeviceName) or nameof(DeviceViewModel.ConnectionText) or
            nameof(DeviceViewModel.IsNoiseAvailable) or nameof(DeviceViewModel.IsNoisePending) or
            nameof(DeviceViewModel.HasAncMode) or nameof(DeviceViewModel.HasAdaptiveMode) or
            nameof(DeviceViewModel.HasTransparencyMode) or nameof(DeviceViewModel.HasOffMode) or
            nameof(DeviceViewModel.IsAncSelected) or nameof(DeviceViewModel.IsAdaptiveSelected) or
            nameof(DeviceViewModel.IsTransparencySelected) or nameof(DeviceViewModel.IsOffSelected)) Refresh();
    }

    private void Refresh()
    {
        if (_icon is null) return;
        if (_device.TrayToolTip != _lastToolTip)
        {
            _lastToolTip = _device.TrayToolTip;
            _icon.ToolTipText = _device.TrayToolTip;
        }
        if (_statusItem is not null) _statusItem.Text = _device.HasDevice ? $"{_device.DeviceName} · {_device.ConnectionText}" : Loc.Get("Device_None");

        bool available = _device.IsNoiseAvailable && !_device.IsNoisePending;
        Set(_ancItem, _device.HasAncMode, _device.IsAncSelected, available);
        Set(_adaptiveItem, _device.HasAdaptiveMode, _device.IsAdaptiveSelected, available);
        Set(_transparencyItem, _device.HasTransparencyMode, _device.IsTransparencySelected, available);
        Set(_offItem, _device.HasOffMode, _device.IsOffSelected, available);

        static void Set(ToggleMenuFlyoutItem? item, bool visible, bool isChecked, bool enabled)
        {
            if (item is null) return;
            item.Visibility = visible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            item.IsChecked = isChecked;
            item.IsEnabled = enabled;
        }
    }

    /// <summary>Balloon notification (shown as a toast on Windows 10/11).</summary>
    public void Notify(string title, string message)
    {
        try
        {
            _icon?.ShowNotification(title, message, NotificationIcon.Info, respectQuietTime: true);
        }
        catch (Exception)
        {
            // Notifications are best effort.
        }
    }

    private static System.Drawing.Icon? LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "EncoDeck.ico");
        if (!File.Exists(path)) return null;
        int size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        return new System.Drawing.Icon(path, size, size);
    }

    private const int SM_CXSMICON = 49;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    public void Dispose()
    {
        _device.PropertyChanged -= OnDeviceChanged;
        if (_cups is not null) _cups.Changed -= OnCupsChanged;
        // On WinUI the icon is not removed automatically; without this a ghost icon remains.
        _icon?.Dispose();
        _icon = null;
    }
}
