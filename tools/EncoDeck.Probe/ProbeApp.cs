// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Probe;

internal static class ProbeApp
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var options = ProbeOptions.Parse(args);
        ConsoleTrace.ShowSensitive = options.Has("show-sensitive");
        if (options.Command is null or "help" || options.Has("help"))
        {
            PrintHelp();
            return options.Command is null ? 1 : 0;
        }

        try
        {
            return options.Command switch
            {
                "devices" => await ListDevicesAsync(options, ct),
                "usb-devices" => await UsbDevicesCommand.RunAsync(ct),
                "services" => await ListServicesAsync(options, ct),
                "query" => await QueryCommands.QueryAsync(options, ct),
                "listen" => await QueryCommands.ListenAsync(options, ct),
                "watch" => await QueryCommands.WatchAsync(options, ct),
                "get" => await QueryCommands.GetAsync(options, ct),
                "set-anc" => await WriteCommands.SetNoiseControlAsync(options, ct),
                "set-eq" => await WriteCommands.SetEqualizerAsync(options, ct),
                "monitor" => await MonitorCommand.RunAsync(options, ct),
                "render-model" => await RenderModelCommand.RunAsync(options, ct),
                "dokidoki" => await DokiDokiCommand.RunAsync(options, ct),
                _ => Unknown(options.Command),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run 'EncoDeck.Probe help'.");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            EncoDeck.Probe - headless diagnostics for OPPO Bluetooth earbuds

            Usage: EncoDeck.Probe <command> [options]

            Read-only commands:
              devices                     List paired Bluetooth Classic devices.
              usb-devices                 List verified supported USB receivers and read standard HID battery.
              services [--device <name>]  List RFCOMM services (SDP) of a device.
                       [--cached]         Use the Windows SDP cache instead of querying the device.
              query                       Open the control channel and send only read queries
                                          (handshake, product id, event codes, feature status,
                                          battery, noise control, equalizer). Prints every frame.
                       [--eq-list]        Also query the equalizer preset list.
                       [--only a,b]       Run only the named queries.
              listen [--seconds N]        Subscribe to device events and print them (default 60 s).
              watch  [--seconds N]        Subscribe and also poll battery / noise control / features,
                     [--interval-ms N]    printing only changes (default 120 s, 2000 ms).
              get --cmd 0x01NN            Send exactly one read (GET-group) request.
                  [--payload HEX]         Commands outside 0x01xx are refused.

            Write commands (change device settings; require --confirm-write):
              set-anc --index N[,N..]     Select noise-control protocol index(es), read back after
                      [--dwell-ms 6000]   each write, then restore the original mode
                      [--no-restore]      (only indices documented for the model are accepted).
              set-eq --preset N[,N..]     Select device EQ preset(s) with read-back, then restore.

            Runtime:
              monitor [--seconds N]       Run the app's device runtime headlessly, print state changes
                                          and finally every feature value the session start read.
                      [--set-anc a,b --confirm-write]  Also run commands (off|anc|transparency|adaptive|smart|deep|medium|light|less|standard|more).

            Experimental DokiDoki / TryFun BLE:
              dokidoki scan [--seconds N]
                                          Scan advertisements matching TF-/DK-/TRYFUN- names
                                          or service FFAC; deduplicate by address.
              dokidoki connect --address XX:XX:XX:XX:XX:XX
                          [--address-type auto|public|random|unspecified]
                          [--seconds N] [--heartbeat-ms N]
                                          Connect without classic pairing, list services and
                                          characteristics, subscribe FFB8, send heartbeats,
                                          and always send zero/stop frames before disconnect.
              dokidoki stop --address XX:XX:XX:XX:XX:XX
                                          Connect and send zero to linear, rotary, and vibration.

            Diagnostics:
              render-model --file <x.vfxms> --out <dir>  Render a HeyMelody 3D model off screen to PNG
                       [--size 600] [--yaw 0,90] [--pitch 0] [--software]

            Common options:
              --device <text>     Device name substring (default: first paired device named 'OPPO'/'Enco').
              --uuid <uuid>       Override the control service UUID.
              --raw               Also print raw received byte chunks.
              --report <file>     Write a JSON transcript (no Bluetooth addresses).
              --timeout-ms <n>    Per-request timeout (default 3000).
              --show-address      Print full Bluetooth addresses (redacted by default).
              --show-sensitive    Print payloads that contain other devices' names/addresses.
                                  (--raw also requires this; reports always stay redacted.)
            """);
    }

    private static async Task<int> ListDevicesAsync(ProbeOptions options, CancellationToken ct)
    {
        var catalog = new BluetoothDeviceCatalog();
        var devices = await catalog.GetPairedClassicDevicesAsync(ct);
        if (devices.Count == 0)
        {
            Console.WriteLine("No paired Bluetooth Classic devices found.");
            return 3;
        }
        Console.WriteLine($"{"#",-3} {"Connected",-10} {"Address",-18} {"CoD",-8} Name");
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            string address = options.Has("show-address") ? d.Address.ToFullString() : d.Address.ToRedactedString();
            Console.WriteLine($"{i,-3} {(d.IsConnected ? "yes" : "no"),-10} {address,-18} {d.ClassOfDeviceRaw,-8:X6} {d.Name}");
        }
        return 0;
    }

    private static async Task<int> ListServicesAsync(ProbeOptions options, CancellationToken ct)
    {
        var catalog = new BluetoothDeviceCatalog();
        var device = await DeviceSelector.SelectAsync(catalog, options, ct);
        if (device is null) return 3;

        bool uncached = !options.Has("cached");
        Console.WriteLine($"Device: {device.Name} ({(options.Has("show-address") ? device.Address.ToFullString() : device.Address.ToRedactedString())}), connected={device.IsConnected}");
        Console.WriteLine($"SDP query mode: {(uncached ? "uncached (live query)" : "cached")}");

        var result = await catalog.GetRfcommServicesAsync(device.DeviceId, uncached, includeSdpRecords: true, ct);
        Console.WriteLine($"Result: {result.Error}, {result.Services.Count} RFCOMM service(s)");
        foreach (var service in result.Services)
        {
            string tag = OppoServiceUuids.IsCandidate(service.ServiceUuid) ? "  <-- OPPO control candidate" : string.Empty;
            Console.WriteLine($"- {service.ServiceUuid:D}{tag}");
            if (service.Record is { } record)
            {
                Console.WriteLine($"    name: {record.ServiceName ?? "(none)"}; rfcomm channel: {record.RfcommChannel?.ToString() ?? "?"}");
                if (record.ServiceClassIds.Count > 0)
                    Console.WriteLine($"    classes: {string.Join(", ", record.ServiceClassIds)}");
                Console.WriteLine($"    attributes: {string.Join(", ", record.RawAttributes.Keys.Order().Select(k => $"0x{k:X4}"))}");
            }
            else if (service.SdpError is not null)
            {
                Console.WriteLine($"    SDP attributes unavailable: {service.SdpError}");
            }
        }

        int candidates = result.Services.Count(s => OppoServiceUuids.IsCandidate(s.ServiceUuid));
        Console.WriteLine(candidates > 0
            ? $"Found {candidates} OPPO control channel candidate(s)."
            : "No known OPPO control channel candidates were found in SDP.");
        return 0;
    }
}
