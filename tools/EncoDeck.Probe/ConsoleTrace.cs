// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Probe;

/// <summary>Prints every frame and, with --raw, every raw read. Also keeps a transcript for reports.</summary>
internal sealed class ConsoleTrace(bool printRaw) : IProtocolTrace
{
    private readonly Lock _gate = new();
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

    public List<TranscriptEntry> Transcript { get; } = [];

    /// <summary>Record the transcript but do not print frames.</summary>
    public bool Quiet { get; init; }

    public void OnFrame(FrameDirection direction, OppoFrame frame, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            Transcript.Add(new TranscriptEntry((timestamp - _start).TotalMilliseconds, direction == FrameDirection.Sent ? "TX" : "RX", OppoRedaction.FrameHex(frame), OppoCommand.Describe(frame.Command)));
            if (Quiet) return;
            Console.ForegroundColor = direction == FrameDirection.Sent ? ConsoleColor.Cyan : ConsoleColor.Green;
            Console.WriteLine($"{(timestamp - _start).TotalMilliseconds,8:F0}ms {(direction == FrameDirection.Sent ? "TX" : "RX")} {OppoCommand.Describe(frame.Command),-30} seq={frame.Sequence:X2} payload[{frame.Payload.Length}]={PayloadText(frame)}");
            Console.ResetColor();
        }
    }

    /// <summary>A non-OPPO frame as its controller summarized it (identifiers already left out).</summary>
    public void OnBrandFrame(FrameDirection direction, string summary, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            string dir = direction == FrameDirection.Sent ? "TX" : "RX";
            Transcript.Add(new TranscriptEntry((timestamp - _start).TotalMilliseconds, dir, summary, "brand"));
            if (Quiet) return;
            Console.ForegroundColor = direction == FrameDirection.Sent ? ConsoleColor.Cyan : ConsoleColor.Green;
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {(timestamp - _start).TotalMilliseconds,8:F0}ms {dir} {summary}");
            Console.ResetColor();
        }
    }

    /// <summary>Spaced hex payload, masked when it carries personal data (unless --show-sensitive).</summary>
    public static string PayloadText(OppoFrame frame) =>
        OppoRedaction.IsSensitive(frame) && !ShowSensitive ? OppoRedaction.PayloadHex(frame) : Spaced(frame.Payload);

    public static bool ShowSensitive { get; set; }

    public void OnRawReceived(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
    {
        // Raw chunks can contain sensitive frames; only print them when explicitly allowed.
        if (!printRaw || !ShowSensitive) return;
        string hex = Spaced(data);
        lock (_gate)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"{(timestamp - _start).TotalMilliseconds,8:F0}ms raw {hex}");
            Console.ResetColor();
        }
    }

    public static string Spaced(ReadOnlySpan<byte> data) => data.IsEmpty ? "-" : string.Join(' ', Convert.ToHexString(data).Chunk(2).Select(c => new string(c)));
}

internal sealed record TranscriptEntry(double OffsetMs, string Direction, string FrameHex, string Description);
