// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Core.Privacy;

namespace EncoDeck.Probe;

internal static class DeviceSelector
{
    private static readonly string[] DefaultNameHints = ["Enco", "OPPO", "OnePlus Buds"];

    public static async Task<PairedDeviceInfo?> SelectAsync(BluetoothDeviceCatalog catalog, ProbeOptions options, CancellationToken ct)
    {
        var devices = await catalog.GetPairedClassicDevicesAsync(ct);
        string? query = options.Get("device");
        PairedDeviceInfo? match = query is not null
            ? devices.FirstOrDefault(d => d.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            : devices
                .Where(d => DefaultNameHints.Any(h => d.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                // Prefer audio devices (CoD major class 0x04) over phones that share the OPPO name.
                .OrderByDescending(d => ((d.ClassOfDeviceRaw >> 8) & 0x1F) == 0x04)
                .ThenByDescending(d => d.IsConnected)
                .FirstOrDefault();
        // Earbuds kept on LE Audio alone have no Classic pairing left; use the identity EncoDeck remembered.
        match ??= LeAudioOnlyDevices.All
            .Where(d => query is null
                ? DefaultNameHints.Any(h => d.Name.Contains(h, StringComparison.OrdinalIgnoreCase))
                : d.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(d => new PairedDeviceInfo(d.DeviceId, d.Name, new BluetoothAddress(d.Address), false, 0))
            .FirstOrDefault();

        if (match is null)
        {
            Console.Error.WriteLine(query is null
                ? "No paired OPPO/Enco device found. Use --device <name> (see 'devices')."
                : $"No paired device name contains '{query}'.");
        }
        return match;
    }
}
