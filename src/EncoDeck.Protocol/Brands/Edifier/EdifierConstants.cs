// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Further commands ported from mEDIFIER (https://github.com/wh201906/mEDIFIER, 10056b5, MIT,
// Copyright (c) 2023 Self Not Found) and Macifier (https://github.com/luanyufei/Macifier, 5479859,
// MIT, Copyright (c) 2024-present Macifier Contributors) — MIT notice in THIRD_PARTY_NOTICES.md —
// and from HyperEars (https://github.com/silverpoetry/HyperEars, 361d279) and BudsLink
// (https://github.com/maniacx/BudsLink, ba3160d), both GPL-3.0.
namespace EncoDeck.Protocol.Brands.Edifier;

/// <summary>
/// Constants of the EDIFIER SPP control protocol (as documented by mEDIFIER / its Klinkore fork,
/// via OppoPodsManager), with TWS state and GATT service facts from Edifier Connect 8.5.3.
/// </summary>
public static class EdifierConstants
{
    /// <summary>Fixed RFCOMM SPP service UUID used by EDIFIER headphones.</summary>
    public static readonly Guid SppServiceId = new("EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF");

    public static readonly Guid GattReadId = new("48090001-1a48-11e9-ab14-d663bd873d93");
    public static readonly Guid GattWriteId = new("48090002-1a48-11e9-ab14-d663bd873d93");

    public static bool IsGattService(Guid uuid)
    {
        string value = uuid.ToString();
        return value.StartsWith("4809", StringComparison.Ordinal) && value.EndsWith("-1a48-11e9-ab14-d663bd873d93", StringComparison.Ordinal);
    }

    public const byte RequestHead = 0xAA;
    public const byte ResponseHeadBb = 0xBB;
    public const byte ResponseHeadCc = 0xCC;

    /// <summary>Checksum seed: sum = 8217 + every byte before the checksum (big-endian, 2 bytes).</summary>
    public const ushort ChecksumSeed = 8217;

    /// <summary>Battery query; the response echoes the command with payload[0] = percent.</summary>
    public const byte QueryBattery = 0xD0;

    /// <summary>TWS state: [peer, leftPercent, rightPercent, casePercent?, flags?, ...].</summary>
    public const byte QueryTwsState = 0xF2;

    /// <summary>Noise query; the response payload is [mode, ambientVolume?].</summary>
    public const byte QueryNoiseMode = 0xCC;

    /// <summary>Noise set with payload [mode]; the device echoes the command as acknowledgement.</summary>
    public const byte SetNoiseMode = 0xC1;

    public const byte NoiseOff = 1;
    public const byte NoiseAnc = 2;
    public const byte NoiseTransparency = 3;

    // Commands below: W820NB Double Gold frames captured in mEDIFIER doc/KnownCommands, W820NB
    // frames from Macifier's hardware test, and the BES v2 dialects recorded by HyperEars and BudsLink.

    /// <summary>Ambient (transparency) level byte after <see cref="NoiseTransparency"/>: 3–9 for −3…+3.</summary>
    public const byte AmbientLevelOffset = 6;
    public const int AmbientLevelMin = -3;
    public const int AmbientLevelMax = 3;

    /// <summary>Game (low latency) mode: query → <c>[0|1]</c>; set <c>[0|1]</c> is echoed.</summary>
    public const byte QueryGameMode = 0x08;
    public const byte SetGameMode = 0x09;

    /// <summary>
    /// Unsolicited notice. The W830NB sent <c>16 08</c> instead of echoing a spatial-scene write while it was
    /// used through BlueCaster, and kept its scene (issue #18); its other meanings are not recorded.
    /// </summary>
    public const byte StateNotice = 0x16;

    /// <summary>
    /// Power-off timer: query → <c>[00]</c> (off) or <c>[minutes hi][minutes lo]</c>; set
    /// <c>[hi][lo]</c>; disable with an empty <see cref="DisablePowerOffTimer"/>. Set and disable are
    /// answered with a <c>CC</c>-headed <c>[01]</c>. A set of zero minutes powers the headphones off
    /// at once, so zero is only ever sent as a disable.
    /// </summary>
    public const byte QueryPowerOffTimer = 0xD3;
    public const byte SetPowerOffTimer = 0xD1;
    public const byte DisablePowerOffTimer = 0xD2;

    /// <summary>Timer values offered by the official app in the W820NB Double Gold capture.</summary>
    public static IReadOnlyList<int> PowerOffTimerMinutes { get; } = [5, 15, 30, 60, 180];

    /// <summary>Button control (noise modes the physical button cycles): query <c>[0A]</c> → <c>[0A][mask]</c>; set <c>[0A][mask]</c>.</summary>
    public const byte QueryKeyControl = 0xF0;
    public const byte SetKeyControl = 0xF1;
    public const byte KeyControlSlot = 0x0A;

    /// <summary>Button control masks (1 = standard/off, 2 = noise cancelling, 4 = ambient) the official app offers.</summary>
    public static IReadOnlyList<int> KeyControlMasks { get; } = [7, 6, 5, 3];

    /// <summary>Built-in sound effect: set <c>[0..3]</c> (classic, pop, classical, rock); query → <c>[value]</c>.</summary>
    public const byte SetSoundEffect = 0xC4;
    public const byte QuerySoundEffect = 0xD5;

    /// <summary>In-ear detection switch (BES v2 TWS): query → <c>[0|1]</c>; set <c>[0|1]</c>.</summary>
    public const byte QueryInEarSwitch = 0xFB;
    public const byte SetInEarSwitch = 0xFC;

    /// <summary>Second byte of every BES v2 frame.</summary>
    public const byte AppCode = 0xEC;

    /// <summary>XOR applied to every BES v2 payload byte, both directions, unless the model sends plaintext.</summary>
    public const byte PayloadXor = 0xA5;

    /// <summary>BES v2 noise slots (first payload byte of <see cref="QueryNoiseMode"/> / <see cref="SetNoiseMode"/>).</summary>
    public const byte AncSlotW860 = 0x10;
    public const byte AncSlotEvo = 0x1B;
    public const byte AncSlotX5 = 0x17;
}
