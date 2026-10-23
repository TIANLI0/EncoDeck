// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using Windows.Devices.Bluetooth;

namespace EncoDeck.Probe;

/// <summary>
/// Probe-local copy of the observed DokiDoki BLE protocol. This intentionally has no
/// dependency on the experimental production implementation.
/// </summary>
internal static class DokiDokiProtocol
{
    public static readonly Guid ServiceUuid = new("0000ffac-0000-1000-8000-00805f9b34fb");
    public static readonly Guid WriteWithResponseUuid = new("0000ffb5-0000-1000-8000-00805f9b34fb");
    public static readonly Guid WriteWithoutResponseUuid = new("0000ffb7-0000-1000-8000-00805f9b34fb");
    public static readonly Guid NotifyUuid = new("0000ffb8-0000-1000-8000-00805f9b34fb");

    public static readonly string[] NamePrefixes = ["TF-", "DK-", "TRYFUN-"];

    private const byte AppRequestCommand = 0x02;
    private const byte HeartbeatCommand = 0xEF;
    private const byte VibrationCommand = 0x08;
    private const byte LinearCommand = 0x0B;
    private const byte RotaryCommand = 0x0E;

    /// <summary>Same order as the official client's ACTIONS_TO_ZERO.</summary>
    public static readonly (string Name, byte Command)[] StopActions =
    [
        ("linear", LinearCommand),
        ("rotary", RotaryCommand),
        ("vibration", VibrationCommand),
    ];

    public static bool IsSupportedName(string? name)
    {
        string normalized = (name ?? string.Empty).Trim();
        return NamePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsServiceUuid(Guid uuid) => uuid == ServiceUuid;

    public static bool TryParseAddress(string? text, out ulong address)
    {
        address = 0;
        string raw = (text ?? string.Empty).Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();
        return raw.Length == 12
            && ulong.TryParse(raw, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }

    public static string FormatAddress(ulong address)
    {
        var parts = new string[6];
        for (int i = 0; i < parts.Length; i++)
            parts[i] = ((address >> (40 - (i * 8))) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
        return string.Join(':', parts);
    }

    public static bool TryParseAddressType(string? text, out BluetoothAddressType type)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "public":
                type = BluetoothAddressType.Public;
                return true;
            case "random":
                type = BluetoothAddressType.Random;
                return true;
            case "unspecified":
            case "auto":
                type = BluetoothAddressType.Unspecified;
                return true;
            default:
                type = BluetoothAddressType.Unspecified;
                return false;
        }
    }

    public static string FormatAddressType(BluetoothAddressType type) => type switch
    {
        BluetoothAddressType.Public => "Public",
        BluetoothAddressType.Random => "Random",
        _ => "Unspecified",
    };

    public static byte[] BuildHeartbeatFrame(byte messageId) =>
        BuildFrame(messageId, [HeartbeatCommand, 0x01]);

    /// <summary>There is deliberately no frame builder for a nonzero motor value.</summary>
    public static byte[] BuildZeroFrame(byte messageId, byte actionCommand) =>
        BuildFrame(messageId, [actionCommand, 0x00]);

    private static byte[] BuildFrame(byte messageId, byte[] payload)
    {
        if (messageId is < 1 or > 15) throw new ArgumentOutOfRangeException(nameof(messageId));

        var frame = new byte[5 + payload.Length];
        frame[0] = messageId;
        frame[1] = AppRequestCommand;
        frame[2] = 0x00;
        frame[3] = (byte)(payload.Length + 1);
        payload.CopyTo(frame, 4);
        int sum = 0;
        foreach (byte value in payload) sum += value;
        frame[^1] = (byte)(-sum & 0xFF);
        return frame;
    }
}

internal sealed class DokiDokiMessageIds
{
    private int _current;

    public byte Next()
    {
        lock (this)
        {
            _current = _current >= 15 ? 1 : _current + 1;
            return (byte)_current;
        }
    }
}
