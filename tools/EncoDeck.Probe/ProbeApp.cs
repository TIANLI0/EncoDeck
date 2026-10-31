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
                "sony-services" => await SonyServicesCommand.RunAsync(options, ct),
                "sony-probe" => await SonyProbeCommand.RunAsync(options, ct),
                "query" => await QueryCommands.QueryAsync(options, ct),
                "listen" => await QueryCommands.ListenAsync(options, ct),
                "watch" => await QueryCommands.WatchAsync(options, ct),
                "get" => await QueryCommands.GetAsync(options, ct),
                "set-anc" => await WriteCommands.SetNoiseControlAsync(options, ct),
                "set-eq" => await WriteCommands.SetEqualizerAsync(options, ct),
                "le-br-switch" => await WriteCommands.SetLeBrSwitchAsync(options, ct),
                "monitor" => await MonitorCommand.RunAsync(options, ct),
                "le-audio" => await LeAudioCommand.RunAsync(options, ct),
                "le-sync" => await LeSyncCommand.RunAsync(options, ct),
                "le-sync-selftest" => await LeSyncCommand.SelfTestAsync(options, ct),
                "le-hook" => await LeHookCommand.RunAsync(options, ct),
                "multipoint-off" => await LeAudioCommand.DisableMultipointCommandAsync(options, ct),
                "render-model" => await RenderModelCommand.RunAsync(options, ct),
                "bridge" => await BridgeCommand.RunAsync(options, ct),
                "components" => await ComponentsCommand.RunAsync(options, ct),
                _ => Unknown(options.Command),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Console.Error.WriteLine($"Stopped: {ex.GetType().Name}; {ex.Message}; HRESULT=0x{ex.HResult:X8}");
            return 5;
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
                       [--codec]          Also query the raw vendor codec report (0x0114).
                       [--only a,b]       Run only the named queries.
              listen [--seconds N]        Subscribe to device events and print them (default 60 s).
              watch  [--seconds N]        Subscribe and also poll battery / noise control / features,
                     [--interval-ms N]    printing only changes (default 120 s, 2000 ms).
                     [--codec]            Also poll codec reports; vendor ids need model validation.
              get --cmd 0x01NN            Send exactly one read (GET-group) request.
                  [--payload HEX]         Commands outside 0x01xx are refused.

            Write commands (change device settings; require --confirm-write):
              multipoint-off              Disable device multipoint and confirm by fresh read-back.
              set-anc --index N[,N..]     Select noise-control protocol index(es), read back after
                      [--dwell-ms 6000]   each write, then restore the original mode
                      [--no-restore]      (only indices documented for the model are accepted).
              set-eq --preset N[,N..]     Select device EQ preset(s) with read-back, then restore.
              le-br-switch [--state 0|1]  Experimental: print the LE/BR switch (0x0136); with --state
                           [--dwell-ms 3000]   also set it (0x0433) and read it back. Not restored.

            Runtime:
              le-audio --device <name>    Reconnect existing LE bonds and log per-ear links/control.
                       [--seconds N]     Default 60 seconds. Audio streaming remains unverified.
                       [--pair --confirm-write]  Confirm multipoint off, then wait for pairing mode and replace this PC's bonds.
                       [--legacy-pairing]  With --pair, use explicit addresses without Windows set discovery for comparison.
                       [--restore --confirm-write]  Remove this PC's LE Audio pairings (pair again in Windows Settings).
              le-sync [--seconds N]       Hold the active LE Audio link like the Labs left/right calibration and
                      [--resync-file <path>]  print its status; creating the file rebuilds the link, creating
                      [--check-file <path>]   the check file plays the check clicks. Default 60 s.
                      [--render <endpoint id>]  Use this render endpoint instead of the active LE Audio one.
              le-sync-selftest [--seconds N]  Research, run elevated: turn on Intel link statistics and print the
                                              per-CIS reports while audio plays, then restore the controller.
              le-hook [status]            Labs LE Audio bitrate: support, applied preset and the hook's log.
              le-hook apply --preset <id> Elevated: what the card's Apply does (48_1..48_5, or default).
                      [--mode auto|low|high]  Transport: Windows' choice, latency first or reliability first.
                      [--no-call]             Calls and game chat do not switch the earbuds to a call link.
              monitor [--seconds N]       Run the app's device runtime headlessly, print state changes
                                          and finally every feature value the session start read.
                      [--set-anc a,b --confirm-write]  Also run commands (off|anc|transparency|adaptive|smart|deep|medium|light|less|standard|more).

            A2DP Windows Bridge:
              bridge [--seconds N]        Connect to the A2DPWB core's pipe, print its state and changes.
                     [--diag]             Also print A2DPWB's diagnostic text (addresses masked).
                     [--rfcomm <uuid>]    Open that RFCOMM service through the core to the device it streams to
                                          (A2DPWB 2.0.2+) and print what the device sends for --seconds.
                     [--instance <name>]  A side-by-side test core (A2DPWB_INSTANCE).
                     [--start <folder> [--config-dir <dir>]]  Start that core first (--background).
                     [--quit]             Ask the core to exit afterwards.

            Components:
              components                  List the component pins of this build (LHDC, A2DP Bridge).
                         [--install lhdc|a2dpwb --root <folder>]  Download and unpack one as the app does.
                         [--check [--floor <x.y.z-rN>]]  Look for component releases online as the Labs page does.

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
