// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;

namespace EncoDeck.Probe.LeSync;

/// <summary>
/// Research, tied to one controller firmware: the Intel Solar A3 main image shipped in ibtusb.sys 24.80.0.2 (loaded at
/// 0x00100800). Its LC3 offload keeps one 12-entry queue of encoded frames per lane (slot 0 = Front Left, slot 1 =
/// Front Right, 1548 bytes apart; +1 = 0xFF while the lane is unused, +5 = read index, +6 = queued frames). After each
/// right frame is encoded, 0x18E6BC hands down the head of the right queue together with the head of the left queue,
/// with no frame index or timestamp check, so a stream whose first right frame was encoded before the first left one
/// pairs right frame k with left frame k-1 for its whole life (the left earbud plays one frame late).
/// Nothing here is valid for another image: <see cref="Verify"/> reads back the code these addresses come from.
/// </summary>
internal static class FirmwareAddressTable
{
    public const uint ImageStart = 0x00100800;
    public const uint ImageEnd = 0x001A3430;
    public const uint LeftSlot = 0x8001AF80;
    public const uint RightSlot = 0x8001B58C;
    public const int SlotLength = 16;
    public const int SlotActive = 1;
    public const int SlotReadIndex = 5;
    public const int SlotQueued = 6;

    // The image's first 32 bytes (ARC vector table).
    public static readonly byte[] ImageHeader = Convert.FromHexString("8809100088E8100090E8100098E81000A0E81000A8E81000B0E81000B8E81000");

    // Pacer selection, per-slot dequeue and the pairing loop: SHA-256 of the bytes in the image file.
    private static readonly (string Name, uint Address, int Length, string Sha256)[] Code =
    [
        ("pacer selection", 0x0018DBE4, 0x74, "2D2EC753C034FA4AF489ABC0F8C0078460017D8074D25CCE70938F222DBAC564"),
        ("slot dequeue", 0x0018E018, 0x76, "163749B4DA4DD0E35DD926851E6ABD256CFE277FF2A5F6A0DABDA91F1328051C"),
        ("pairing loop", 0x0018E6BC, 0x1CE, "3C6BF755196C25B6378648C8AA30B366548456A1C42EF72AEEB126A80E8D3D4B"),
    ];

    /// <summary>Addresses the probe may read: the main image's code, and the two queue slots.</summary>
    public static bool IsReadable(uint address, int length) =>
        (address >= ImageStart && address + (uint)length <= ImageEnd)
        || (address >= LeftSlot && address + (uint)length <= LeftSlot + SlotLength)
        || (address >= RightSlot && address + (uint)length <= RightSlot + SlotLength);

    /// <summary>Null when the running image is the analysed one; otherwise why it is not.</summary>
    public static string? Verify(IntelVendorCommands.Session session)
    {
        if (!session.ReadMemory(ImageStart, ImageHeader.Length).AsSpan().SequenceEqual(ImageHeader))
            return "image header differs";
        foreach (var (name, address, length, sha256) in Code)
        {
            byte[] bytes = ReadSpan(session, address, length);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != sha256)
                return $"{name} at 0x{address:X8} differs";
        }
        return null;
    }

    public static byte[] ReadSpan(IntelVendorCommands.Session session, uint address, int length)
    {
        if (!IsReadable(address, length)) throw new ArgumentOutOfRangeException(nameof(address), $"0x{address:X8}+{length} is outside the known map");
        byte[] result = new byte[length];
        for (int done = 0; done < length;)
        {
            int chunk = Math.Min(IntelVendorCommands.Session.MaxReadLength, length - done);
            session.ReadMemory(address + (uint)done, chunk).CopyTo(result, done);
            done += chunk;
        }
        return result;
    }
}

internal enum PairState
{
    Unknown,
    Aligned,
    Misaligned,
}

/// <summary>One read of both queue slots; <see cref="Ms"/> from the start of the sampling.</summary>
internal readonly record struct QueueSample(double Ms, byte LeftActive, byte LeftRead, byte LeftQueued, byte RightActive, byte RightQueued)
{
    public bool LeftOnly => LeftQueued > 0 && RightQueued == 0;
}

internal sealed record PairStateResult(PairState State, string Reason, double LeftOnlyShare, int Failures, IReadOnlyList<QueueSample> Samples);

/// <summary>
/// Classifies the pairing state from the left queue. Aligned, a left frame waits only while the right frame of the same
/// period is being encoded; misaligned, it waits for the next period, i.e. most of the frame. So the share of samples
/// with a left frame queued (and none on the right) is about encode time / frame when aligned and about 1 minus that
/// when misaligned. The thresholds are placeholders until the acoustic calibration (lc3-pair-monitor) sets them.
/// </summary>
internal static class PairStateMonitor
{
    public const double DefaultMisalignedAt = 0.6;
    public const double DefaultAlignedAt = 0.4;

    public static PairStateResult Sample(IntelVendorCommands.Session session, int samples = 20,
        double misalignedAt = DefaultMisalignedAt, double alignedAt = DefaultAlignedAt)
    {
        var list = new List<QueueSample>(samples);
        int failures = 0;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < samples; i++)
        {
            try
            {
                double ms = clock.Elapsed.TotalMilliseconds;
                byte[] left = FirmwareAddressTable.ReadSpan(session, FirmwareAddressTable.LeftSlot, FirmwareAddressTable.SlotLength);
                byte[] right = FirmwareAddressTable.ReadSpan(session, FirmwareAddressTable.RightSlot, FirmwareAddressTable.SlotLength);
                list.Add(new QueueSample(ms, left[FirmwareAddressTable.SlotActive], left[FirmwareAddressTable.SlotReadIndex],
                    left[FirmwareAddressTable.SlotQueued], right[FirmwareAddressTable.SlotActive], right[FirmwareAddressTable.SlotQueued]));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                failures++;
            }
        }
        return Classify(list, failures, misalignedAt, alignedAt);
    }

    public static PairStateResult Classify(IReadOnlyList<QueueSample> samples, int failures, double misalignedAt, double alignedAt)
    {
        int total = samples.Count + failures;
        if (total == 0 || failures > total * 0.3) return new(PairState.Unknown, $"{failures}/{total} reads failed", 0, failures, samples);
        if (samples.Any(s => s.LeftActive == 0xFF || s.RightActive == 0xFF))
            return new(PairState.Unknown, "a lane is not in use (no two-lane LC3 stream)", 0, failures, samples);
        // Frames flowing: the left read index moves at least once (every frame at 7.5 or 10 ms).
        if (samples.Select(s => s.LeftRead).Distinct().Count() < 2)
            return new(PairState.Unknown, "no frames moved through the left queue", 0, failures, samples);
        double share = samples.Count(s => s.LeftOnly) / (double)samples.Count;
        var state = share >= misalignedAt ? PairState.Misaligned : share <= alignedAt ? PairState.Aligned : PairState.Unknown;
        return new(state, $"left-only share {share:0.00}", share, failures, samples);
    }
}
