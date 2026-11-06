// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth;
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Oppo.Models;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Probe;

/// <summary>
/// Runs the same runtime stack as the app (DeviceManager → supervisor → controller → state store)
/// headlessly and prints every state change. Optional --set-anc exercises the command path.
/// With <paramref name="watch"/> (Probe 'watch' on a non-OPPO model) it also prints every control frame
/// the model's controller sends and receives, and runs no write.
/// </summary>
internal static class MonitorCommand
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct, bool watch = false)
    {
        using var loggers = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .SetMinimumLevel(options.Has("verbose") ? LogLevel.Debug : LogLevel.Information));

        var store = new DeviceStateStore();
        var registry = new DeviceProfileRegistry(BuiltInProfiles.Create());

        // --device pins the runtime to one paired device; without it the manager's own selection wins.
        string? deviceQuery = options.Get("device");
        string? selectedId = null;
        if (deviceQuery is not null)
        {
            var catalog = new BluetoothDeviceCatalog();
            var picked = await DeviceSelector.SelectAsync(catalog, options, ct).ConfigureAwait(false);
            if (picked is null) return 2;
            selectedId = picked.DeviceId;
        }

        await using var manager = new DeviceManager(
            registry,
            store,
            new RfcommChannelConnector(registry, loggers.CreateLogger<RfcommChannelConnector>()),
            preferredDeviceId: () => selectedId,
            allowExperimental: static () => false,
            loggers,
            trace: watch || options.Has("trace") ? new ConsoleTrace(printRaw: watch && options.Has("raw")) : null);

        var ready = new TaskCompletionSource();
        store.StateChanged += (_, e) =>
        {
            Console.WriteLine($"[rev {e.Current.Revision}] {Summarize(e.Current)}");
            if (e.Current.Connection == Core.Devices.DeviceConnectionState.ControlReady) ready.TrySetResult();
        };
        manager.ActiveDeviceLinkConnected += (_, id) => Console.WriteLine($"*** link connected: {id.Name} (popup trigger)");
        manager.Start();
        if (selectedId is not null)
        {
            Console.WriteLine($"*** pinned device: {deviceQuery} -> {selectedId}");
            await manager.SelectDeviceAsync(selectedId, ct).ConfigureAwait(false);
        }

        int seconds = options.GetInt("seconds", watch ? 120 : 30);
        var deadline = Task.Delay(TimeSpan.FromSeconds(seconds), ct);

        if (watch)
        {
            Console.WriteLine($"Watching for {seconds}s through the app's runtime, with the model's own control service and protocol. "
                + "Every control frame is printed (TX = sent, RX = received; 'unsolicited' = pushed by the device). "
                + "Once ControlReady appears, change noise control on the headset or in its phone app, and note the time of each change. Ctrl+C to stop.");
        }

        if (!watch && options.Get("set-anc") is { } target && options.Has("confirm-write"))
        {
            await Task.WhenAny(ready.Task, deadline);
            if (ready.Task.IsCompleted)
            {
                foreach (var step in target.Split(','))
                {
                    var state = ParseNoise(step.Trim());
                    if (state is null) { Console.Error.WriteLine($"Unknown noise option '{step}'."); continue; }
                    var started = DateTimeOffset.UtcNow;
                    var result = await manager.SetNoiseControlAsync(state, ct);
                    Console.WriteLine($">>> SetNoiseControl({state.Mode}/{state.LevelId ?? "-"}) = {result} in {(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0} ms");
                    await Task.Delay(TimeSpan.FromSeconds(options.GetInt("dwell", 4)), ct);
                }
            }
        }

        // What the app's Disconnect and Connect do, once each, on the active device (kept in memory only).
        if (!watch && options.Has("disconnect-cycle") && options.Has("confirm-write"))
        {
            await Task.WhenAny(ready.Task, deadline);
            if (ready.Task.IsCompleted && store.Current.Device is { } device)
            {
                var started = DateTimeOffset.UtcNow;
                var result = await manager.DisconnectDeviceAsync(device.DeviceId, cancellationToken: ct);
                Console.WriteLine($">>> DisconnectDevice = {result} in {(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0} ms");
                await Task.Delay(TimeSpan.FromSeconds(options.GetInt("dwell", 15)), ct);
                started = DateTimeOffset.UtcNow;
                result = await manager.ConnectDeviceAsync(device.DeviceId, ct);
                Console.WriteLine($">>> ConnectDevice = {result} in {(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0} ms");
            }
        }

        try
        {
            await deadline;
        }
        catch (OperationCanceledException)
        {
        }
        Console.WriteLine("Final: " + Summarize(store.Current));
        Console.WriteLine("Features: " + SummarizeFeatures(store.Current));
        return 0;
    }

    private static NoiseControlState? ParseNoise(string text) => text.ToLowerInvariant() switch
    {
        "off" => new NoiseControlState(NoiseControlMode.Off),
        "anc" => new NoiseControlState(NoiseControlMode.NoiseCancellation),
        "transparency" => new NoiseControlState(NoiseControlMode.Transparency),
        "adaptive" => new NoiseControlState(NoiseControlMode.Adaptive),
        "smart" or "deep" or "medium" or "light" => new NoiseControlState(NoiseControlMode.NoiseCancellation, text.ToLowerInvariant()),
        "less" or "standard" or "more" => new NoiseControlState(NoiseControlMode.Adaptive, text.ToLowerInvariant()),
        _ => null,
    };

    /// <summary>Feature state read by the official session-start sequence. Other devices' names/addresses are not printed.</summary>
    private static string SummarizeFeatures(DeviceState s)
    {
        var parts = new List<string>
        {
            $"pid={s.ProductId:X6} color={s.ColorId?.ToString() ?? "?"}",
            $"spatial={s.Spatial?.Value.ToString() ?? "?"}",
            $"gameSound={(s.GameSound?.Value is { } g ? $"{g.SelectedType} of [{string.Join(",", g.SupportedTypes)}]" : "?")}",
            $"promptVolume={s.PromptVolume?.Value.ToString() ?? "?"}",
            $"bass={(s.BassEngine?.Value is { } b ? $"{b.Current} in {b.Min}..{b.Max}" : "?")}",
            $"headMotion={s.HeadMotionType?.Value.ToString() ?? "?"}",
            $"leBrSwitch={(s.LeAudioSwitch?.Value is { } le ? $"{(le.Enabled ? 1 : 0)}/owner {(le.Owner ? 1 : 0)}" : "?")}",
            $"cycle={(s.NoiseCycle?.Value is { } c ? $"L=0x{c.LeftMask:X4} R=0x{c.RightMask:X4}" : "?")}",
            $"smartLevel={s.SmartLevelId ?? "-"}",
        };
        if (s.KeyFunctions?.Value is { } keys)
        {
            parts.Add("keys=" + string.Join(",", keys.Select(k => $"{(int)k.Side}/{k.Button}/{(int)k.Action}={k.Function}")));
        }
        if (s.CustomEqualizers?.Value is { } presets)
        {
            parts.Add($"customEq={presets.Count}" + string.Concat(presets.Select(p => $" [id {p.Id}{(p.IsSelected ? " selected" : string.Empty)} {p.Bands.Count} bands {p.MinGain}..{p.MaxGain}]")));
        }
        if (s.SourceDevices?.Value is { } sources)
        {
            parts.Add($"sources={sources.Count}" + string.Concat(sources.Select(d => $" [state {d.State} type {d.DeviceType}{(d.IsThisDevice ? " this" : string.Empty)}{(d.IsAudioActive ? " audio" : string.Empty)}]")));
        }
        if (s.PreferredDevice?.Value is { } preferred) parts.Add(preferred.Address is null ? "preferred=auto" : "preferred=set");
        return string.Join(" | ", parts);
    }

    private static string Summarize(DeviceState s)
    {
        string Battery(Observed<BatteryReading>? r) => r is null ? "?" : $"{r.Value.Level}%{(r.Value.IsCharging == true ? "+" : string.Empty)}({r.Source.ToString()[0]})";
        var parts = new List<string>
        {
            s.Device?.Name ?? "(no device)",
            s.Connection.ToString(),
            $"LE bonded={s.LeAudioLinks.BondedEarbuds} connected={s.LeAudioLinks.ConnectedEarbuds} primary={s.LeAudioLinks.PrimaryConnected} secondary={s.LeAudioLinks.SecondaryConnected} streaming=unverified",
            $"model={s.Profile?.ModelId ?? "-"}",
            $"L={Battery(s.Battery.Left)} R={Battery(s.Battery.Right)} C={Battery(s.Battery.Case)}",
        };
        if (s.Wear is { } w) parts.Add($"ear L={w.Value.LeftInEar} R={w.Value.RightInEar}");
        if (s.NoiseControl is { } n) parts.Add($"noise={n.Value.Mode}/{n.Value.LevelId ?? "-"}({n.Source})");
        if (s.Equalizer is { } e) parts.Add($"eq={e.Value.RawPresetValue}");
        if (s.Firmware is { } f) parts.Add($"fw={f.Value.Version}");
        if (s.Toggles.Count > 0) parts.Add("toggles=" + string.Join(",", s.Toggles.Select(t => $"{t.Key}:{t.Value.Value}")));
        if (s.PendingCommands.Count > 0) parts.Add($"pending={s.PendingCommands.Count}");
        if (s.LastError is { } err) parts.Add($"error=\"{err.Message}\"");
        return string.Join(" | ", parts);
    }
}
