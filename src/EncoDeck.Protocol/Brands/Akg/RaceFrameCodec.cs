// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace EncoDeck.Protocol.Brands.Akg;

/// <summary>
/// Airoha RACE stream framing: <c>05 type length(u16 LE, id + payload) id(u16 LE) payload</c>; type 5A
/// command, 5B response, 5C command without response, 5D indication.
/// </summary>
public sealed class RaceFrameCodec
{
    private readonly List<byte> _buffer = [];
    public static byte[] Encode(ushort command, ReadOnlySpan<byte> payload, byte type = 0x5A)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, 4090);
        byte[] bytes = new byte[6 + payload.Length];
        bytes[0] = 5; bytes[1] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)(payload.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), command);
        payload.CopyTo(bytes.AsSpan(6));
        return bytes;
    }
    public IReadOnlyList<BrandPacket> Feed(ReadOnlySpan<byte> bytes)
    {
        var packets = new List<BrandPacket>();
        foreach (byte value in bytes)
        {
            _buffer.Add(value);
            while (_buffer.Count >= 4)
            {
                var span = CollectionsMarshal.AsSpan(_buffer);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
                if (span[0] != 5 || span[1] is not (0x5A or 0x5B or 0x5C or 0x5D) || length is < 2 or > 4092)
                { _buffer.RemoveAt(0); continue; }
                if (_buffer.Count < length + 4) break;
                packets.Add(new(BinaryPrimitives.ReadUInt16LittleEndian(span[4..]), span[1], span.Slice(6, length - 2).ToArray()));
                _buffer.RemoveRange(0, length + 4);
            }
        }
        return packets;
    }
}
