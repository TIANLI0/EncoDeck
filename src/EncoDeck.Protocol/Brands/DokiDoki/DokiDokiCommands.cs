// SPDX-License-Identifier: GPL-3.0-only
// Ported from Yilia src/chunfeng/protocol.ts (MIT) and manager.ts routing.
namespace EncoDeck.Protocol.Brands.DokiDoki;

/// <summary>
/// Pure DokiDoki command encoder. Action and stop frames prefer
/// <see cref="DokiDokiConstants.WriteWithoutResponseCharacteristicUuid"/> with a
/// <see cref="DokiDokiConstants.WriteWithResponseCharacteristicUuid"/> fallback; heartbeat frames
/// prefer the with-response characteristic and fall back to write-without-response.
/// </summary>
public static class DokiDokiCommands
{
    private static readonly DokiDokiChannel[] Meta2Channels =
        [DokiDokiChannel.Vibration, DokiDokiChannel.Linear, DokiDokiChannel.Rotary];
    private static readonly DokiDokiChannel[] Bh2ProChannels =
        [DokiDokiChannel.Linear, DokiDokiChannel.Vibration];
    private static readonly DokiDokiChannel[] Meta2StopOrder =
        [DokiDokiChannel.Linear, DokiDokiChannel.Rotary, DokiDokiChannel.Vibration];

    /// <summary>Returns the actuator channels supported by <paramref name="family"/>.</summary>
    public static IReadOnlyList<DokiDokiChannel> SupportedChannels(DokiDokiFamily family) => family switch
    {
        DokiDokiFamily.Meta2 => Meta2Channels,
        DokiDokiFamily.Bh2Pro => Bh2ProChannels,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown DokiDoki family."),
    };

    /// <summary>Tests whether a known family supports a channel.</summary>
    public static bool Supports(DokiDokiFamily family, DokiDokiChannel channel) => family switch
    {
        DokiDokiFamily.Meta2 => channel is DokiDokiChannel.Vibration or DokiDokiChannel.Linear or DokiDokiChannel.Rotary,
        DokiDokiFamily.Bh2Pro => channel is DokiDokiChannel.Vibration or DokiDokiChannel.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown DokiDoki family."),
    };

    /// <summary>Builds an action frame, clamping <paramref name="strength"/> to 0–100.</summary>
    public static byte[] EncodeAction(
        int messageId,
        DokiDokiFamily family,
        DokiDokiChannel channel,
        int strength)
    {
        ValidateMessageId(messageId);
        if (!Supports(family, channel))
            throw new NotSupportedException($"{family} does not support the {channel} channel.");

        byte command = (family, channel) switch
        {
            (DokiDokiFamily.Meta2, DokiDokiChannel.Vibration) => DokiDokiConstants.Meta2VibrationCommand,
            (DokiDokiFamily.Meta2, DokiDokiChannel.Linear) => DokiDokiConstants.Meta2LinearCommand,
            (DokiDokiFamily.Meta2, DokiDokiChannel.Rotary) => DokiDokiConstants.Meta2RotaryCommand,
            (DokiDokiFamily.Bh2Pro, DokiDokiChannel.Vibration) => DokiDokiConstants.Bh2ProVibrationCommand,
            (DokiDokiFamily.Bh2Pro, DokiDokiChannel.Linear) => DokiDokiConstants.Bh2ProLinearCommand,
            _ => throw new NotSupportedException($"{family} does not support the {channel} channel."),
        };
        byte level = (byte)Math.Clamp(strength, DokiDokiConstants.MinStrength, DokiDokiConstants.MaxStrength);
        return EncodeCommandFrame(messageId, command, level);
    }

    /// <summary>
    /// Builds the frames needed to stop every actuator. META2 emits linear, rotary, then vibration
    /// frames; BH2PRO emits its single FF 00 command. Message IDs advance and wrap at 15.
    /// </summary>
    public static IReadOnlyList<byte[]> EncodeStopAll(int messageId, DokiDokiFamily family)
    {
        ValidateMessageId(messageId);
        if (family == DokiDokiFamily.Bh2Pro)
            return [EncodeCommandFrame(messageId, DokiDokiConstants.Bh2ProStopCommand, 0)];
        if (family != DokiDokiFamily.Meta2)
            throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown DokiDoki family.");

        var frames = new List<byte[]>(Meta2StopOrder.Length);
        int currentId = messageId;
        foreach (DokiDokiChannel channel in Meta2StopOrder)
        {
            frames.Add(EncodeAction(currentId, family, channel, 0));
            currentId = NextMessageId(currentId);
        }
        return frames;
    }

    /// <summary>Builds the EF 01 heartbeat frame.</summary>
    public static byte[] EncodeHeartbeat(int messageId) =>
        EncodeCommandFrame(messageId, DokiDokiConstants.HeartbeatCommand, 0x01);

    /// <summary>
    /// True for frames built by <see cref="EncodeHeartbeat"/>, which a transport writes to the heartbeat
    /// characteristic; every other frame goes to the action characteristic.
    /// </summary>
    public static bool IsHeartbeatFrame(ReadOnlySpan<byte> frame) =>
        frame.Length == 7
        && frame[1] == DokiDokiConstants.ApplicationRequestCommand
        && frame[3] == 3
        && frame[4] == DokiDokiConstants.HeartbeatCommand;

    /// <summary>The message id that follows <paramref name="messageId"/> (1–15, wrapping).</summary>
    public static int NextMessageId(int messageId) =>
        messageId >= DokiDokiConstants.MaxMessageId ? DokiDokiConstants.MinMessageId : messageId + 1;

    /// <summary>
    /// Builds a frame as [message id][0x02][0x00][payload length + 1][payload][checksum].
    /// The checksum is the two's complement of the payload sum only.
    /// </summary>
    public static byte[] EncodeFrame(int messageId, byte commandType, ReadOnlySpan<byte> payload)
    {
        ValidateMessageId(messageId);
        if (payload.Length > byte.MaxValue - 1)
            throw new ArgumentException("DokiDoki payload is too large.", nameof(payload));

        var frame = new byte[4 + payload.Length + 1];
        frame[0] = (byte)messageId;
        frame[1] = commandType;
        frame[2] = 0x00;
        frame[3] = (byte)(payload.Length + 1);
        payload.CopyTo(frame.AsSpan(4));
        frame[^1] = CalculateChecksum(payload);
        return frame;
    }

    /// <summary>Returns the two's-complement byte checksum used by DokiDoki.</summary>
    public static byte CalculateChecksum(ReadOnlySpan<byte> payload)
    {
        int sum = 0;
        foreach (byte value in payload) sum += value;
        return unchecked((byte)-sum);
    }

    private static byte[] EncodeCommandFrame(int messageId, byte command, byte value) =>
        EncodeFrame(messageId, DokiDokiConstants.ApplicationRequestCommand, [command, value]);

    private static void ValidateMessageId(int messageId)
    {
        if (messageId is < DokiDokiConstants.MinMessageId or > DokiDokiConstants.MaxMessageId)
            throw new ArgumentOutOfRangeException(
                nameof(messageId),
                messageId,
                $"Message ID must be between {DokiDokiConstants.MinMessageId} and {DokiDokiConstants.MaxMessageId}.");
    }
}
