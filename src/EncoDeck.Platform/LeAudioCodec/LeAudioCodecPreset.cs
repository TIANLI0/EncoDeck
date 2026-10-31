// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Platform.LeAudioCodec;

/// <summary>
/// An LC3 configuration (48 kHz, one frame per SDU) Windows can be told to ask LE Audio earbuds for. Only BAP presets
/// 48_1 .. 48_5: Windows has a QoS preset for each, and 48_6 (155 octets) exceeds what common earbuds accept.
/// </summary>
public sealed record LeAudioCodecPreset(string Id, int FrameUs, int Octets)
{
    /// <summary>No rewrite: whatever Windows picks.</summary>
    public static readonly LeAudioCodecPreset WindowsDefault = new("default", 0, 0);

    public static IReadOnlyList<LeAudioCodecPreset> All { get; } =
    [
        WindowsDefault,
        new("48_1", 7500, 75),
        new("48_2", 10000, 100),
        new("48_3", 7500, 90),
        new("48_4", 10000, 120),
        new("48_5", 7500, 117),
    ];

    public bool IsWindowsDefault => FrameUs == 0;

    /// <summary>Bitrate of one earbud (each earbud gets its own stream of this size), in kbit/s.</summary>
    public double Kbps => IsWindowsDefault ? 0 : Octets * 8 * 1000.0 / FrameUs;

    // Latency: Windows' own choice uses 7.5 ms frames, so the default reads like the 7.5 ms presets.
    private bool TenMs => FrameUs == 10000;

    /// <summary>LC3 algorithmic delay: one frame plus the look-ahead.</summary>
    public double CodecDelayMs => TenMs ? 12.5 : 11.5;

    /// <summary>Max transport latency Windows asks for while a game plays (BAP low latency; longer for 10 ms frames).</summary>
    public int GameLatencyLimitMs => TenMs ? 20 : 15;

    /// <summary>Max transport latency Windows asks for otherwise (BAP high reliability; longer for 10 ms frames).</summary>
    public int MediaLatencyLimitMs => TenMs ? 100 : 75;

    /// <summary>Presentation delay Windows asks the earbuds for, in both modes.</summary>
    public const int PresentationDelayMs = 40;

    public static LeAudioCodecPreset? Find(int frameUs, int octets) =>
        All.FirstOrDefault(p => p.FrameUs == frameUs && p.Octets == octets);

    public static LeAudioCodecPreset? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
