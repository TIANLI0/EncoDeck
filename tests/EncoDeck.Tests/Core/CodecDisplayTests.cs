// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;

namespace EncoDeck.Tests.Core;

public class CodecDisplayTests
{
    private static readonly DeviceIdentity Device = new("codec-display", "Earbuds", new BluetoothAddress(0x112233445566));
    private static DeviceState Ready => DeviceState.Empty with { Device = Device, Connection = DeviceConnectionState.ControlReady };
    private static DeviceState Logged => Ready with { WindowsCodecLogged = true, WindowsCodec = "AAC" };

    [Theory]
    [InlineData(DeviceConnectionState.AudioConnected)]
    [InlineData(DeviceConnectionState.ControlConnecting)]
    [InlineData(DeviceConnectionState.ControlReady)]
    [InlineData(DeviceConnectionState.ControlUnavailable)]
    [InlineData(DeviceConnectionState.Reconnecting)]
    public void WindowsLog_NamesTheCodec_WithOrWithoutEncoDecksControl(DeviceConnectionState connection) =>
        Assert.Equal(new CodecDisplay(CodecDisplayKind.WindowsStream, "AAC"), CodecDisplay.Resolve(Logged with { Connection = connection }));

    [Fact]
    public void ClassicLink_WithoutTheLog_IsPlainA2dp() =>
        Assert.Equal(new CodecDisplay(CodecDisplayKind.A2dpLogOff), CodecDisplay.Resolve(Ready));

    [Fact]
    public void LogOn_NothingLoggedForThisDevice_IsPlainA2dp() =>
        Assert.Equal(new CodecDisplay(CodecDisplayKind.A2dpNotLogged), CodecDisplay.Resolve(Logged with { WindowsCodec = null }));

    [Theory]
    [InlineData(DeviceConnectionState.Disconnected)]
    [InlineData(DeviceConnectionState.PairedNotConnected)]
    [InlineData(DeviceConnectionState.NotDetected)]
    public void NoLink_NoBadge(DeviceConnectionState connection)
    {
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(Logged with { Connection = connection }).Kind);
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(Ready with { Connection = connection }).Kind);
    }

    [Fact]
    public void LinkedOnlyThroughTheBridgesAdapter_IsNotAWindowsA2dpLink() =>
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(Logged with { LinkViaBridge = true }).Kind);

    [Fact]
    public void BridgeStream_WinsOverWindowsAndLe_EvenWithoutWindowsLink()
    {
        var state = Logged with { IsLeAudio = true, Bridge = new(true, " LDAC ", 990, 96000, "Air5s") };
        var expected = new CodecDisplay(CodecDisplayKind.BridgeStream, "LDAC");
        Assert.Equal(expected, CodecDisplay.Resolve(state));
        Assert.Equal(expected, CodecDisplay.Resolve(state with { Connection = DeviceConnectionState.Disconnected }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void BridgeMissingCodec_NeverFallsBackToWindowsOrLe(string? codec) =>
        Assert.Equal(CodecDisplayKind.UnknownBridgeStream, CodecDisplay.Resolve(Logged with
        {
            Bridge = new(true, codec, 256, 48000, "Air5s"), IsLeAudio = true,
        }).Kind);

    [Fact]
    public void IdleBridge_DoesNotClaimItsConfiguredCodecIsStreaming() =>
        Assert.Equal(new CodecDisplay(CodecDisplayKind.WindowsStream, "AAC"), CodecDisplay.Resolve(Logged with { Bridge = new(false, "LDAC", 0, 0, "saved") }));

    [Fact]
    public void LeLink_IsALink_NotVerifiedLc3()
    {
        var state = Logged with { IsLeAudio = true };
        Assert.Equal(new CodecDisplay(CodecDisplayKind.LeLink), CodecDisplay.Resolve(state));
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(state with { Connection = DeviceConnectionState.Disconnected }).Kind);
    }

    [Fact]
    public void NoSelectedDevice_OrUsbReceiver_DoesNotGetABluetoothBadge()
    {
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(Ready with { Device = null, Bridge = new(true, "AAC", 0, 0, "old") }).Kind);
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(Logged with { Device = Device with { Transport = DeviceTransport.Usb } }).Kind);
    }
}
