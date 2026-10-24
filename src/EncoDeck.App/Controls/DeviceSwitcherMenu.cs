// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// The Home device switcher: every supported headset, then the cup device entry when that
/// experimental category is on. EncoDeck shows and controls one device at a time.
/// </summary>
internal static class DeviceSwitcherMenu
{
    /// <summary>The switcher is only worth showing with more than one entry.</summary>
    public static int EntryCount => App.Instance.Host.Devices.KnownDevices.Count + (CupSession is null ? 0 : 1);

    /// <summary>A connected cup is listed only while its session exists; it leaves the list on disconnect.</summary>
    private static EncoDeck.Bluetooth.Cups.ICupSession? CupSession => App.Instance.Host.Cups is { IsEnabled: true, Session: { } s } ? s : null;

    /// <summary>Rebuilds <paramref name="menu"/>; call when it opens so the marks are current.</summary>
    public static void Populate(MenuFlyout menu, bool cupShown)
    {
        var host = App.Instance.Host;
        menu.Items.Clear();
        foreach (var device in host.Devices.KnownDevices)
        {
            string deviceId = device.Identity.DeviceId;
            var item = new RadioMenuFlyoutItem
            {
                Text = device.IsConnected ? device.Identity.Name : Loc.Format("Home_SwitchDevice_NotConnected", device.Identity.Name),
                GroupName = "ActiveDevice",
                IsChecked = device.IsActive && !cupShown,
            };
            item.Click += async (_, _) => await SelectHeadsetAsync(deviceId);
            menu.Items.Add(item);
        }
        if (CupSession is { } session)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = Loc.Format("Home_SwitchDevice_Cup", session.Identity.Name),
                GroupName = "ActiveDevice",
                IsChecked = cupShown,
            };
            item.Click += (_, _) => host.Cups.ShowOnHome(true);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = Loc.Get("Home_ManageDevices"), Icon = new FontIcon { Glyph = "\uE713" } };
        manage.Click += (_, _) => App.Instance.Windows.ShowMain("Devices");
        menu.Items.Add(manage);
    }

    /// <summary>Same as Devices › Use this device. Leaving the cup view stops its output (it stays connected).</summary>
    public static async Task SelectHeadsetAsync(string deviceId)
    {
        var host = App.Instance.Host;
        if (host.Cups.IsShownOnHome)
        {
            host.Cups.ShowOnHome(false);
            await host.Cups.EmergencyStopAsync();
        }
        if (host.Devices.KnownDevices.Any(d => d.IsActive && d.Identity.DeviceId == deviceId)) return;
        host.Settings.Update(s => s with { PreferredDeviceId = deviceId });
        await host.Devices.SelectDeviceAsync(deviceId);
    }
}
