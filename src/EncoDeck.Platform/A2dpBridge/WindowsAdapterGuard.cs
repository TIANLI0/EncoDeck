// SPDX-License-Identifier: GPL-3.0-only
using Windows.Devices.Bluetooth;

namespace EncoDeck.Platform.A2dpBridge;

/// <summary>
/// Keeps EncoDeck from handing Windows' own Bluetooth adapter to WinUSB: everything paired with this PC (and
/// EncoDeck's control of the earbuds) runs on it. A second, idle adapter is what BlueCaster is meant for.
/// </summary>
public static class WindowsAdapterGuard
{
    /// <summary>The device instance id ("USB\VID_8087&amp;PID_0033\…") of the adapter Windows' Bluetooth uses, or null when there is none.</summary>
    public static async Task<string?> DefaultAdapterInstanceIdAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            return adapter is null ? null : InstanceIdOf(adapter.DeviceId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
    }

    /// <summary>A device interface path (<c>\\?\USB#VID_…#…#{guid}</c>) as its device instance id (<c>USB\VID_…\…</c>).</summary>
    internal static string? InstanceIdOf(string? interfacePath)
    {
        if (string.IsNullOrEmpty(interfacePath)) return null;
        string path = interfacePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? interfacePath[4..] : interfacePath;
        int guid = path.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0) path = path[..guid];
        return path.Replace('#', '\\');
    }

    public static bool IsSameDevice(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
