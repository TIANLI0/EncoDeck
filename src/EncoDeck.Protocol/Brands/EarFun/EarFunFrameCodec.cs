// SPDX-License-Identifier: GPL-3.0-only
// EarFun wire facts: EarKit a77acd18890a5f28746c2303bfff0e23fff8d8ca (MIT; see THIRD_PARTY_NOTICES.md).
namespace EncoDeck.Protocol.Brands.EarFun;

public sealed record EarFunPacket(ushort Command, byte Status, byte[] Payload);

/// <summary>Bounded, incremental GAIA v3/v4 or EF decoding, including optional GAIA XOR and EF checksums.</summary>
public sealed class EarFunFrameCodec(bool gaia)
{
    private readonly List<byte> _buffer = [];

    public static byte[] Encode(bool gaia, ushort command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 255) throw new ArgumentOutOfRangeException(nameof(payload));
        if (gaia) return [0xFF, 4, 0, (byte)payload.Length, 0, 10, (byte)(command >> 8), (byte)command, .. payload];
        int sum = payload.Length;
        foreach (byte value in payload) sum += value;
        return [0xEF, (byte)(command >> 8), (byte)command, (byte)payload.Length, .. payload, (byte)(sum & 255), 0xFE];
    }

    public IReadOnlyList<EarFunPacket> Feed(ReadOnlySpan<byte> bytes)
    {
        var packets = new List<EarFunPacket>();
        // Consume incrementally, so a large incoming chunk cannot grow the pending buffer without bound.
        foreach (byte value in bytes)
        {
            _buffer.Add(value);
            while (_buffer.Count > 0)
            {
                if (_buffer[0] != (gaia ? 0xFF : 0xEF)) { _buffer.RemoveAt(0); continue; }
                if (_buffer.Count < 4) break;
                int header = gaia && (_buffer[2] & 2) != 0 ? 5 : 4;
                if (gaia && (_buffer[1] is not (3 or 4) || (_buffer[2] & ~3) != 0)) { _buffer.RemoveAt(0); continue; }
                if (_buffer.Count < header) break;
                int length = header == 5 ? (_buffer[3] << 8) | _buffer[4] : _buffer[3];
                if (length > 1024) { _buffer.RemoveAt(0); continue; }
                int total = gaia ? header + 4 + length + (_buffer[2] & 1) : length + 6;
                if (_buffer.Count < total) break;
                byte[] frame = _buffer.GetRange(0, total).ToArray();
                if (gaia)
                {
                    int checksum = 0;
                    if ((frame[2] & 1) != 0) foreach (byte b in frame) checksum ^= b;
                    if (checksum != 0) { _buffer.RemoveRange(0, total); continue; }
                    // Only EarFun vendor 0x000A, replies 0x83xx or reported values 0x03xx.
                    if (frame[header] == 0 && frame[header + 1] == 10 && frame[header + 2] is 0x83 or 3 && length >= 1)
                        packets.Add(new((ushort)(0x0300 | frame[header + 3]), frame[header + 4], frame[(header + 5)..(header + 4 + length)]));
                }
                else
                {
                    int checksum = length;
                    foreach (byte b in frame.AsSpan(4, length)) checksum += b;
                    if (frame[^1] != 0xFE || frame[^2] != (byte)(checksum & 255)) { _buffer.RemoveAt(0); continue; }
                    packets.Add(new((ushort)((frame[1] << 8) | frame[2]), 0, frame[4..(4 + length)]));
                }
                _buffer.RemoveRange(0, total);
            }
        }
        return packets;
    }
}
