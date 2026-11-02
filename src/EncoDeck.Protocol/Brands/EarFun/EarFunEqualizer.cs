// SPDX-License-Identifier: GPL-3.0-only
// EarKit a77acd18890a5f28746c2303bfff0e23fff8d8ca (MIT): EQ frames, master-gain rules and preset curves.
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.EarFun;

/// <summary>
/// The earbuds' single user curve. It is written band by band and never reported back, so EncoDeck shows the
/// curve it last sent. Ten-band models take gains in half-dB steps from -6 to +6 dB; six-band models in whole dB
/// from -10 to +10 dB.
/// </summary>
public static class EarFunEqualizer
{
    public const int CurveId = 1;
    public const ushort GaiaVendor = 0x001D;
    public const ushort GaiaCommand = 0x0E01;
    public const ushort EfCommand = 0x2095;

    // EarKit's slider positions (level -10..10) in dB; ten-band presets are given as levels.
    private static readonly double[] Levels = [-6, -5, -4, -3.5, -3, -2.5, -2, -1.5, -1, -0.5, 0, 0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 5, 6];
    private static readonly double[] Freq10 = [31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    private static readonly double[] FreqTw205 = [32, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    // Six-band models: the six editable bands, then four fixed shaping bands.
    private static readonly double[] Q6Freq = [63, 180, 500, 1000, 8000, 15000, 33, 200, 2000, 15000];
    private static readonly double[] Q6Q = [0.7, 0.6, 0.6, 0.6, 0.6, 0.6, 0.5, 0.6, 0.9, 0.6];
    private static readonly int[] Q6Fixed = [270, -270, 90, 210];
    private static readonly double[] Ow100Master = [.. Enumerable.Repeat(-0.5, 11), -1.0, -1.5, -2.0, -2.5, -3.0, -3.5, -4.0, -4.5, -5.5, -6.5];
    private static readonly double[] Tw230Master = [.. Enumerable.Repeat(-11.0, 17), -11.5, -12.0, -13.0, -14.0];

    private static readonly (string Key, int Variant, int[] Levels)[] Presets10 =
    [
        ("Flat", 0, [0,0,0,0,0,0,0,0,0,0]), ("Rock", 0, [4,5,2,0,0,1,2,3,3,2]), ("Pop", 0, [3,4,0,0,0,0,2,3,3,3]),
        ("Country", 0, [0,1,0,0,1,0,0,2,1,2]), ("RnB", 0, [2,3,1,0,0,0,1,2,0,1]), ("Classical", 0, [-3,-2,-1,1,1,0,1,1,2,3]),
        ("Jazz", 0, [0,0,0,1,1,0,1,2,1,2]), ("Electronic", 0, [1,2,0,0,2,0,2,4,6,4]), ("Dance", 0, [0,4,0,0,2,0,2,4,1,0]),
        ("HipHop", 0, [2,4,0,0,3,2,1,7,0,0]), ("Metal", 0, [0,2,0,2,3,0,2,4,6,7]), ("Blues", 0, [0,0,0,1,2,0,1,3,1,2]),
        ("Soul", 0, [3,3,1,0,0,0,1,2,2,2]), ("Hits", 0, [2,4,1,-1,0,3,1,3,0,0]), ("Latin", 0, [3,3,0,0,0,3,0,2,0,2]),
        ("Natural", 0, [-5,-4,-2,0,-1,-1,0,-1,0,0]), ("VocalBoost", 0, [0,0,0,1,4,5,4,2,0,0]), ("VocalReduce", 0, [0,0,0,-1,-4,-5,-4,-2,0,0]),
        ("BassBoost", 1, [2,3,0,0,0,0,0,0,0,0]), ("BassBoost", 2, [3,6,0,0,0,0,0,0,0,0]), ("BassBoost", 3, [4,9,0,0,0,0,0,0,0,0]),
        ("BassReduce", 1, [-4,-3,-2,0,0,0,0,0,0,0]), ("BassReduce", 2, [-8,-6,-3,0,0,0,0,0,0,0]),
        ("TrebleBoost", 1, [0,0,0,0,0,0,0,2,3,3]), ("TrebleBoost", 2, [0,0,0,0,0,0,0,3,6,6]),
        ("TrebleReduce", 1, [0,0,0,0,0,0,0,-2,-3,-3]), ("TrebleReduce", 2, [0,0,0,0,0,0,0,-3,-6,-6]),
    ];
    private static readonly (string Key, int[] Gains)[] Presets6 =
    [
        ("Flat", [0,0,0,0,0,0]), ("BassBoost", [8,0,0,0,0,0]), ("TrebleBoost", [0,0,0,0,0,6]),
        ("BassReduce", [-6,-3,2,0,0,0]), ("TrebleReduce", [0,0,0,-2,-6,-6]),
    ];

    /// <summary>EarKit's EQ type: q10 / q6 (GAIA), or the EF master-gain rule.</summary>
    public static string Kind(EarFunModel model) => model.Id switch
    {
        "TWA01" or "TW201" or "ATVEL_TW201" or "TW101S" => "q6",
        _ when model.Gaia => "q10",
        "TW205" => "tw205",
        "TW230" => "tw230",
        "OW100" or "OW100_NEW" => "ow100",
        "CW100" => "cw100",
        "CW200" => "cw200",
        "HP201" => "hp201",
        "HP220" or "HP300" => "hp220",
        _ => "fixed",
    };

    private static bool SixBands(EarFunModel model) => Kind(model) == "q6";

    /// <summary>Models whose firmware takes the whole curve in one write (EarKit's combined EQ).</summary>
    public static bool Combined(EarFunModel model) => model.Id is "TW230" or "CW100" or "CW200";

    public static BandEqualizerSpec Spec(EarFunModel model) => SixBands(model)
        ? new(-10, 10, GainScale: 1) { WriteOnly = true, Presets = [.. Presets6.Select(p => new BandCurvePreset("EarFunEq_" + p.Key, 0, p.Gains))] }
        : new(-12, 12, GainScale: 2) { WriteOnly = true, Presets = [.. Presets10.Select(p => new BandCurvePreset("EarFunEq_" + p.Key, p.Variant, [.. p.Levels.Select(HalfDb)]))] };

    private static int HalfDb(int level) => (int)(Levels[level + 10] * 2);

    public static IReadOnlyList<int> Frequencies(EarFunModel model) => SixBands(model)
        ? [.. Q6Freq.Take(6).Select(f => (int)f)]
        : [.. (Kind(model) == "tw205" ? FreqTw205 : Freq10).Select(f => (int)f)];

    public static CustomEqualizer Flat(EarFunModel model) => Curve(model, new int[Frequencies(model).Count]);

    public static CustomEqualizer Curve(EarFunModel model, IReadOnlyList<int> gains)
    {
        var spec = Spec(model);
        return new(CurveId, "", spec.MinGain, spec.MaxGain, IsSelected: true,
            [.. Frequencies(model).Select((f, i) => new EqualizerBand(f, gains[i]))]);
    }

    /// <summary>True when the curve has this model's bands, in order, with every gain in range.</summary>
    public static bool Fits(EarFunModel model, CustomEqualizer curve)
    {
        var spec = Spec(model);
        return curve.Bands.Select(b => b.FrequencyHz).SequenceEqual(Frequencies(model))
            && curve.Bands.All(b => b.Gain >= spec.MinGain && b.Gain <= spec.MaxGain);
    }

    /// <summary>The frames that write <paramref name="gains"/> (in the spec's steps), one per band.</summary>
    public static IReadOnlyList<byte[]> Encode(EarFunModel model, IReadOnlyList<int> gains)
    {
        string kind = Kind(model);
        int q07 = (int)(0.7 * 4096);
        if (model.Gaia)
        {
            // Qualcomm user-EQ band parameters: band id, master -6 dB, frequency ×3, gain ×60 (dB), Q ×4096.
            bool six = kind == "q6";
            double[] freqs = six ? Q6Freq : Freq10;
            return [.. Enumerable.Range(0, 10).Select(i =>
            {
                int gain = six ? i < 6 ? gains[i] * 60 : Q6Fixed[i - 6] : gains[i] * 30;
                int q = six ? (int)(Q6Q[i] * 4096) : q07;
                return Gaia([(byte)(0xA1 + i), 0xFE, 0x98, .. U16((int)(freqs[i] * 3)), .. U16(gain), .. U16(q)]);
            })];
        }
        double max = gains.Max() / 2.0;
        // EarKit picks the master by the slider position of the loudest band; between positions, the next one up.
        int level = Array.FindIndex(Levels, v => v >= max);
        int master = kind switch
        {
            "hp201" => (int)(60 * (-2 - Math.Max(max, 0))),
            "hp220" => (int)(-8 - Math.Max(max, 0)) * 60,
            "cw100" => (int)(60 * (-5 - Math.Max(max, 0))),
            "cw200" => (int)(60 * (max > 0 ? -max : 0)),
            "ow100" => (int)(60 * Ow100Master[level]),
            "tw230" => (int)(60 * Tw230Master[level]),
            _ => 0xFE98,
        };
        double[] ef = kind == "tw205" ? FreqTw205 : Freq10;
        return [.. Enumerable.Range(0, gains.Count).Select(i => EarFunFrameCodec.Encode(false, EfCommand,
            [(byte)gains.Count, (byte)(i + 1), .. U16(master), .. U16((int)(ef[i] * 3)), .. U16(gains[i] * 30), .. U16(q07)]))];
    }

    private static byte[] Gaia(byte[] payload) =>
        [0xFF, 4, 0, (byte)payload.Length, GaiaVendor >> 8, GaiaVendor & 0xFF, GaiaCommand >> 8, GaiaCommand & 0xFF, .. payload];

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];
}
