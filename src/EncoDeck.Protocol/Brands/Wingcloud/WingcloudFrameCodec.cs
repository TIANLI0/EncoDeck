// SPDX-License-Identifier: GPL-3.0-only
// Protocol facts from the independent Android app "我的X5s Pro" by shui-tiao
// (https://github.com/shui-tiao/my-x5s-pro, GPL-3.0), verified on a
// HONOR CHOICE Earbuds X5s Pro (Wingcloud / USFY, firmware 1.1.64.2).
using EncoDeck.Protocol.Brands.Huawei;

namespace EncoDeck.Protocol.Brands.Wingcloud;

/// <summary>One decoded frame. <see cref="Command"/> is <c>service &lt;&lt; 8 | command</c>.</summary>
public readonly record struct WingcloudFrame(ushort Command, byte[] Payload)
{
    public byte Service => (byte)(Command >> 8);

    public byte CommandId => (byte)Command;
}

/// <summary>
/// Wingcloud framing: <c>5A len_hi len_lo 00 service command TLV… crc_hi crc_lo</c>, where the length is
/// the payload length plus three and the CRC-16/XMODEM covers <c>0x5A</c> up to the last payload byte.
/// The same layout is emitted by the HUAWEI MBB codec; only the TLV rules differ (Wingcloud uses the
/// two-byte length form from 128 bytes on, which the HUAWEI parser would misread).
/// </summary>
public sealed class WingcloudFrameCodec
{
    /// <summary>Largest TLV body accepted (the header's length field is 16 bits).</summary>
    private const int MaxPayload = HuaweiWire.MaxPayload;

    private readonly MbbFrameCodec _inner = new();

    /// <summary>Frames received and dropped as fragments (always 0 for this firmware, kept for diagnostics).</summary>
    public int DroppedFragments => _inner.DroppedFragments;

    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload) => _inner.Encode(command, payload);

    public IReadOnlyList<WingcloudFrame> Feed(ReadOnlySpan<byte> data) =>
        [.. _inner.Feed(data).Select(f => new WingcloudFrame(f.Command, f.Payload))];

    /// <summary>Parses a TLV list. A truncated trailing entry fails the whole list.</summary>
    public static bool TryParseTlv(ReadOnlySpan<byte> payload, out Dictionary<byte, byte[]> fields)
    {
        fields = [];
        while (!payload.IsEmpty)
        {
            if (payload.Length < 2) return false;
            int length = payload[1];
            int offset = 2;
            if ((length & 0x80) != 0)
            {
                if (payload.Length < 3) return false;
                length = ((length & 0x7F) << 8) | payload[2];
                offset = 3;
            }
            if (length > MaxPayload || payload.Length < offset + length) return false;
            if (!fields.TryAdd(payload[0], payload.Slice(offset, length).ToArray())) return false;
            payload = payload[(offset + length)..];
        }
        return fields.Count > 0;
    }

    /// <summary>Encodes one TLV entry, using the two-byte length form from 128 bytes on.</summary>
    public static byte[] Tlv(byte tag, ReadOnlySpan<byte> value) => value.Length < 0x80
        ? [tag, (byte)value.Length, .. value]
        : [tag, (byte)(0x80 | (value.Length >> 8)), (byte)value.Length, .. value];

    public static byte[] Tlv(byte tag, params byte[] value) => Tlv(tag, (ReadOnlySpan<byte>)value);

    /// <summary><c>01 3A</c> read address: <c>TLV(1, TLV(2, TLV(3, service) + TLV(4, prop) + [05, 00]))</c>.</summary>
    public static byte[] ReadProperty(int service, int property)
    {
        byte[] inner = [.. Tlv(3, Int32(service)), .. Tlv(4, Int32(property)), 0x05, 0x00];
        return [.. Tlv(1, Tlv(2, inner))];
    }

    /// <summary><c>01 39</c> write payload: <c>TLV(1, service) + TLV(2, property) + TLV(3, value)</c>, 20 bytes.</summary>
    public static byte[] WriteProperty(int service, int property, int value) =>
        [.. Tlv(1, Int32(service)), .. Tlv(2, Int32(property)), .. Tlv(3, Int32(value))];

    /// <summary>
    /// The reply to a property read: <c>01 3A 81 14 82 12 03 04 svc 04 04 prop 05 04 value</c>. The reply
    /// nests under tags <c>0x81</c> and <c>0x82</c>, not the <c>1</c>/<c>2</c> the request uses. Returns
    /// the service, property and value when both addresses match the request exactly.
    /// </summary>
    public static bool TryParsePropertyReply(byte[] payload, int expectedService, int expectedProperty, out int value)
    {
        value = 0;
        if (!TryParseTlv(payload, out var outer) || !outer.TryGetValue(0x81, out var middle)
            || !TryParseTlv(middle, out var nested) || !nested.TryGetValue(0x82, out var inner)
            || !TryParseTlv(inner, out var fields)) return false;
        if (!fields.TryGetValue(3, out var service) || service.Length != 4 || ReadInt32(service) != expectedService) return false;
        if (!fields.TryGetValue(4, out var property) || property.Length != 4 || ReadInt32(property) != expectedProperty) return false;
        if (!fields.TryGetValue(5, out var raw) || raw.Length != 4) return false;
        value = ReadInt32(raw);
        return true;
    }

    public static int ReadInt32(ReadOnlySpan<byte> value) =>
        value[0] << 24 | value[1] << 16 | value[2] << 8 | value[3];

    public static byte[] Int32(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
