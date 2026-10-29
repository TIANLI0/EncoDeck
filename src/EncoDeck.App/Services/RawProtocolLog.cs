// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Oppo;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// Writes control-channel frames to the diagnostic log, only while the user has enabled
/// "raw protocol logging". Frames that carry other devices' names/addresses are masked.
/// </summary>
internal sealed class RawProtocolLog(ILogger logger, Func<bool> enabled) : IProtocolTrace
{
    public void OnFrame(FrameDirection direction, OppoFrame frame, DateTimeOffset timestamp)
    {
        if (!enabled()) return;
        logger.LogInformation(
            "{Direction} {Command} seq={Sequence:X2} {Payload}",
            direction == FrameDirection.Sent ? "TX" : "RX",
            OppoCommand.Describe(frame.Command),
            frame.Sequence,
            OppoRedaction.PayloadHex(frame));
    }

    public void OnRawReceived(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
    {
        // Raw chunks are not logged: they could contain unredacted sensitive frames.
    }

    public void OnBrandFrame(FrameDirection direction, string summary, DateTimeOffset timestamp)
    {
        if (!enabled()) return;
        logger.LogInformation("{Direction} {Frame}", direction == FrameDirection.Sent ? "TX" : "RX", summary);
    }
}
