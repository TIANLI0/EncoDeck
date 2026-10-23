// SPDX-License-Identifier: GPL-3.0-only
// Ported from Yilia src/chunfeng/protocol.ts (MIT).
namespace EncoDeck.Protocol.Brands.DokiDoki;

/// <summary>DokiDoki (TryFun / Chunfeng) GATT identifiers, command IDs, and protocol limits.</summary>
public static class DokiDokiConstants
{
    public static readonly Guid ServiceUuid = new("0000FFAC-0000-1000-8000-00805F9B34FB");
    public static readonly Guid WriteWithResponseCharacteristicUuid = new("0000FFB5-0000-1000-8000-00805F9B34FB");
    public static readonly Guid WriteWithoutResponseCharacteristicUuid = new("0000FFB7-0000-1000-8000-00805F9B34FB");
    public static readonly Guid NotifyCharacteristicUuid = new("0000FFB8-0000-1000-8000-00805F9B34FB");

    /// <summary>Actions and stop prefer ffb7 and use ffb5 only when ffb7 is unavailable.</summary>
    public static Guid ActionWriteCharacteristicUuid => WriteWithoutResponseCharacteristicUuid;
    public static Guid ActionFallbackWriteCharacteristicUuid => WriteWithResponseCharacteristicUuid;

    /// <summary>Heartbeat prefers ffb5 and uses ffb7 only when ffb5 is unavailable.</summary>
    public static Guid HeartbeatWriteCharacteristicUuid => WriteWithResponseCharacteristicUuid;
    public static Guid HeartbeatFallbackWriteCharacteristicUuid => WriteWithoutResponseCharacteristicUuid;

    /// <summary>The second byte of every request frame.</summary>
    public const byte ApplicationRequestCommand = 0x02;

    public const byte Meta2VibrationCommand = 0x08;
    public const byte Meta2LinearCommand = 0x0B;
    public const byte Meta2RotaryCommand = 0x0E;
    public const byte Bh2ProVibrationCommand = 0x09;
    public const byte Bh2ProLinearCommand = 0x0C;
    public const byte Bh2ProStopCommand = 0xFF;
    public const byte HeartbeatCommand = 0xEF;

    public const int MinMessageId = 1;
    public const int MaxMessageId = 15;
    public const int MinStrength = 0;
    public const int MaxStrength = 100;

    public const string DefaultDeviceName = "DK-META2";
    public const string Meta2DeviceName = DefaultDeviceName;
    public const string Bh2ProDeviceName = "DK-BH2PRO";
    public const string TryFunAliasPrefix = "TF-";
    public const string DokiDokiPrefix = "DK-";
    public const string TryFunPrefix = "TRYFUN-";
}
