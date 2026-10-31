// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;

namespace EncoDeck.Core.State;

public enum CodecDisplayKind
{
    None,
    BridgeStream,
    UnknownBridgeStream,
    LeLink,
    /// <summary>The codec Windows logged for this device's A2DP stream.</summary>
    WindowsStream,
    /// <summary>A Classic link while Windows' codec log is off: only "A2DP" is known; the log can be turned on.</summary>
    A2dpLogOff,
    /// <summary>A Classic link with the log on, but Windows has not logged a stream to this device yet.</summary>
    A2dpNotLogged,
}

/// <summary>
/// One source for the audio badge on every surface. Codecs come only from the PC side (A2DP Bridge or Windows' own log),
/// never from the earbuds' report, which does not prove what the PC streams.
/// </summary>
public sealed record CodecDisplay(CodecDisplayKind Kind, string? Codec = null)
{
    public static CodecDisplay Resolve(DeviceState state)
    {
        if (state.Device?.Transport != DeviceTransport.Bluetooth) return new(CodecDisplayKind.None);

        // BridgeDeviceMatch attaches this stream only after matching its Bluetooth address.
        // A streaming bridge with a missing codec must not fall back to an unrelated old record.
        if (state.Bridge is { IsStreaming: true } bridge)
            return string.IsNullOrWhiteSpace(bridge.Codec) ? new(CodecDisplayKind.UnknownBridgeStream)
                : new(CodecDisplayKind.BridgeStream, bridge.Codec.Trim());

        if (!state.Connection.IsLinkUp()) return new(CodecDisplayKind.None);
        if (state.IsLeAudio) return new(CodecDisplayKind.LeLink);
        // Linked only through the bridge's own adapter: Windows has no A2DP link to this device.
        if (state.LinkViaBridge) return new(CodecDisplayKind.None);
        if (!state.WindowsCodecLogged) return new(CodecDisplayKind.A2dpLogOff);
        return state.WindowsCodec is { Length: > 0 } codec ? new(CodecDisplayKind.WindowsStream, codec) : new(CodecDisplayKind.A2dpNotLogged);
    }
}
