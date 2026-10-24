// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Brands.DokiDoki;

namespace EncoDeck.Tests.DokiDoki;

public sealed class DokiDokiCommandsTests
{
    private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", string.Empty));

    [Theory]
    [InlineData("01 02 00 03 08 14 E4")]
    [InlineData("02 02 00 03 0B 37 BE")]
    [InlineData("02 02 00 03 0E 37 BB")]
    [InlineData("07 02 00 03 0B 64 91")]
    public void EncodeAction_Meta2MatchesYilia(string expected)
    {
        byte[] wire = Hex(expected);
        var channel = wire[4] switch
        {
            DokiDokiConstants.Meta2VibrationCommand => DokiDokiChannel.Vibration,
            DokiDokiConstants.Meta2LinearCommand => DokiDokiChannel.Linear,
            DokiDokiConstants.Meta2RotaryCommand => DokiDokiChannel.Rotary,
            _ => throw new InvalidOperationException(),
        };

        Assert.Equal(wire, DokiDokiCommands.EncodeAction(wire[0], DokiDokiFamily.Meta2, channel, wire[5]));
    }

    [Theory]
    [InlineData("01 02 00 03 0C 04 F0")]
    [InlineData("02 02 00 03 09 07 F0")]
    public void EncodeAction_Bh2ProMatchesCapturedFrames(string expected)
    {
        byte[] wire = Hex(expected);
        var channel = wire[4] == DokiDokiConstants.Bh2ProLinearCommand
            ? DokiDokiChannel.Linear
            : DokiDokiChannel.Vibration;

        Assert.Equal(wire, DokiDokiCommands.EncodeAction(wire[0], DokiDokiFamily.Bh2Pro, channel, wire[5]));
    }

    [Fact]
    public void EncodeHeartbeat_MatchesYiliaLayout()
    {
        Assert.Equal(Hex("03 02 00 03 EF 01 10"), DokiDokiCommands.EncodeHeartbeat(3));
    }

    [Fact]
    public void EncodeFrame_MatchesYiliaChecksumAndHeader()
    {
        Assert.Equal(
            Hex("07 02 00 03 0B 64 91"),
            DokiDokiCommands.EncodeFrame(7, DokiDokiConstants.ApplicationRequestCommand, [0x0B, 0x64]));
        Assert.Equal(0xE4, DokiDokiCommands.CalculateChecksum([0x08, 0x14]));
        Assert.Equal(0x00, DokiDokiCommands.CalculateChecksum([0x00]));
        Assert.Equal(0xFF, DokiDokiCommands.CalculateChecksum([0x01]));
    }

    [Theory]
    [InlineData(-100, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(55, 55)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    public void EncodeAction_ClampsStrength(int input, byte expected)
    {
        var frame = DokiDokiCommands.EncodeAction(1, DokiDokiFamily.Meta2, DokiDokiChannel.Vibration, input);
        Assert.Equal(expected, frame[5]);
    }

    [Fact]
    public void SupportedChannels_MatchFamilyProfiles()
    {
        Assert.Equal(
            [DokiDokiChannel.Vibration, DokiDokiChannel.Linear, DokiDokiChannel.Rotary],
            DokiDokiCommands.SupportedChannels(DokiDokiFamily.Meta2));
        Assert.Equal(
            [DokiDokiChannel.Linear, DokiDokiChannel.Vibration],
            DokiDokiCommands.SupportedChannels(DokiDokiFamily.Bh2Pro));
        Assert.True(DokiDokiCommands.Supports(DokiDokiFamily.Meta2, DokiDokiChannel.Rotary));
        Assert.False(DokiDokiCommands.Supports(DokiDokiFamily.Bh2Pro, DokiDokiChannel.Rotary));
    }

    [Fact]
    public void EncodeAction_RejectsBh2ProRotary()
    {
        Assert.Throws<NotSupportedException>(
            () => DokiDokiCommands.EncodeAction(1, DokiDokiFamily.Bh2Pro, DokiDokiChannel.Rotary, 20));
    }

    [Fact]
    public void EncodeStopAll_Meta2UsesReferenceOrderAndWrapsIds()
    {
        var frames = DokiDokiCommands.EncodeStopAll(14, DokiDokiFamily.Meta2);

        Assert.Equal(3, frames.Count);
        Assert.Equal(Hex("0E 02 00 03 0B 00 F5"), frames[0]);
        Assert.Equal(Hex("0F 02 00 03 0E 00 F2"), frames[1]);
        Assert.Equal(Hex("01 02 00 03 08 00 F8"), frames[2]);
    }

    [Fact]
    public void EncodeStopAll_Bh2ProUsesSingleCapturedCommand()
    {
        Assert.Equal(
            [Hex("06 02 00 03 FF 00 01")],
            DokiDokiCommands.EncodeStopAll(6, DokiDokiFamily.Bh2Pro));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void Encode_RejectsOutOfRangeMessageId(int messageId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DokiDokiCommands.EncodeHeartbeat(messageId));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DokiDokiCommands.EncodeStopAll(messageId, DokiDokiFamily.Meta2));
    }

    [Theory]
    [InlineData("DK-META2", DokiDokiFamily.Meta2)]
    [InlineData("tf-meta2", DokiDokiFamily.Meta2)]
    [InlineData("  DK-META2\0\0 ", DokiDokiFamily.Meta2)]
    [InlineData("DK-BH2PRO", DokiDokiFamily.Bh2Pro)]
    [InlineData("TF-BH2PRO", DokiDokiFamily.Bh2Pro)]
    public void DetectFamily_MatchesYiliaProfileAliases(string name, DokiDokiFamily expected)
    {
        Assert.Equal(expected, DokiDokiNames.DetectFamily(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SOMETHING")]
    [InlineData("DK-META2-PLUS")]
    [InlineData("TRYFUN-META2")]
    public void DetectFamily_RejectsUnknownProfileNames(string? name)
    {
        Assert.Null(DokiDokiNames.DetectFamily(name));
    }

    [Theory]
    [InlineData("DK-META2")]
    [InlineData("TF-X")]
    [InlineData("TRYFUN-1")]
    [InlineData("  tryfun-1\0")]
    public void IsSupportedDeviceName_AcceptsKnownPrefixes(string name)
    {
        Assert.True(DokiDokiNames.IsSupportedDeviceName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ACME")]
    [InlineData("TRYFUN")]
    public void IsSupportedDeviceName_RejectsUnknownPrefixes(string? name)
    {
        Assert.False(DokiDokiNames.IsSupportedDeviceName(name));
    }

    [Fact]
    public void Constants_UseFullBluetoothBaseUuids()
    {
        Assert.Equal(Guid.Parse("0000ffac-0000-1000-8000-00805f9b34fb"), DokiDokiConstants.ServiceUuid);
        Assert.Equal(Guid.Parse("0000ffb5-0000-1000-8000-00805f9b34fb"), DokiDokiConstants.WriteWithResponseCharacteristicUuid);
        Assert.Equal(Guid.Parse("0000ffb7-0000-1000-8000-00805f9b34fb"), DokiDokiConstants.WriteWithoutResponseCharacteristicUuid);
        Assert.Equal(Guid.Parse("0000ffb8-0000-1000-8000-00805f9b34fb"), DokiDokiConstants.NotifyCharacteristicUuid);
        Assert.Equal(DokiDokiConstants.WriteWithoutResponseCharacteristicUuid, DokiDokiConstants.ActionWriteCharacteristicUuid);
        Assert.Equal(DokiDokiConstants.WriteWithResponseCharacteristicUuid, DokiDokiConstants.ActionFallbackWriteCharacteristicUuid);
        Assert.Equal(DokiDokiConstants.WriteWithResponseCharacteristicUuid, DokiDokiConstants.HeartbeatWriteCharacteristicUuid);
        Assert.Equal(DokiDokiConstants.WriteWithoutResponseCharacteristicUuid, DokiDokiConstants.HeartbeatFallbackWriteCharacteristicUuid);
    }
}
