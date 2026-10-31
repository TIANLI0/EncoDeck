// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.A2dpCodec;

namespace EncoDeck.Tests.Platform;

public sealed class WindowsA2dpCodecLogTests
{
    [Theory]
    [InlineData(0x00, 0, 0, "SBC")]
    [InlineData(0x02, 0, 0, "AAC")]
    [InlineData(0xFF, 0x004F, 0x0001, "aptX")]
    [InlineData(0xFF, 0x00D7, 0x0024, "aptX HD")]
    [InlineData(0xFF, 0x000A, 0x00AD, "aptX Adaptive")]
    [InlineData(0xFF, 0x012D, 0x00AA, "LDAC")]
    [InlineData(0xFF, 0x053A, 0x484C, "LHDC")]
    [InlineData(0xFF, 0x1234, 0x0001, null)]
    [InlineData(0x04, 0, 0, null)]
    public void NamesTheCodecIds(ulong standard, ulong vendor, ulong vendorCodec, string? expected) =>
        Assert.Equal(expected, WindowsA2dpCodecLog.CodecName(standard, vendor, vendorCodec));

    [Fact]
    public void KeepsTheLatestStreamPerDevice()
    {
        var at = new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
        var latest = WindowsA2dpCodecLog.Latest(
        [
            new(1, "AAC", at.AddMinutes(5)),
            new(1, "LHDC", at),
            new(2, "SBC", at),
        ]);
        Assert.Equal("AAC", latest[1].Codec);
        Assert.Equal("SBC", latest[2].Codec);
    }
}
