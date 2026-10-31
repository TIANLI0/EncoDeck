// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.LeAudioCodec;

namespace EncoDeck.Probe;

/// <summary>
/// The Labs LE Audio bitrate hook from the command line: <c>status</c> shows support, what was applied and the hook's
/// log; <c>apply --preset &lt;id&gt;</c> (run elevated) does what the card's Apply button does.
/// </summary>
internal static class LeHookCommand
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        string action = options.Positional.Count > 0 ? options.Positional[0].ToLowerInvariant() : "status";
        return action switch
        {
            "status" => await StatusAsync(ct),
            "apply" => await ApplyAsync(options),
            "resolve" => await ResolveAsync(ct),
            _ => Usage(),
        };
    }

    /// <summary>Finds the patched functions in this PC's Microsoft.Bluetooth.Audio.dll (downloads its public symbols once).</summary>
    private static async Task<int> ResolveAsync(CancellationToken ct)
    {
        string dll = Path.Combine(Environment.SystemDirectory, "Microsoft.Bluetooth.Audio.dll");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (targets, error) = await LeAudioHookTargets.ResolveAsync(dll, LeAudioCodecHook.UserSymbolCache, ct);
        Console.WriteLine($"resolve: {error} in {watch.ElapsedMilliseconds} ms");
        foreach (var target in targets ?? [])
            Console.WriteLine($"  0x{target.Rva:X} {Convert.ToHexString(target.Prologue)}  {target.Name}");
        return targets is null ? 1 : 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("le-hook status | le-hook apply --preset <id> [--mode auto|low|high] [--dll <path>]   (apply needs an elevated prompt)");
        foreach (var preset in LeAudioCodecPreset.All)
            Console.Error.WriteLine($"  {preset.Id,-8} {(preset.IsWindowsDefault ? "Windows' own choice" : $"{preset.FrameUs} us, {preset.Octets} octets, {preset.Kbps:0.#} kbps")}");
        return 2;
    }

    private static async Task<int> StatusAsync(CancellationToken ct)
    {
        Console.WriteLine($"support: {await LeAudioCodecHook.CheckSupportAsync(ct)}");
        var applied = LeAudioCodecHook.ReadApplied();
        Console.WriteLine($"applied: {applied.Preset.Id}, {applied.Mode}{(applied.IsWindowsDefault ? "" : applied.Active ? " (active)" : " (lost: the service restarted)")}");
        if (LeAudioCodecHook.ReadLink() is { } link)
            Console.WriteLine($"link: {link.At:HH:mm:ss} {link.FrameUs} us, max SDU {link.MaxSdu} ({link.Kbps:0.#} kbps), at most {link.LatencyMs} ms, {link.Retransmissions} retransmissions");
        if (File.Exists(LeAudioCodecHook.LogPath))
        {
            using var stream = new FileStream(LeAudioCodecHook.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            Console.WriteLine("log:");
            foreach (string line in reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(15))
                Console.WriteLine("  " + line.TrimEnd('\r'));
        }
        return 0;
    }

    private static async Task<int> ApplyAsync(ProbeOptions options)
    {
        if (LeAudioCodecPreset.Find(options.Get("preset") ?? "") is not { } preset) return Usage();
        string dll = options.Get("dll") ?? Path.Combine(AppContext.BaseDirectory, LeAudioCodecHook.HookDllName);
        var mode = options.Get("mode")?.ToLowerInvariant() switch
        {
            "low" => LeAudioLatencyMode.LowLatency,
            "high" => LeAudioLatencyMode.HighReliability,
            _ => LeAudioLatencyMode.Auto,
        };
        int result = await LeAudioCodecHook.ApplyElevatedAsync(preset, mode, dll, Console.WriteLine);
        Console.WriteLine(result == LeAudioCodecHook.Result.Ok
            ? $"applied {preset.Id}: takes effect when Windows next builds the LE Audio stream"
            : $"not applied: result {result}");
        return result;
    }
}
