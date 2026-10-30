// SPDX-License-Identifier: GPL-3.0-only
// Facts only (no code copied). Airoha RACE noise control as used by Harman's Airoha models:
//  - ANC status read: HOSTAUDIO_MMI_GET_ENUM 0x0901 with module 5 (u16 LE); the reply is
//    [module 05 00, status, filter, optional gain s16 LE]. Seen answering on a real JBL Tune 770NC
//    (gist wielkiborsuk/f46f6792…, mode byte at payload[3]) and documented the same way by
//    HellzYeahh/earshot's JBL notes ("reply byte 9 = filter").
//  - ANC write: RACE 0x0E06, [00 0A filter (mode)] on and [00 0B] off; the reply is [status, sub-id].
//    The RACE id and the ON/OFF sub-ids match Airoha's published RACE command specification
//    (RACE_DSPREALTIME_ANC, RACE_ANC_ON = 10, RACE_ANC_OFF = 11).
//  - Filter numbering: 1–4 noise cancelling; pass-through is 5–7 on AB155x and 9–11 on AB156x /
//    AB158x, whose ON command also carries a mode byte (0 ANC, 4 pass-through). On the JBL Tune
//    770NC, TalkThru read back as 9 and Ambient Aware as 10.
//  - The chip family is named by the ASCII value of NV key 0x1002 (RACE_NVKEY_READFULLKEY 0x0A00,
//    a read; reply [length u16 LE, ASCII…]).
// None of this has been confirmed on an AKG N5 / N9 Hybrid yet, so the controller keeps it behind
// the experimental opt-in and only offers what the headset answers.
using System.Buffers.Binary;
using System.Text;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Akg;

/// <summary>Where an Airoha chip keeps its pass-through filters, which decides the ANC payloads.</summary>
public enum AirohaAncLayout
{
    Unknown,

    /// <summary>AB155x: pass-through filters 5–7, ON command without a mode byte.</summary>
    Ab155x,

    /// <summary>AB156x / AB157x / AB158x: pass-through filters 9–11, ON command with a mode byte.</summary>
    Ab156x,
}

public static class AkgRace
{
    public const ushort MmiGetEnum = 0x0901, Anc = 0x0E06, NvKeyRead = 0x0A00;
    public const ushort AncModule = 5, ChipNameKey = 0x1002;
    public const byte AncOn = 0x0A, AncOff = 0x0B;

    /// <summary>Ambient Aware (the second pass-through filter), the mode the AKG app calls ambient.</summary>
    private const byte Ab155xAmbient = 6, Ab156xAmbient = 10, Ab156xModeAnc = 0, Ab156xModePassThrough = 4;

    /// <summary>Module 5 (u16 LE) for <see cref="MmiGetEnum"/>.</summary>
    public static byte[] AncStatusRequest => [(byte)AncModule, 0];

    /// <summary>NV key 0x1002 (chip name) and a 1000-byte limit, both u16 LE, for <see cref="NvKeyRead"/>.</summary>
    public static byte[] ChipNameRequest => [ChipNameKey & 0xFF, ChipNameKey >> 8, 0xE8, 0x03];

    /// <summary>Whether <paramref name="payload"/> is the reply to the module-5 read at all (whatever its status).</summary>
    public static bool IsAncStatusReply(byte[] payload) => payload.Length >= 3 && payload[0] == AncModule && payload[1] == 0;

    /// <summary><c>[05 00 status filter (gain16)]</c>; false for another module, a short reply or a non-zero status.</summary>
    public static bool TryParseAncStatus(byte[] payload, out byte filter)
    {
        filter = 0;
        if (!IsAncStatusReply(payload) || payload.Length < 4 || payload[2] != 0) return false;
        filter = payload[3];
        return true;
    }

    /// <summary>
    /// The mode a filter index stands for, or null for an index outside the known numbering.
    /// <paramref name="layout"/> is updated when the index itself shows the pass-through numbering.
    /// </summary>
    public static NoiseControlMode? ModeOf(byte filter, ref AirohaAncLayout layout)
    {
        switch (filter)
        {
            case 0: return NoiseControlMode.Off;
            case >= 1 and <= 4: return NoiseControlMode.NoiseCancellation;
            case >= 5 and <= 7 when layout != AirohaAncLayout.Ab156x:
                layout = AirohaAncLayout.Ab155x;
                return NoiseControlMode.Transparency;
            case >= 9 and <= 11 when layout != AirohaAncLayout.Ab155x:
                layout = AirohaAncLayout.Ab156x;
                return NoiseControlMode.Transparency;
            default: return null;
        }
    }

    /// <summary>The 0x0E06 payload for <paramref name="mode"/>, or null when the layout does not allow it.</summary>
    public static byte[]? AncPayload(NoiseControlMode mode, AirohaAncLayout layout) => mode switch
    {
        NoiseControlMode.Off => [0, AncOff],
        NoiseControlMode.NoiseCancellation => layout == AirohaAncLayout.Ab156x ? [0, AncOn, 1, Ab156xModeAnc] : [0, AncOn, 1],
        NoiseControlMode.Transparency => layout switch
        {
            AirohaAncLayout.Ab155x => [0, AncOn, Ab155xAmbient],
            AirohaAncLayout.Ab156x => [0, AncOn, Ab156xAmbient, Ab156xModePassThrough],
            _ => null,
        },
        _ => null,
    };

    /// <summary>The 0x0E06 reply to <paramref name="request"/>: <c>[status, same sub-id, …]</c>.</summary>
    public static bool IsAncReply(byte[] request, byte[] reply) => reply.Length >= 2 && request.Length >= 2 && reply[1] == request[1];

    /// <summary><c>[length u16 LE, ASCII…]</c>; trailing NULs are dropped.</summary>
    public static bool TryParseChipName(byte[] payload, out string name)
    {
        name = string.Empty;
        if (payload.Length < 3) return false;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (length == 0 || length != payload.Length - 2) return false;
        var text = payload.AsSpan(2).TrimEnd((byte)0);
        if (text.IsEmpty || text.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E)) return false;
        name = Encoding.ASCII.GetString(text);
        return true;
    }

    /// <summary>The same chip-family classification the Airoha SDK applies to the chip name.</summary>
    public static AirohaAncLayout Classify(string chipName)
    {
        string name = chipName.Trim().ToUpperInvariant();
        if (name.Length == 0 || name.Contains("AB1562", StringComparison.Ordinal)) return AirohaAncLayout.Unknown;
        if (name.StartsWith("AB158", StringComparison.Ordinal) || name.StartsWith("AB157", StringComparison.Ordinal)
            || name.StartsWith("MT", StringComparison.Ordinal) || name.Contains("AB1565", StringComparison.Ordinal)
            || name.Contains("AB1568", StringComparison.Ordinal)) return AirohaAncLayout.Ab156x;
        return name.StartsWith("AB155", StringComparison.Ordinal) ? AirohaAncLayout.Ab155x : AirohaAncLayout.Unknown;
    }
}
