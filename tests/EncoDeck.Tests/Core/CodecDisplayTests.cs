// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;

namespace EncoDeck.Tests.Core;

public class CodecDisplayTests
{
    private static readonly DeviceIdentity Device = new("codec-display", "Earbuds", new BluetoothAddress(0x112233445566));
    private static Observed<T> O<T>(T value, StateSource source = StateSource.QueryResponse) => Observed.From(value, DateTimeOffset.UnixEpoch, source);
    private static DeviceState Ready => DeviceState.Empty with { Device = Device, Connection = DeviceConnectionState.ControlReady };

    [Theory]
    [InlineData(AudioCodec.Sbc, "SBC")]
    [InlineData(AudioCodec.Aac, "AAC")]
    [InlineData(AudioCodec.Ldac, "LDAC")]
    [InlineData(AudioCodec.Lhdc, "LHDC")]
    [InlineData(AudioCodec.AptX, "aptX")]
    [InlineData(AudioCodec.AptXHd, "aptX HD")]
    [InlineData(AudioCodec.AptXAdaptive, "aptX Adaptive")]
    public void ConfirmedReport_IsLabeledAsReport_NotAsVerifiedStream(AudioCodec value, string name)
    {
        Assert.Equal(new CodecDisplay(CodecDisplayKind.DeviceReport, name), CodecDisplay.Resolve(Ready with { Codec = O(value) }));
    }

    [Theory]
    [InlineData(StateSource.Cache)]
    [InlineData(StateSource.Optimistic)]
    public void UnconfirmedData_IsNotShownAsCurrent(StateSource source)
    {
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(Ready with { Codec = O(AudioCodec.Aac, source), ReportedCodecId = O((byte)2, source) }).Kind);
    }

    [Theory]
    [InlineData(DeviceConnectionState.Disconnected)]
    [InlineData(DeviceConnectionState.PairedNotConnected)]
    [InlineData(DeviceConnectionState.AudioConnected)]
    [InlineData(DeviceConnectionState.ControlConnecting)]
    [InlineData(DeviceConnectionState.Reconnecting)]
    [InlineData(DeviceConnectionState.ControlUnavailable)]
    public void NoReadyControl_HidesOldDeviceReport(DeviceConnectionState connection)
    {
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(Ready with { Connection = connection, Codec = O(AudioCodec.Aac) }).Kind);
    }

    [Theory]
    [InlineData(0x06C810u)]
    [InlineData(0x06E810u)]
    public void Air5s_AnomalousReportIsUnknown_EvenWithAnOldAcceptedCodec(uint productId)
    {
        var state = Ready with { ProductId = productId, Codec = O(AudioCodec.Aac), ReportedCodecId = O((byte)7) };
        Assert.Equal(CodecDisplayKind.UnknownDeviceReport, CodecDisplay.Resolve(state).Kind);
        Assert.Equal(CodecDisplayKind.UnknownDeviceReport, CodecDisplay.Resolve(state with { Codec = O(AudioCodec.Lhdc), ReportedCodecId = null }).Kind);
    }

    [Fact]
    public void UnknownRawId_AndNoReport_AreDistinct()
    {
        Assert.Equal(CodecDisplayKind.UnknownDeviceReport, CodecDisplay.Resolve(Ready with { ReportedCodecId = O((byte)99) }).Kind);
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(Ready).Kind);
    }

    [Fact]
    public void BridgeStream_WinsOverDeviceAndLeEvidence_EvenWithoutWindowsControl()
    {
        var state = Ready with { Codec = O(AudioCodec.Lhdc), IsLeAudio = true, Bridge = new(true, " AAC ", 256, 48000, "Air5s") };
        var expected = new CodecDisplay(CodecDisplayKind.BridgeStream, "AAC");
        Assert.Equal(expected, CodecDisplay.Resolve(state));
        Assert.Equal(expected, CodecDisplay.Resolve(state with { Connection = DeviceConnectionState.Disconnected }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void BridgeMissingCodec_NeverFallsBackToTheDeviceOrLe(string? codec)
    {
        Assert.Equal(CodecDisplayKind.UnknownBridgeStream, CodecDisplay.Resolve(Ready with
        {
            Bridge = new(true, codec, 256, 48000, "Air5s"), Codec = O(AudioCodec.Aac), IsLeAudio = true,
        }).Kind);
    }

    [Fact]
    public void IdleBridge_DoesNotClaimItsConfiguredCodecIsStreaming()
    {
        var state = Ready with { Bridge = new(false, "LDAC", 0, 0, "saved"), Codec = O(AudioCodec.Aac) };
        Assert.Equal(new CodecDisplay(CodecDisplayKind.DeviceReport, "AAC"), CodecDisplay.Resolve(state));
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(state with { Connection = DeviceConnectionState.Disconnected }).Kind);
    }

    [Fact]
    public void LeAclConnection_IsALink_NotVerifiedLc3()
    {
        var state = Ready with { IsLeAudio = true, Codec = O(AudioCodec.Aac) };
        Assert.Equal(new CodecDisplay(CodecDisplayKind.LeLink), CodecDisplay.Resolve(state));
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(state with { Connection = DeviceConnectionState.Disconnected }).Kind);
    }

    [Fact]
    public void WindowsLog_ReplacesTheDeviceReport()
    {
        var state = Ready with { Codec = O(AudioCodec.Ldac), WindowsCodecLogged = true, WindowsCodec = "AAC" };
        Assert.Equal(new CodecDisplay(CodecDisplayKind.WindowsStream, "AAC"), CodecDisplay.Resolve(state));
        // Windows' log does not need EncoDeck's control channel.
        Assert.Equal(CodecDisplayKind.WindowsStream, CodecDisplay.Resolve(state with { Connection = DeviceConnectionState.AudioConnected }).Kind);
        // Nothing logged for this device yet: no badge rather than the earbuds' report.
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(state with { WindowsCodec = null }).Kind);
        // A streaming bridge and an LE link still come first.
        Assert.Equal(CodecDisplayKind.BridgeStream, CodecDisplay.Resolve(state with { Bridge = new(true, "LDAC", 0, 0, "p") }).Kind);
        Assert.Equal(CodecDisplayKind.LeLink, CodecDisplay.Resolve(state with { IsLeAudio = true }).Kind);
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(state with { Connection = DeviceConnectionState.Disconnected }).Kind);
    }

    [Fact]
    public void NoSelectedDevice_OrUsbReceiver_DoesNotGetABluetoothBadge()
    {
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(Ready with { Device = null, Bridge = new(true, "AAC", 0, 0, "old") }).Kind);
        Assert.Equal(CodecDisplayKind.None, CodecDisplay.Resolve(Ready with { Device = Device with { Transport = DeviceTransport.Usb }, Codec = O(AudioCodec.Aac) }).Kind);
    }
}
