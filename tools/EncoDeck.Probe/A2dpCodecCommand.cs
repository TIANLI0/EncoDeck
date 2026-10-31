// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.A2dpCodec;

namespace EncoDeck.Probe;

/// <summary>
/// The A2DP codec Windows logs: <c>read</c> lists the recorded streams of the session's file (or <c>--file</c>);
/// <c>authorize</c> / <c>remove</c> register or unregister that session (administrator prompt).
/// </summary>
internal static class A2dpCodecCommand
{
    public static async Task<int> RunAsync(ProbeOptions options)
    {
        string action = options.Positional.Count > 0 ? options.Positional[0].ToLowerInvariant() : "read";
        switch (action)
        {
            case "read":
                string path = options.Get("file") ?? WindowsA2dpCodecLog.LogPath;
                Console.WriteLine($"authorized: {WindowsA2dpCodecLog.IsAuthorized}  file: {path}");
                if (!File.Exists(path)) return 1;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var records = WindowsA2dpCodecLog.Read(path);
                Console.WriteLine($"{records.Count} streams in {watch.ElapsedMilliseconds} ms");
                foreach (var record in records)
                    Console.WriteLine($"  {record.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {record.Address:X12}  {record.Codec}");
                foreach (var latest in WindowsA2dpCodecLog.Latest(records).Values)
                    Console.WriteLine($"latest {latest.Address:X12}: {latest.Codec}");
                return 0;
            case "authorize":
                await WindowsA2dpCodecLog.AuthorizeAsync();
                Console.WriteLine($"authorized: {WindowsA2dpCodecLog.IsAuthorized}");
                return WindowsA2dpCodecLog.IsAuthorized ? 0 : 1;
            case "remove":
                await WindowsA2dpCodecLog.RemoveAsync();
                Console.WriteLine($"authorized: {WindowsA2dpCodecLog.IsAuthorized}");
                return 0;
            default:
                Console.Error.WriteLine("a2dp-codec [read [--file <etl>]] | a2dp-codec authorize | a2dp-codec remove   (administrator prompt)");
                return 2;
        }
    }
}
