// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
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
    public static async Task<int> SnapshotAsync(CancellationToken ct)
    {
        var samples = new ConcurrentDictionary<ushort, CisLinkSample>();
        using (LinkStatisticsTrace.Start(sample => samples[sample.Handle] = sample))
        {
            IntelVendorCommands.RequestLinkStatisticsSnapshot();
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        foreach (var sample in samples.Values.OrderBy(sample => sample.CisIndex))
            Console.WriteLine($"CIS 0x{sample.Handle:X4} index={sample.CisIndex} AF={sample.Packets} AE={sample.Retransmitted} BA={sample.LateSubevent}");

        if (samples.Count == 2) return 0;
        Console.Error.WriteLine($"Expected two active CIS reports. Received {samples.Count}.");
        return 2;
    }

    /// <summary>
    /// Research (run elevated): reads controller memory with Intel 0xFC8D, limited to the addresses
    /// <see cref="FirmwareAddressTable"/> knows to be mapped. Without --addr it checks the image header (T1).
    /// </summary>
    public static int FirmwareMemoryRead(ProbeOptions options)
    {
        uint address = options.Get("addr") is { } text
            ? Convert.ToUInt32(text, text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10)
            : FirmwareAddressTable.ImageStart;
        int length = options.GetInt("len", FirmwareAddressTable.ImageHeader.Length);
        if (!FirmwareAddressTable.IsReadable(address, length))
        {
            Console.Error.WriteLine($"0x{address:X8}+{length} is outside the known map (image code or the two queue slots)");
            return 2;
        }
        using var session = IntelVendorCommands.OpenSession();
        Console.WriteLine($"version TLVs: {Convert.ToHexString(session.ReadVersion())}");
        byte[] data = FirmwareAddressTable.ReadSpan(session, address, length);
        Console.WriteLine($"0x{address:X8}: {Convert.ToHexString(data)}");
        if (address != FirmwareAddressTable.ImageStart) return 0;
        int matching = data.Zip(FirmwareAddressTable.ImageHeader).TakeWhile(pair => pair.First == pair.Second).Count();
        Console.WriteLine($"image header: {matching}/{Math.Min(length, FirmwareAddressTable.ImageHeader.Length)} bytes match");
        return matching == Math.Min(length, FirmwareAddressTable.ImageHeader.Length) ? 0 : 3;
    }

    /// <summary>
    /// Research (run elevated): checks that the running firmware is the analysed image, then samples both LC3 queue
    /// slots and prints the pairing state. --log appends every round (samples included) as JSON lines for calibration.
    /// </summary>
    public static async Task<int> FirmwarePairStateAsync(ProbeOptions options, CancellationToken ct)
    {
        using var session = IntelVendorCommands.OpenSession();
        if (FirmwareAddressTable.Verify(session) is { } mismatch)
        {
            Console.Error.WriteLine($"unknown firmware ({mismatch}): state not read");
            return 3;
        }
        int samples = Math.Clamp(options.GetInt("samples", 20), 4, 500);
        int rounds = Math.Clamp(options.GetInt("rounds", 1), 1, 10000);
        int gapMs = Math.Clamp(options.GetInt("gap-ms", 1000), 0, 60000);
        double misalignedAt = options.Get("misaligned-at") is { } m ? double.Parse(m, System.Globalization.CultureInfo.InvariantCulture) : PairStateMonitor.DefaultMisalignedAt;
        double alignedAt = options.Get("aligned-at") is { } a ? double.Parse(a, System.Globalization.CultureInfo.InvariantCulture) : PairStateMonitor.DefaultAlignedAt;
        string? log = options.Get("log");
        for (int round = 0; round < rounds; round++)
        {
            if (round > 0) await Task.Delay(gapMs, ct);
            var result = PairStateMonitor.Sample(session, samples, misalignedAt, alignedAt);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {result.State} ({result.Reason}, {result.Samples.Count} samples in " +
                $"{(result.Samples.Count > 0 ? result.Samples[^1].Ms : 0):0} ms, {result.Failures} failed)");
            if (options.Has("verbose"))
            {
                foreach (var s in result.Samples)
                    Console.WriteLine($"  {s.Ms,7:0.0} ms  left {s.LeftQueued} (read {s.LeftRead}, active {s.LeftActive:X2})  right {s.RightQueued} (active {s.RightActive:X2})");
            }
            if (log is not null)
            {
                var row = new
                {
                    time = DateTimeOffset.Now.ToString("O"),
                    wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    state = result.State.ToString(),
                    share = result.LeftOnlyShare,
                    failures = result.Failures,
                    samples = result.Samples.Select(s => new[] { Math.Round(s.Ms, 2), s.LeftQueued, s.RightQueued, s.LeftRead, s.LeftActive, s.RightActive }),
                };
                await File.AppendAllTextAsync(log, System.Text.Json.JsonSerializer.Serialize(row) + Environment.NewLine, ct);
            }
        }
        return 0;
    }

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

    /// <summary>
    /// Research (run elevated): enables the controller's vendor trace and prints every event the driver forwards to
    /// ETW. Marks payloads that carry the audio path's ABxx firmware log tags (AB0C is emitted per lane with the
    /// channel number, so its ordering is the left/right encode-order signal). --all prints every event.
    /// </summary>
    public static async Task<int> TraceAsync(ProbeOptions options, CancellationToken ct)
    {
        Console.WriteLine($"manufacturer: {IntelBluetoothRadio.GetManufacturer()?.ToString() ?? "none"}");
        Console.WriteLine("enabling vendor trace");
        IntelVendorCommands.EnableLinkStatistics();
        int total = 0, hits = 0;
        bool all = options.Has("all");
        try
        {
            using (IntelEtWCapture.Start((id, payload) =>
            {
                Interlocked.Increment(ref total);
                bool hit = ContainsTag(payload, 0xAB0C) || ContainsTag(payload, 0xAB19) || ContainsTag(payload, 0xAB1B);
                if (hit) Interlocked.Increment(ref hits);
                if (all || hit)
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} id=0x{id:X4} len={payload.Length}{(hit ? " AB" : "")} {Convert.ToHexString(payload)}");
            }))
            {
                Console.WriteLine("trace running");
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.GetInt("seconds", 10), 2, 600)), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Console.WriteLine($"events: {total}, ab: {hits}; restoring");
            IntelVendorCommands.DisableLinkStatistics();
            Console.WriteLine("restored");
        }
        return 0;
    }

    /// <summary>True when the 16-bit little-endian tag appears anywhere in the payload.</summary>
    private static bool ContainsTag(ReadOnlySpan<byte> payload, ushort tag)
    {
        for (int i = 0; i + 1 < payload.Length; i++)
            if (payload[i] == (byte)tag && payload[i + 1] == (byte)(tag >> 8)) return true;
        return false;
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
