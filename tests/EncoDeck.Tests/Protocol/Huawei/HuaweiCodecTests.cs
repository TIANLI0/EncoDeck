// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Brands.Huawei;

namespace EncoDeck.Tests.Protocol.Huawei;

public class HuaweiCodecTests
{
    // Captured frames from the reference projects (HuaweiPods captures, mbb_codec.py, HeyBuds fixtures).
    public static TheoryData<ushort, string, string> LegacyFixtures => new()
    {
        { 0x0108, "010002000300", "5A0009000108010002000300FBB9" },
        { 0x2B2A, "0100", "5A0005002B2A0100427E" },
        { 0x2B10, "010100", "5A0006002B10010100B977" },
        { 0x2B04, "01020101", "5A0007002B0401020101F13D" },
        // HuaweiPods FreeClip 2 spatial audio: state query, then off / head tracking / fixed writes.
        { 0x2BB4, "01011802000300", "5A000A002BB4010118020003009B3F" },
        { 0x2BB4, "010118020100", "5A0009002BB401011802010060ED" },
        { 0x2BB4, "010118020101", "5A0009002BB401011802010170CC" },
        { 0x2BB4, "010118020102", "5A0009002BB401011802010240AF" },
    };

    [Theory]
    [MemberData(nameof(LegacyFixtures))]
    public void Legacy_EncodesCapturedFrames(ushort command, string payload, string wire)
    {
        var codec = new LegacyHuaweiFrameCodec();
        Assert.Equal(wire, Convert.ToHexString(codec.Encode(command, Convert.FromHexString(payload))));
        var frame = Assert.Single(codec.Feed(Convert.FromHexString(wire)));
        Assert.Equal(command, frame.Command);
        Assert.Equal(payload, Convert.ToHexString(frame.Payload));
    }

    [Fact]
    public void Mbb_EncodesReferenceVector_AndRoundTrips()
    {
        var codec = new MbbFrameCodec();
        byte[] payload = [0x01, 0x01, 0x05, 0x02, 0x02, 0x10, 0x20];
        Assert.Equal("5A000A002B49010105020210 20B9D4".Replace(" ", ""), Convert.ToHexString(codec.Encode(0x2B49, payload)));
        var frame = Assert.Single(codec.Feed(codec.Encode(0x2B49, payload)));
        Assert.Equal((ushort)0x2B49, frame.Command);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    public void Crc16_IsXmodem()
    {
        Assert.Equal(0x31C3, HuaweiWire.Crc16("123456789"u8));
    }

    [Fact]
    public void LongFrames_UseATwoByteBigEndianLength()
    {
        // OpenFreebuds #116: the length is two bytes, big-endian. FreeBuds Pro 5 answers the device-info
        // query with 282 bytes, which a one-byte length truncated.
        var payload = new byte[300];
        var legacy = new LegacyHuaweiFrameCodec().Encode(0x2B49, payload);
        var mbb = new MbbFrameCodec().Encode(0x2B49, payload);
        Assert.Equal([0x5A, 0x01, 0x2F, 0x00], legacy[..4]); // 303 big-endian + zero byte
        Assert.Equal(legacy, mbb);
        Assert.Single(new LegacyHuaweiFrameCodec().Feed(legacy));
        Assert.Single(new MbbFrameCodec().Feed(mbb));
    }

    [Fact]
    public void Legacy_DecodesALongReplyFollowedByAnotherFrame()
    {
        // A device-info style reply of more than 255 bytes, then EZBuds' FreeClip 2 battery capture in the same stream.
        byte[] info = [.. Enumerable.Range(0, 8).SelectMany(i => new byte[] { (byte)(i + 1), 32 }.Concat(Enumerable.Repeat((byte)'A', 32)))];
        var codec = new LegacyHuaweiFrameCodec();
        byte[] stream = [.. codec.Encode(0x0107, info), .. Convert.FromHexString("5a001b000108010160020363604503030000000402140a0502000006010acbfb")];
        Assert.Equal(0x01, stream[1]);

        var frames = new List<HuaweiFrame>();
        for (int i = 0; i < stream.Length; i += 100) frames.AddRange(codec.Feed(stream.AsSpan(i, Math.Min(100, stream.Length - i))));

        Assert.Equal([(ushort)0x0107, (ushort)0x0108], frames.Select(f => f.Command));
        Assert.Equal(info, frames[0].Payload);
    }

    // Community real-device captures: OpenFreebuds test_config.py / test_low_latency.py and vicrodh's
    // test_buds7i.py (write acknowledgements), HuaweiPods FreeBuds 7 tests, OpenFreebuds-Android (5i handshake).
    [Theory]
    [InlineData("5a0009002b107f04000186a0729d", 0x2B10, true)]
    [InlineData("5a0009002b6c7f04000186a0a497", 0x2B6C, true)]
    [InlineData("5a0009002b497f04000186a0b3c6", 0x2B49, true)]
    [InlineData("5A0006002B11010100CFC3", 0x2B11, false)]
    [InlineData("5a0006000a0e020100fb3f", 0x0A0E, false)]
    public void Captures_DecodeAndClassifyAcknowledgements(string hex, int command, bool ack)
    {
        var frame = Assert.Single(new LegacyHuaweiFrameCodec().Feed(Convert.FromHexString(hex)));
        Assert.Equal((ushort)command, frame.Command);
        Assert.True(HuaweiWire.TryParseTlv(frame.Payload, out var fields));
        Assert.Equal(ack, HuaweiWire.IsSuccessAck(fields));
        Assert.False(HuaweiWire.IsError(fields));
    }

    [Theory]
    [InlineData("7F04000186A3")]
    [InlineData("7F0101")]
    public void ResultCodes_OtherThanSuccessAreErrors(string payload)
    {
        Assert.True(HuaweiWire.TryParseTlv(Convert.FromHexString(payload), out var fields));
        Assert.True(HuaweiWire.IsError(fields));
        Assert.False(HuaweiWire.IsSuccessAck(fields));
    }

    [Fact]
    public void Legacy_SkipsAFrameWithANonZeroFourthByte()
    {
        var codec = new LegacyHuaweiFrameCodec();
        var fragment = codec.Encode(0x2B4A, [0x01, 0x00]);
        fragment[3] = 0x01;
        ushort crc = HuaweiWire.Crc16(fragment.AsSpan(0, fragment.Length - 2));
        fragment[^2] = (byte)(crc >> 8);
        fragment[^1] = (byte)crc;
        var good = codec.Encode(0x2B11, [0x01, 0x01, 0x01]);
        Assert.Equal((ushort)0x2B11, Assert.Single(codec.Feed([.. fragment, .. good])).Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Feed_BuffersPartialFrames_AndSkipsGarbage(bool mbb)
    {
        IHuaweiFrameCodec codec = mbb ? new MbbFrameCodec() : new LegacyHuaweiFrameCodec();
        var a = codec.Encode(0x0108, [0x02, 0x03, 0x50, 0x46, 0x20]);
        var b = codec.Encode(0x2B2A, [0x01, 0x02, 0x03, 0x01]);
        byte[] stream = [0x00, 0x5A, 0x7F, .. a, .. b];

        var frames = new List<HuaweiFrame>();
        foreach (var by in stream) frames.AddRange(codec.Feed([by]));

        Assert.Equal([(ushort)0x0108, (ushort)0x2B2A], frames.Select(f => f.Command));
        Assert.Equal([0x01, 0x02, 0x03, 0x01], frames[1].Payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Feed_DropsFrameWithBadCrc(bool mbb)
    {
        IHuaweiFrameCodec codec = mbb ? new MbbFrameCodec() : new LegacyHuaweiFrameCodec();
        var bad = codec.Encode(0x2B2A, [0x01, 0x00]);
        bad[^1] ^= 0xFF;
        var good = codec.Encode(0x2B11, [0x01, 0x01, 0x01]);
        var frame = Assert.Single(codec.Feed([.. bad, .. good]));
        Assert.Equal((ushort)0x2B11, frame.Command);
    }

    [Fact]
    public void Mbb_SkipsFragments()
    {
        var codec = new MbbFrameCodec();
        var fragment = codec.Encode(0x2B4A, [0x01, 0x00]);
        fragment[3] = 0x01;
        ushort crc = HuaweiWire.Crc16(fragment.AsSpan(0, fragment.Length - 2));
        fragment[^2] = (byte)(crc >> 8);
        fragment[^1] = (byte)crc;
        Assert.Empty(codec.Feed(fragment));
        Assert.Equal(1, codec.DroppedFragments);
    }

    [Fact]
    public void ParseTlv_IgnoresTruncatedTail()
    {
        var fields = HuaweiWire.ParseTlv([0x01, 0x02, 0xAA, 0xBB, 0x02, 0x05, 0x01]);
        Assert.Equal([0xAA, 0xBB], Assert.Single(fields).Value);
    }
}
