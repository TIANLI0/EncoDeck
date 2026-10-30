// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace EncoDeck.Probe.LeSync;

/// <summary>
/// One per-CIS link statistics report from an Intel controller. Counters run from the moment the CIS was
/// established. <see cref="CisIndex"/> is the CIS's place in the CIG (0 is scheduled first).
/// </summary>
/// <param name="Packets">TLV 0xAF: packets transmitted on the CIS.</param>
/// <param name="LateSubevent">TLV 0xBA: grows on the later CIS when the earlier one was given the audio frame
/// one ISO interval late; the meaning is inferred from measurements, Intel does not document it.</param>
/// <param name="Retransmitted">TLV 0xAE.</param>
internal readonly record struct CisLinkSample(ushort Handle, byte CisIndex, uint Packets, uint LateSubevent, uint Retransmitted);

/// <summary>
/// Parses Intel's link statistics telemetry (vendor event FF len 87 80 …). Each report is a type byte followed by
/// TLVs; the per-CIS report starts its TLVs with 0xAA = connection handle. Other reports (ACL links, radio) are
/// ignored.
/// </summary>
internal static class IntelTelemetry
{
    private const byte HandleTag = 0xAA;
    private const byte RetransmittedTag = 0xAE;
    private const byte PacketsTag = 0xAF;
    private const byte CisIndexTag = 0xB6;
    private const byte LateSubeventTag = 0xBA;

    /// <summary>Accepts the telemetry starting at 87 80, or the whole HCI vendor event starting at FF.</summary>
    public static bool TryParseCis(ReadOnlySpan<byte> telemetry, out CisLinkSample sample)
    {
        sample = default;
        if (telemetry.Length >= 2 && telemetry[0] == 0xFF) telemetry = telemetry[2..];
        // 87 80, then one report-type byte, then TLVs.
        if (telemetry.Length < 3 || telemetry[0] != 0x87 || telemetry[1] != 0x80) return false;
        ReadOnlySpan<byte> tlvs = telemetry[3..];
        ushort? handle = null;
        byte cisIndex = 0;
        uint packets = 0, late = 0, retransmitted = 0;
        bool hasPackets = false, hasLate = false;
        while (tlvs.Length >= 2)
        {
            byte tag = tlvs[0];
            int length = tlvs[1];
            if (tlvs.Length < 2 + length) return false;
            ReadOnlySpan<byte> value = tlvs.Slice(2, length);
            switch (tag)
            {
                case HandleTag when length == 2:
                    handle = BinaryPrimitives.ReadUInt16LittleEndian(value);
                    break;
                case CisIndexTag when length == 1:
                    cisIndex = value[0];
                    break;
                case PacketsTag when length == 4:
                    packets = BinaryPrimitives.ReadUInt32LittleEndian(value);
                    hasPackets = true;
                    break;
                case LateSubeventTag when length == 4:
                    late = BinaryPrimitives.ReadUInt32LittleEndian(value);
                    hasLate = true;
                    break;
                case RetransmittedTag when length == 4:
                    retransmitted = BinaryPrimitives.ReadUInt32LittleEndian(value);
                    break;
            }
            tlvs = tlvs[(2 + length)..];
        }
        if (handle is null || !hasPackets || !hasLate) return false;
        sample = new CisLinkSample(handle.Value, cisIndex, packets, late, retransmitted);
        return true;
    }

    /// <summary>
    /// The Intel Bluetooth driver forwards each report to ETW as "1822 - Intel performance counter Event": a
    /// NUL-terminated ASCII caption, a 32-bit length, then the telemetry bytes starting at 87 80.
    /// </summary>
    public static bool TryParseEtwPayload(ReadOnlySpan<byte> payload, out CisLinkSample sample)
    {
        sample = default;
        int nul = payload.IndexOf((byte)0);
        if (nul < 0 || payload.Length < nul + 5) return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(nul + 1, 4));
        ReadOnlySpan<byte> rest = payload[(nul + 5)..];
        if (length == 0 || length > rest.Length) return false;
        return TryParseCis(rest[..(int)length], out sample);
    }
}
