// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;
using EncoDeck.Core.Privacy;
using EncoDeck.Platform.A2dpBridge;

namespace EncoDeck.Probe;

/// <summary>
/// Connects to the BlueCaster core (A2DP Windows Bridge up to 2.0.x) over its pipe, prints its state and the changes for a while. Read-only
/// unless asked: --start launches a core (optionally a side-by-side test instance), --quit ends it afterwards.
/// </summary>
internal static class BridgeCommand
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        string? instance = options.Get("instance");
        bool showAddress = options.Has("show-address");
        if (options.Get("start") is { } folder)
        {
            if (!A2dpBridgeComponent.IsBridgeFolder(folder))
            {
                Console.Error.WriteLine($"No {A2dpBridgeComponent.Current.Core} (or {A2dpBridgeComponent.Legacy.Core}) in {folder}.");
                return 2;
            }
            var environment = A2dpBridgeComponent.TestEnvironment(instance, options.Get("config-dir") is { } config ? Path.GetFullPath(config) : null);
            var copy = new BridgeInstallation(Path.GetFullPath(folder), BridgeOrigin.Running, null);
            A2dpBridgeComponent.StartCore(copy, environment);
            Console.WriteLine($"Started {copy.Names.Core} --background{(instance is null ? "" : $" (instance {instance})")}.");
        }

        Console.WriteLine($"Core running: {A2dpBridgeMonitor.IsCoreRunning(instance)} (mutex {A2dpBridgeMonitor.MutexName(instance)} or {A2dpBridgeMonitor.LegacyMutexName(instance)})");
        if (A2dpBridgeComponent.FindInstalled() is { } installed) Console.WriteLine($"BlueCaster installation: {installed.Directory} ({installed.Version ?? "unknown version"})");
        await using var monitor = new A2dpBridgeMonitor(instance: instance) { IdleInterval = TimeSpan.FromSeconds(1) };
        monitor.Start();
        if (!await monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(options.GetInt("connect-seconds", 15)), ct).ConfigureAwait(false))
        {
            Console.WriteLine($"Not connected: {monitor.Status.Link} {monitor.Status.Detail}");
            return 3;
        }
        Console.WriteLine($"Connected to {monitor.Status.Server}{(monitor.Status.Rfcomm ? " (rfcomm)" : "")}");
        string? windowsAdapter = await WindowsAdapterGuard.DefaultAdapterInstanceIdAsync().ConfigureAwait(false);
        string last = "";
        void Print()
        {
            if (monitor.Status.Snapshot is not { } s) return;
            string text = Describe(s, showAddress, windowsAdapter);
            if (text == last) return;
            last = text;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
        }
        Print();
        monitor.Changed += (_, _) => Print();

        if (options.Has("diag"))
        {
            var diag = await monitor.RequestAsync("diag.info", new JsonObject { ["mask"] = !showAddress }, ct: ct).ConfigureAwait(false);
            Console.WriteLine(diag?["text"]?.GetValue<string>());
        }
        var seconds = TimeSpan.FromSeconds(options.GetInt("seconds", 5));
        if (options.Get("rfcomm") is { } service)
            await ReadRfcommAsync(monitor, Guid.Parse(service), seconds, ct).ConfigureAwait(false);
        else await Task.Delay(seconds, ct).ConfigureAwait(false);
        if (options.Has("quit"))
        {
            try { await monitor.RequestAsync("app.quit", ct: ct).ConfigureAwait(false); }
            catch (IOException) { }
            for (int i = 0; i < 50 && A2dpBridgeMonitor.IsCoreRunning(instance); i++) await Task.Delay(100, ct).ConfigureAwait(false);
            Console.WriteLine($"Quit; core running: {A2dpBridgeMonitor.IsCoreRunning(instance)}");
        }
        return 0;
    }

    /// <summary>Opens <paramref name="service"/> through the core and prints what the device sends (nothing is written).</summary>
    private static async Task ReadRfcommAsync(A2dpBridgeMonitor monitor, Guid service, TimeSpan duration, CancellationToken ct)
    {
        string address = monitor.Status.Snapshot?.ActiveStreamProfile?.DeviceAddress ?? "00:00:00:00:00:00";
        BridgeRfcommChannel channel;
        try { channel = await monitor.OpenRfcommAsync(address, service, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or NotSupportedException)
        {
            Console.WriteLine($"rfcomm.open {service}: {(ex is BridgeRequestException r ? $"{r.Code} {string.Join(' ', r.BridgeMessage.Args)}" : ex.Message)}");
            return;
        }
        await using (channel)
        {
            Console.WriteLine($"rfcomm.open {service}: channel {channel.Id}, MTU {channel.Mtu}");
            using var until = CancellationTokenSource.CreateLinkedTokenSource(ct);
            until.CancelAfter(duration);
            var buffer = new byte[4096];
            try
            {
                int n;
                while ((n = await channel.ReadAsync(buffer, until.Token).ConfigureAwait(false)) > 0)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {n} bytes: {Convert.ToHexString(buffer, 0, n)}");
                Console.WriteLine($"Channel closed: {channel.CloseReason}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
    }

    private static string Describe(BridgeSnapshot s, bool showAddress, string? windowsAdapter)
    {
        string Address(string text) => showAddress || BridgeDeviceMatch.ParseAddress(text) is not { } a ? text : new BluetoothAddress(a).ToRedactedString();
        var lines = new List<string>
        {
            $"adapter {s.AdapterStatus}{(s.AdapterError is { } e ? " (" + A2dpBridgeText.Format(e) + ")" : "")}",
        };
        lines.AddRange(s.Adapters.Select(a => $"  adapter {a.DisplayName} driver={a.Driver}/{a.DriverService} firmware={a.FirmwarePresent?.ToString() ?? "n/a"}"
            + (WindowsAdapterGuard.IsSameDevice(a.InstanceId, windowsAdapter) ? " [Windows' own adapter: never assigned]" : "")));
        lines.AddRange(s.Devices.Select(d => $"  device {Address(d.Address)} '{d.Name}' saved={d.Saved} codecs={string.Join(',', d.Codecs)}"));
        lines.AddRange(s.Profiles.Select(p => $"  profile '{p.Name}' -> {Address(p.DeviceAddress)} {A2dpBridgeText.Codec(p.Codec)}"));
        var info = s.Stream.Info;
        lines.Add($"  stream {s.Stream.State} '{A2dpBridgeText.Format(s.Stream.Message)}' profile={s.Stream.Profile ?? "-"}"
            + (info is null ? "" : $" {info.Codec} {info.BitrateKbps} kbps {info.SampleRate} Hz") + (s.Stream.Volume is { } v ? $" volume={v}" : ""));
        return string.Join(Environment.NewLine, lines);
    }
}
