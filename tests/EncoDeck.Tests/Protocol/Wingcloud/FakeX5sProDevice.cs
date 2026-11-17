// SPDX-License-Identifier: GPL-3.0-only
// Every byte below is a capture from an HONOR CHOICE Earbuds X5s Pro (USFY, firmware 1.1.64.2), via the
// independent Android app "我的X5s Pro" (https://github.com/shui-tiao/my-x5s-pro, GPL-3.0).
using EncoDeck.Protocol.Brands.Wingcloud;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;

namespace EncoDeck.Tests.Protocol.Wingcloud;

/// <summary>
/// Emulates the X5s Pro on top of <see cref="FakeByteChannel"/>. It answers the <c>2B</c> service
/// commands, serves the <c>01 39</c> / <c>01 3A</c> property channel from a table (unlike the HUAWEI
/// earbuds it reports <c>7F 04 00 01 86 A0</c> for writes whether or not they are applied), and stays
/// silent for an address it does not have.
/// </summary>
internal sealed class FakeX5sProDevice : IDuplexByteChannel
{
    public static readonly byte[] Ack = [0x7F, 0x04, 0x00, 0x01, 0x86, 0xA0];

    /// <summary>Both parts report their level, the case does not; TLV 3 is the constant <c>64 64 00</c>.</summary>
    public static readonly byte[] InfoReply =
        [0x01, 0x00, 0x02, 0x03, 0x50, 0x4B, 0x00, 0x03, 0x03, 0x64, 0x64, 0x00];

    /// <summary>The write acknowledgement of <c>2B 04</c> / <c>2B 18</c>: no state, just a status byte.</summary>
    public static readonly byte[] StatusAck = [0x02, 0x01, 0x00];

    /// <summary><c>2B 0A</c>: TLV 1 serial, 2 zeros, 3 code, 4 sku, 5 prodId, 6 version (captured shape).</summary>
    public static readonly byte[] FirmwareReply =
    [
        0x01, 0x40, .. System.Text.Encoding.ASCII.GetBytes(
            "BE8899448D750E5853A778FC13853D2249BEFBD0336A46587672C0576D4E5724"),
        0x02, 0x04, 0x00, 0x00, 0x00, 0x00,
        0x03, 0x03, 0x30, 0x38, 0x32,
        0x04, 0x03, 0x35, 0x30, 0x31,
        0x05, 0x04, 0x55, 0x53, 0x46, 0x59,
        0x06, 0x05, 0x31, 0x2E, 0x30, 0x2E, 0x30,
    ];

    private readonly WingcloudFrameCodec _codec = new();

    public FakeByteChannel Inner { get; } = new();

    public bool Silent { get; set; }
    public bool AncSilent { get; set; }
    public bool IgnorePropertyWrites { get; set; }
    public bool AncWrites { get; set; } = true;

    /// <summary>service &lt;&lt; 8 | property → value, for EQ, LDAC, spatial audio and the gestures.</summary>
    public Dictionary<int, int> Properties { get; } = new()
    {
        [3 << 8 | 5] = 0,
        [6 << 8 | 0] = 0,
        [6 << 8 | 1] = 0,
        [4 << 8 | 2] = 0,
        [4 << 8 | 3] = 0,
        [4 << 8 | 5] = 0,
        [4 << 8 | 6] = 0,
        [4 << 8 | 1] = 4,
    };

    /// <summary><c>2B 2A</c> answer: <c>[level][mode]</c>.</summary>
    public byte[] Anc { get; set; } = [0x00, 0x00];

    public byte[] Cycle { get; set; } = [0x02, 0x02];

    public List<WingcloudFrame> Received { get; } = [];

    public string Description => "fake x5s pro";

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await Inner.WriteAsync(data, cancellationToken);
        foreach (var frame in _codec.Feed(data.Span))
        {
            lock (Received) Received.Add(frame);
            if (Silent) continue;
            switch (frame.Command)
            {
                // Handshake: the earbuds answer the host's 0x40 declarations with their own 0x60 values.
                case 0x0127:
                    Reply(frame.Command, InfoReply);
                    break;
                case 0x013E:
                    Reply(frame.Command, [0x01, 0x04, 0x00, 0x00, 0x00, 0x82]);
                    break;
                case 0x0107:
                    // Shape captured from the earbuds; 7 = firmware, 9 = serial, 10/11 = model text.
                    Reply(frame.Command,
                    [
                        0x01, 0x08, 0x35, 0x2E, 0x33, 0x2E, 0x32, 0x2E, 0x32, 0x37,
                        0x03, 0x0B, 0x35, 0x30, 0x31, 0x31, 0x35, 0x5F, 0x33, 0x30, 0x2F, 0x34, 0x30,
                        0x05, 0x06, 0x31, 0x4C, 0xE4, 0x9E, 0x00, 0x00,
                        0x07, 0x08, 0x31, 0x2E, 0x31, 0x2E, 0x36, 0x34, 0x2E, 0x32,
                        0x09, 0x10, 0x35, 0x35, 0x30, 0x34, 0x41, 0x41, 0x4B, 0x43, 0x32, 0x34, 0x34, 0x51, 0x30, 0x34, 0x33, 0x37,
                        0x0A, 0x0F, 0x42, 0x54, 0x56, 0x2D, 0x4D, 0x45, 0x31, 0x30, 0x2D, 0x30, 0x30, 0x55, 0x53, 0x46, 0x59,
                        0x0C, 0x00,
                        0x11, 0x08, 0x42, 0x54, 0x56, 0x2D, 0x4D, 0x45, 0x31, 0x30,
                        0x19, 0x01, 0x00,
                    ]);
                    break;
                case 0x2B39:
                    // This firmware never answers 2B 39; the levels come in the 01 27 envelope.
                    break;
                case 0x2B0A:
                    Reply(frame.Command, FirmwareReply);
                    break;
                case 0x2B2A:
                    if (!AncSilent) Reply(frame.Command, [0x01, 0x02, .. Anc]);
                    break;
                case 0x2B04:
                    // TLV payload = the host's command (mode first); a bare status ack carries no state.
                    if (WingcloudFrameCodec.TryParseTlv(frame.Payload, out var anc) && anc.TryGetValue(1, out var ancValue) && ancValue is [var mode, var level])
                    {
                        if (AncWrites) Anc = [mode == 1 ? level : (byte)0, mode];
                        if (!AncSilent) Reply(frame.Command, StatusAck);
                    }
                    break;
                case 0x2B19:
                    // The read answers tag 1 = right, tag 2 = left, the reverse of the write.
                    Reply(frame.Command, [0x01, 0x01, Cycle[1], 0x02, 0x01, Cycle[0]]);
                    break;
                case 0x2B18:
                    if (frame.Payload is [1, 1, var left, 2, 1, var right]) Cycle = [left, right];
                    Reply(frame.Command, StatusAck);
                    break;
                case 0x0139:
                    ApplyWrite(frame.Payload);
                    Reply(frame.Command, Ack);
                    break;
                case 0x013A:
                    if (ReadAddress(frame.Payload) is not { } address)
                    {
                        // The handshake's 0x013A is the version negotiation, answered with a status.
                        Reply(frame.Command, [0x7F, 0x04, 0x00, 0x01]);
                    }
                    else if (Properties.TryGetValue(address, out int value))
                    {
                        Reply(frame.Command, [.. WingcloudFrameCodec.Tlv(0x81, WingcloudFrameCodec.Tlv(0x82,
                            [.. WingcloudFrameCodec.Tlv(3, WingcloudFrameCodec.Int32(address >> 8)),
                             .. WingcloudFrameCodec.Tlv(4, WingcloudFrameCodec.Int32(address & 0xFF)),
                             .. WingcloudFrameCodec.Tlv(5, WingcloudFrameCodec.Int32(value))]))]);
                    }
                    break;
            }
        }
    }

    private void ApplyWrite(byte[] payload)
    {
        if (!WingcloudFrameCodec.TryParseTlv(payload, out var fields)
            || !fields.TryGetValue(1, out var service) || service.Length != 4
            || !fields.TryGetValue(2, out var property) || property.Length != 4
            || !fields.TryGetValue(3, out var raw) || raw.Length != 4) return;
        int address = WingcloudFrameCodec.ReadInt32(service) << 8 | WingcloudFrameCodec.ReadInt32(property);
        if (!IgnorePropertyWrites && Properties.ContainsKey(address)) Properties[address] = WingcloudFrameCodec.ReadInt32(raw);
    }

    private static int? ReadAddress(byte[] payload)
    {
        // The request nests under 1 → 2 → {3 service, 4 property, 5 empty}, unlike the 81/82 reply.
        if (!WingcloudFrameCodec.TryParseTlv(payload, out var outer) || !outer.TryGetValue(1, out var middle)
            || !WingcloudFrameCodec.TryParseTlv(middle, out var nested) || !nested.TryGetValue(2, out var innerRaw)
            || !WingcloudFrameCodec.TryParseTlv(innerRaw, out var inner)) return null;
        return inner.TryGetValue(3, out var service) && service.Length == 4 && inner.TryGetValue(4, out var property) && property.Length == 4
            ? WingcloudFrameCodec.ReadInt32(service) << 8 | WingcloudFrameCodec.ReadInt32(property)
            : null;
    }

    private static byte[] Tetra(byte tag, string text) =>
        WingcloudFrameCodec.Tlv(tag, [.. System.Text.Encoding.ASCII.GetBytes(text)]);

    public void Reply(ushort command, byte[] payload) => Inner.Receive(_codec.Encode(command, payload));

    /// <summary>Unsolicited <c>2B 04</c> report (mode first), as sent when the earbuds change the mode themselves.</summary>
    public void PushNoise(byte mode, byte level) => Reply(0x2B04, [1, 2, mode, level, 2, 1, 0]);

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => Inner.ReadAsync(buffer, cancellationToken);

    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}
