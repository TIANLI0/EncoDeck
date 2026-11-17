// SPDX-License-Identifier: GPL-3.0-only
// Captured from the HarmonyOS plugin by the independent Android app "我的X5s Pro" by shui-tiao
// (https://github.com/shui-tiao/my-x5s-pro, GPL-3.0); see EarbudCommands.handshakeFrames().
namespace EncoDeck.Protocol.Brands.Wingcloud;

/// <summary>
/// The session the X5s Pro wants before it answers anything. Without <c>01 27</c> it silently drops
/// every <c>2B xx</c> query; without <c>01 3E</c> (devType) it drops most <c>2B xx</c> settings.
/// The frames are fixed captures, so they are sent verbatim rather than rebuilt from TLVs.
/// </summary>
internal static class WingcloudHandshake
{
    /// <summary>Declares the host's capability and version tuples (the earbuds answer with <c>0x60</c>).</summary>
    private static readonly byte[] Capabilities =
        [0x01, 0x27, 0x01, 0x01, 0x40, 0x02, 0x03, 0x64, 0x64, 0x40, 0x03, 0x03, 0x64, 0x64, 0x00];

    /// <summary>Property-channel version negotiation.</summary>
    private static readonly byte[] Negotiate =
        [0x01, 0x3A, 0x81, 0x14, 0x82, 0x12, 0x03, 0x04, 0x00, 0x00, 0x00, 0x03, 0x04, 0x04, 0x00, 0x00, 0x00,
         0x05, 0x05, 0x04, 0x00, 0x00, 0x00, 0x00];

    /// <summary>devType: establishes the session. Without it most <c>2B xx</c> settings are ignored.</summary>
    private static readonly byte[] DeviceType =
        [0x01, 0x3E, 0x01, 0x04, 0x00, 0x00, 0x00, 0x82];

    /// <summary>The plugin's capability list: versions, serial and model text of this firmware.</summary>
    private static readonly byte[] CapabilityList =
    [
        0x01, 0x07, 0x01, 0x08, 0x35, 0x2E, 0x33, 0x2E, 0x32, 0x2E, 0x32, 0x37, 0x03, 0x0B, 0x35, 0x30,
        0x31, 0x31, 0x35, 0x5F, 0x33, 0x30, 0x2F, 0x34, 0x30, 0x05, 0x06, 0x31, 0x4C, 0xE4, 0x9E, 0x00,
        0x00, 0x07, 0x08, 0x31, 0x2E, 0x31, 0x2E, 0x36, 0x34, 0x2E, 0x32, 0x09, 0x10, 0x35, 0x35, 0x30,
        0x34, 0x41, 0x41, 0x4B, 0x43, 0x32, 0x34, 0x34, 0x51, 0x30, 0x34, 0x33, 0x37, 0x0A, 0x0F, 0x42,
        0x54, 0x56, 0x2D, 0x4D, 0x45, 0x31, 0x30, 0x2D, 0x30, 0x30, 0x55, 0x53, 0x46, 0x59, 0x0C, 0x00,
        0x11, 0x08, 0x42, 0x54, 0x56, 0x2D, 0x4D, 0x45, 0x31, 0x30, 0x19, 0x01, 0x00,
    ];

    private static readonly WingcloudFrameCodec Codec = new();

    /// <summary>The handshake in the plugin's order: the command it answers and the frame to send.</summary>
    public static IReadOnlyList<(ushort Command, byte[] Frame)> Frames { get; } =
    [
        (0x0127, Codec.Encode(0x0127, Capabilities)),
        (0x013A, Codec.Encode(0x013A, Negotiate)),
        (0x013E, Codec.Encode(0x013E, DeviceType)),
        (0x0107, Codec.Encode(0x0107, CapabilityList)),
    ];

    /// <summary>
    /// Reads the identity out of the <c>01 07</c> answer, the only place this firmware states it:
    /// 7 = firmware (1.1.64.2), 9 = serial, 10 = model name, 11 = short model.
    /// </summary>
    public static (string? Version, string? Model, Dictionary<string, string> Extra)? ParseIdentity(byte[] payload)
    {
        if (!WingcloudFrameCodec.TryParseTlv(payload, out var fields)) return null;
        static string? Text(Dictionary<byte, byte[]> all, byte tag) =>
            all.TryGetValue(tag, out var raw) && raw.Length > 0 && raw.All(b => b is >= 32 and < 127)
                ? System.Text.Encoding.ASCII.GetString(raw).Trim()
                : null;

        string? version = Text(fields, 7), model = Text(fields, 10), serial = Text(fields, 9);
        if (version is null && model is null) return null;
        var extra = new Dictionary<string, string>();
        if (serial is not null) extra["serial"] = serial;
        if (Text(fields, 11) is { } shortModel) extra["shortModel"] = shortModel;
        return (version, model, extra);
    }
}
