// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Security.Principal;
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Bluetooth.Link;
using Windows.Devices.Bluetooth;

namespace EncoDeck.Probe;

/// <summary>
/// Experiment for a real connect/disconnect of the PC's link: sends KSPROPERTY_ONESHOT_DISCONNECT/_RECONNECT to the
/// Bluetooth audio drivers behind the device's endpoints (as Windows' Bluetooth audio tools do), or forces the
/// Classic link down with IOCTL_BTH_DISCONNECT_DEVICE, then logs the Classic link, both LE links and the endpoints.
/// Opens no control channel of its own; exit the app first, its control channel keeps the link up.
/// </summary>
internal static class LinkCommand
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        string action = options.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        if (action is not ("status" or "disconnect" or "connect"))
        {
            Console.Error.WriteLine("Refused: link takes status, disconnect or connect.");
            return 2;
        }
        bool ioctl = options.Has("ioctl");
        if (ioctl && action != "disconnect")
        {
            Console.Error.WriteLine("Refused: --ioctl only applies to disconnect.");
            return 2;
        }
        if (action != "status" && !options.Has("confirm-write"))
        {
            Console.Error.WriteLine("Refused: connect/disconnect changes the PC's Bluetooth link. Re-run with --confirm-write.");
            return 2;
        }

        var device = await DeviceSelector.SelectAsync(new BluetoothDeviceCatalog(), options, ct);
        if (device is null) return 3;
        ulong address = device.Address.Value;
        var clock = Stopwatch.StartNew();
        var gate = new object();
        void Log(string message)
        {
            lock (gate) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} +{clock.Elapsed.TotalSeconds,6:0.0}s {message}");
        }

        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Log($"Target {device.Name}; process elevated={elevated}");

        using var classic = await BluetoothDevice.FromIdAsync(device.DeviceId).AsTask(ct);
        using var primary = await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public).AsTask(ct);
        var secondaries = await LeAudioPairing.FindSecondaryBondsAsync(address, primary?.Name ?? device.Name, ct);
        try
        {
            var links = new LeDualCommand.LinkTimeline(Log);
            if (classic is not null) links.Watch("BR", () => classic.ConnectionStatus, h => classic.ConnectionStatusChanged += (_, _) => h());
            if (primary is not null) links.Watch("LE-primary", () => primary.ConnectionStatus, h => primary.ConnectionStatusChanged += (_, _) => h());
            foreach (var secondary in secondaries)
                links.Watch("LE-secondary", () => secondary.ConnectionStatus, h => secondary.ConnectionStatusChanged += (_, _) => h());

            var endpoints = await GetEndpointsAsync(address, ct);
            Log($"endpoints: {Describe(endpoints)}");
            var ids = endpoints.Select(e => WindowsAudioEndpointPlatform.ToMmDeviceId(e.InstanceId)).ToList();
            foreach (var filter in BluetoothAudioLink.FindFilters(ids))
                Log($"  {Kind(endpoints, filter.EndpointId)} -> filter {filter.FilterId ?? "(none)"} 0x{filter.HResult:X8}");
            if (action == "status") return 0;

            if (ioctl)
            {
                int error = BluetoothAudioLink.DisconnectLink(address);
                Log($"IOCTL_BTH_DISCONNECT_DEVICE: {error}");
            }
            else
            {
                var request = action == "connect" ? BluetoothAudioLinkRequest.Connect : BluetoothAudioLinkRequest.Disconnect;
                var results = BluetoothAudioLink.Send(ids, request);
                if (results.Count == 0) Log("No audio driver filter found for this device");
                foreach (var result in results)
                    Log($"{request} {Kind(endpoints, result.EndpointId)}: 0x{result.HResult:X8}");
            }

            string last = Describe(endpoints);
            var until = TimeSpan.FromSeconds(Math.Clamp(options.GetInt("seconds", 20), 3, 600));
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < until)
            {
                await Task.Delay(500, ct);
                string now = Describe(await GetEndpointsAsync(address, ct));
                if (now != last) Log($"endpoints: {now}");
                last = now;
            }
            links.PrintSummary();
            return 0;
        }
        finally
        {
            foreach (var secondary in secondaries) secondary.Dispose();
        }
    }

    private static async Task<List<AudioEndpointInfo>> GetEndpointsAsync(ulong address, CancellationToken ct)
    {
        var platform = new WindowsAudioEndpointPlatform();
        var render = await platform.GetRenderEndpointsAsync(ct);
        var capture = await platform.GetCaptureEndpointsAsync(ct);
        return render.Concat(capture).Where(e => e.BelongsTo(address)).OrderBy(e => e.Kind).ToList();
    }

    private static string Describe(IEnumerable<AudioEndpointInfo> endpoints)
    {
        var text = endpoints.Select(e => $"{e.Kind}={(e.IsDisabled ? "disabled" : !e.IsStateKnown ? "unknown" : e.IsActive ? "active" : "inactive")}").ToList();
        return text.Count == 0 ? "none" : string.Join(", ", text);
    }

    private static string Kind(List<AudioEndpointInfo> endpoints, string endpointId) =>
        endpoints.FirstOrDefault(e => WindowsAudioEndpointPlatform.ToMmDeviceId(e.InstanceId).Equals(endpointId, StringComparison.OrdinalIgnoreCase))?.Kind.ToString()
        ?? endpointId;
}
