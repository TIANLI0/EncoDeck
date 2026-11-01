// SPDX-License-Identifier: GPL-3.0-only
// EarKit a77acd18890a5f28746c2303bfff0e23fff8d8ca (MIT): GAIA/EF command and model facts.
using System.Text;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.EarFun;

public enum EarFunSetting { Firmware, Battery, RightBattery, Case, Charging, CaseCharging, Noise, Game, Dual, Wear, Prompt, Find, Spatial, Sub, Ambient, AutoOff, Mic, Quality, TouchLock }

/// <summary>One paired host from the dual-connection list. <see cref="Mac"/> is the partial address the earbuds report.</summary>
public sealed record EarFunSource(byte Number, string Name, byte[] Mac, bool Connected);

public static class EarFunSettings
{
    // Dual-connection list and host commands (no write/read pair): list, connect, disconnect, active-player list.
    public static ushort DeviceList(EarFunModel model) => model.Gaia ? (ushort)0x032C : (ushort)0x2150;
    public static ushort ConnectHost(EarFunModel model) => model.Gaia ? (ushort)0x0329 : (ushort)0x2165;
    public static ushort DisconnectHost(EarFunModel model) => model.Gaia ? (ushort)0x032A : (ushort)0x2175;
    public const ushort PlayList = 0x0358;

    /// <summary>Entry layout: number, 19-byte name, then 3 (GAIA) or 4 (EF) address bytes and the connection state.</summary>
    public static EarFunSource? Source(EarFunModel model, byte[] p)
    {
        if (p.Length < 26 || p[25] > 1) return null;
        var name = p.AsSpan(1, 19);
        int end = name.IndexOf((byte)0);
        return new(p[0], Encoding.UTF8.GetString(end < 0 ? name : name[..end]).Trim(), model.Gaia ? p[22..25] : p[21..25], p[25] == 1);
    }

    /// <summary>Codec switches in the quality byte. aptX bit 0 is never offered, only preserved.</summary>
    public static IReadOnlyDictionary<DeviceToggle, byte> CodecBits(EarFunModel model)
    {
        var bits = new Dictionary<DeviceToggle, byte>();
        if (!model.Has("quality")) return bits;
        if (model.Has("ldac")) bits[DeviceToggle.Ldac] = model.Gaia ? (byte)8 : (byte)4;
        if (model.Has("aptx")) { bits[DeviceToggle.AptXAdaptive] = 2; bits[DeviceToggle.AptXLossless] = 16; }
        return bits;
    }

    /// <summary>Touch lock as one value, 0 none, 1 both, 2 left, 3 right: GAIA's own byte, EF's two side flags.</summary>
    public static byte[] TouchLockPayload(EarFunModel model, int value) =>
        model.Gaia ? [(byte)value] : [value is 1 or 2 ? (byte)1 : (byte)0, value is 1 or 3 ? (byte)1 : (byte)0];
    public static int? TouchLockValue(EarFunModel model, byte[] p) => model.Gaia
        ? p is [<= 3 and var v, ..] ? v : null
        : p is [<= 1 and var l, <= 1 and var r] ? (l, r) switch { (0, 0) => 0, (1, 1) => 1, (1, 0) => 2, _ => 3 } : null;

    public static (ushort Read, ushort Write) Command(EarFunModel model, EarFunSetting setting) => (model.Gaia, setting) switch
    {
        (true, EarFunSetting.Firmware) => (0x0309, 0), (false, EarFunSetting.Firmware) => (0x1010, 0),
        (true, EarFunSetting.Battery) => (0x0306, 0), (false, EarFunSetting.Battery) => (0x1014, 0),
        (true, EarFunSetting.RightBattery) => (0x0307, 0),
        (true, EarFunSetting.Case) => (0x0317, 0), (false, EarFunSetting.Case) => (0x1018, 0),
        (false, EarFunSetting.Charging) => (0x1016, 0), (false, EarFunSetting.CaseCharging) => (0x1020, 0),
        (true, EarFunSetting.Noise) => (0x0315, 0x0314), (false, EarFunSetting.Noise) => (0x2080, 0x2085),
        (true, EarFunSetting.Game) => (0x0313, 0x0312), (false, EarFunSetting.Game) => (0x2030, 0x2035),
        (true, EarFunSetting.Dual) => (0x0326, 0x0327), (false, EarFunSetting.Dual) => (0x2140, 0x2145),
        (true, EarFunSetting.Wear) => (0x034A, 0x0349),
        (true, EarFunSetting.Prompt) => (0x0339, 0x0338), (false, EarFunSetting.Prompt) => (0x2120, 0x2125),
        (true, EarFunSetting.Find) => (0x0333, 0x0332), (false, EarFunSetting.Find) => (0x2050, 0x2055),
        (false, EarFunSetting.Spatial) => (0x2190, 0x2195),
        (true, EarFunSetting.Sub) => (0x033B, 0x033A), (false, EarFunSetting.Sub) => (0x2310, 0x2315),
        (true, EarFunSetting.Ambient) => (0x033D, 0x033C), (false, EarFunSetting.Ambient) => (0x2300, 0x2305),
        (true, EarFunSetting.AutoOff) => (0x0348, 0x0347), (false, EarFunSetting.AutoOff) => (0x2130, 0x2135),
        (true, EarFunSetting.Mic) => (0x0331, 0x0330), (false, EarFunSetting.Mic) => (0x2200, 0x2205),
        (true, EarFunSetting.Quality) => (0x032F, 0x032E), (false, EarFunSetting.Quality) => (0x2100, 0x2105),
        (true, EarFunSetting.TouchLock) => (0x0335, 0x0334), (false, EarFunSetting.TouchLock) => (0x2060, 0x2065),
        _ => (0, 0),
    };

    public static IReadOnlyList<(byte Value, NoiseControlState State, string Label, string Key)> NoiseModes(EarFunModel model)
    {
        if (!model.Has("noise")) return [];
        if (model.Gaia) return [(0, new(NoiseControlMode.Off), "Off", "NoiseMode_Off"),
            (1, new(NoiseControlMode.NoiseCancellation), "Noise cancelling", "NoiseMode_NoiseCancellation"),
            (2, new(NoiseControlMode.Transparency), "Transparency", "NoiseMode_Transparency")];
        return [(1, new(NoiseControlMode.Off), "Off", "NoiseMode_Off"),
            (2, new(NoiseControlMode.Transparency), "Transparency", "NoiseMode_Transparency"),
            .. model.Variant == "tw230" ? new[] { ((byte)3, new NoiseControlState(NoiseControlMode.NoiseCancellation), "Noise cancelling", "NoiseMode_NoiseCancellation") } :
                new[] { ((byte)3, new NoiseControlState(NoiseControlMode.NoiseCancellation, "wind"), "Wind reduction", "EarFun_Wind"),
                    ((byte)4, new NoiseControlState(NoiseControlMode.NoiseCancellation), "Noise cancelling", "NoiseMode_NoiseCancellation") },
            .. model.Variant == "head" && model.Id != "HP201" ? new[] { ((byte)5, new NoiseControlState(NoiseControlMode.NoiseCancellation, "anc1"), "ANC 1", "EarFun_Anc1") } : [],
            .. model.Variant == "ef" ? new[] { ((byte)5, new NoiseControlState(NoiseControlMode.Adaptive), "Adaptive", "NoiseMode_Adaptive") } : []];
    }

    public static DeviceOption? Option(EarFunModel model, EarFunSetting setting, byte value)
    {
        DeviceOptionChoice C(int v, string key) => new(v, "EarFun_" + key);
        IReadOnlyList<DeviceOptionChoice> choices = setting switch
        {
            EarFunSetting.Sub when model.Tw700 => [C(0,"EarAdaptive"), C(4,"Adaptive"), C(1,"Wind"), C(6,"Level1"), C(5,"Level2"), C(3,"Level3"), C(2,"Level4")],
            EarFunSetting.Sub when model.Variant == "tw600" => [C(2,"Strong"), C(3,"Balanced"), C(4,"Adaptive"), C(0,"EarAdaptive"), C(1,"Wind")],
            EarFunSetting.Sub when model.Variant == "hp1000" => [C(6,"EarAdaptive"), C(0,"Adaptive"), C(1,"Wind"), C(5,"Level1"), C(4,"Level2"), C(3,"Level3"), C(2,"Level4")],
            EarFunSetting.Sub when model.Variant == "tw230" => [C(1,"Strong"), C(2,"Balanced"), C(4,"Adaptive"), C(3,"Wind")],
            EarFunSetting.Sub => [C(0,"Strong"), C(1,"Wind")],
            EarFunSetting.Ambient => [C(model.Gaia ? 0 : 1,"Default"), C(model.Gaia ? 1 : 2,"Natural")],
            EarFunSetting.AutoOff => [new(255,"EarFun_Never"), .. AutoOff(model).Select(p => new DeviceOptionChoice(p.Wire, "EarFun_Minutes", p.Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)))],
            EarFunSetting.Mic => [C(0,"MicAuto"), C(1,"MicLeft"), C(2,"MicRight")],
            EarFunSetting.TouchLock => [C(0,"TouchLockOff"), C(1,"TouchLockBoth"), C(2,"TouchLockLeft"), C(3,"TouchLockRight")],
            _ => [],
        };
        return choices.Any(c => c.Value == value) ? new("earfun-" + setting.ToString().ToLowerInvariant(), "EarFun_" + setting, value, choices) : null;
    }

    public static IReadOnlyList<(byte Wire, int Minutes)> AutoOff(EarFunModel model) => model.Variant == "hp1000"
        ? [(2, 90), (3, 30), (4, 60), (5, 120)] : model.Gaia ? [(1, 5), (2, 10), (3, 30), (4, 60), (5, 120)]
        : model.Variant is "tw230" or "cw100" or "cw200" or "head" ? [(1, 5), (2, 10), (3, 30), (4, 60), (6, 120)]
        : [(1, 5), (2, 10), (3, 30), (4, 60), (5, 90), (6, 120)];
}
