// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;

namespace EncoDeck.Core.State;

public enum CodecDisplayKind
{
    None,
    DeviceReport,
    UnknownDeviceReport,
    BridgeStream,
    UnknownBridgeStream,
    LeLink,
    WindowsStream,
}

/// <summary>One source for the audio badge on every surface; a device report is not a verified PC stream.</summary>
public sealed record CodecDisplay(CodecDisplayKind Kind, string? Codec = null)
{
    public static CodecDisplay Resolve(DeviceState state)
    {
        if (state.Device?.Transport != DeviceTransport.Bluetooth) return new(CodecDisplayKind.None);

        // BridgeDeviceMatch attaches this stream only after matching its Bluetooth address.
        // A streaming bridge with a missing codec must not fall back to an unrelated old report.
        if (state.Bridge is { IsStreaming: true } bridge)
            return string.IsNullOrWhiteSpace(bridge.Codec) ? new(CodecDisplayKind.UnknownBridgeStream)
                : new(CodecDisplayKind.BridgeStream, bridge.Codec.Trim());

        if (!state.Connection.IsLinkUp()) return new(CodecDisplayKind.None);
        if (state.IsLeAudio) return new(CodecDisplayKind.LeLink);
        // Windows' own log of its A2DP streams, once authorized, replaces the earbuds' report (also while none is logged).
        if (state.WindowsCodecLogged)
            return state.WindowsCodec is { Length: > 0 } codec ? new(CodecDisplayKind.WindowsStream, codec) : new(CodecDisplayKind.None);
        if (state.Connection != DeviceConnectionState.ControlReady) return new(CodecDisplayKind.None);

        bool air5s = state.ProductId is 0x06C810 or 0x06E810;
        if (state.ReportedCodecId is { IsConfirmed: true } raw && air5s && raw.Value is not (1 or 2))
            return new(CodecDisplayKind.UnknownDeviceReport);
        if (state.Codec is { IsConfirmed: true } report)
        {
            if (air5s && report.Value is not (AudioCodec.Sbc or AudioCodec.Aac))
                return new(CodecDisplayKind.UnknownDeviceReport);
            string? name = report.Value switch
            {
                AudioCodec.Sbc => "SBC",
                AudioCodec.Aac => "AAC",
                AudioCodec.Ldac => "LDAC",
                AudioCodec.AptX => "aptX",
                AudioCodec.AptXHd => "aptX HD",
                AudioCodec.AptXAdaptive => "aptX Adaptive",
                AudioCodec.Lhdc => "LHDC",
                _ => null,
            };
            return name is null ? new(CodecDisplayKind.UnknownDeviceReport) : new(CodecDisplayKind.DeviceReport, name);
        }
        return state.ReportedCodecId is { IsConfirmed: true }
            ? new(CodecDisplayKind.UnknownDeviceReport) : new(CodecDisplayKind.None);
    }
}
