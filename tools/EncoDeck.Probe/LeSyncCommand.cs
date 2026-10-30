// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Platform.LeAudioSync;
using EncoDeck.Probe.LeSync;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Probe;

/// <summary>
/// Runs the Labs LE Audio left/right calibration headless against the active LE Audio endpoints, printing every status
/// change. Test drivers rebuild the link by creating a file, so it can run while an installed EncoDeck keeps running.
/// </summary>
internal static class LeSyncCommand
{
    /// <summary>
    /// Research (run elevated): turns on the Intel controller's per-second link statistics, prints the per-CIS reports
    /// the driver forwards to ETW while an LE Audio stream plays, then restores the controller.
    /// </summary>
    public static async Task<int> SelfTestAsync(ProbeOptions options, CancellationToken ct)
    {
        Console.WriteLine($"manufacturer: {IntelBluetoothRadio.GetManufacturer()?.ToString() ?? "none"}");
        Console.WriteLine("enabling link statistics");
        IntelVendorCommands.EnableLinkStatistics();
        int count = 0;
        try
        {
            using (LinkStatisticsTrace.Start(sample =>
            {
                Interlocked.Increment(ref count);
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {sample}");
            }))
            {
                Console.WriteLine("trace running");
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.GetInt("seconds", 8), 2, 600)), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Console.WriteLine($"samples: {count}; restoring");
            IntelVendorCommands.DisableLinkStatistics();
            Console.WriteLine("restored");
        }
        return 0;
    }

    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        }).SetMinimumLevel(LogLevel.Information));
        var logger = loggers.CreateLogger("le-sync");

        Console.WriteLine($"support: {LeAudioSyncEligibility.Check()}");
        var endpoints = options.Get("render") is { } render
            ? new LeAudioEndpoints(WindowsAudioEndpointPlatform.ToMmDeviceId(render), null)
            : await FindLeEndpointsAsync(ct);
        if (endpoints is null)
            return 2;
        Console.WriteLine($"endpoints: {endpoints}");

        var timings = LeAudioSyncTimings.Default;
        if (options.Get("teardown-ms") is { } teardown)
            timings = timings with { Teardown = TimeSpan.FromMilliseconds(int.Parse(teardown, System.Globalization.CultureInfo.InvariantCulture)) };
        await using var guard = new LeAudioSyncGuard(new WindowsLeAudioSyncPlatform(), logger, timings);
        guard.Changed += (_, _) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} status: {guard.Status}");
        guard.SetEnabled(true);
        guard.SetEndpoints(endpoints);
        // Test driver hooks: these files ask for a rebuild without the check clicks, or for the check clicks.
        string? rebuildFile = options.Get("resync-file");
        string? checkFile = options.Get("check-file");
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(options.GetInt("seconds", 60), 5, 3600));
        try
        {
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(200, ct);
                if (rebuildFile is not null && File.Exists(rebuildFile))
                {
                    File.Delete(rebuildFile);
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} rebuild requested");
                    guard.Rebuild();
                }
                if (checkFile is not null && File.Exists(checkFile))
                {
                    File.Delete(checkFile);
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} check requested");
                    guard.StartCheck();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        Console.WriteLine("stopping");
        return 0;
    }

    private static async Task<LeAudioEndpoints?> FindLeEndpointsAsync(CancellationToken ct)
    {
        var platform = new WindowsAudioEndpointPlatform();
        var renders = await platform.GetRenderEndpointsAsync(ct);
        var le = renders.Where(e => e is { Kind: AudioEndpointKind.LeAudioRender, IsActive: true, IsDisabled: false }).ToList();
        if (le.Count != 1)
        {
            Console.Error.WriteLine($"expected one active LE Audio render endpoint, found {le.Count}");
            return null;
        }
        var captures = await platform.GetCaptureEndpointsAsync(ct);
        string? capture = captures.Where(c => c is { Kind: AudioEndpointKind.LeAudioCapture, IsDisabled: false }
                && c.ContainerId == le[0].ContainerId)
            .Select(c => WindowsAudioEndpointPlatform.ToMmDeviceId(c.InstanceId)).FirstOrDefault();
        return new LeAudioEndpoints(WindowsAudioEndpointPlatform.ToMmDeviceId(le[0].InstanceId), capture);
    }
}
