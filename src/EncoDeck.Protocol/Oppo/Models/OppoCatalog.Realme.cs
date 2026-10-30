// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;

namespace EncoDeck.Protocol.Oppo.Models;

/// <summary>
/// Noise-control trees and EQ presets of realme earbuds. realme models speak the same control
/// protocol as OPPO (0xAA framing, battery 0x0106, noise 0x0404 / 0x010C, EQ 0x0406 / 0x010F, RFCOMM
/// 0000079A-D102-11E1-9B23-00025B00A5A5), but the supplementary model table carries no noise or EQ
/// data for them, so those facts are kept here by hand.
/// <para>
/// Sources (interoperability facts only, re-implemented; no code copied):
/// QuickBuds — https://github.com/spizganed/QuickBuds (spizganed and contributors, GPL-3.0-or-later):
/// per-model noise trees and the four realme EQ presets (0x0406 ids 0 Pure Raw, 1 Deep Bass,
/// 2 Pure Voice, 3 Pure Bass), and which models their owners confirmed on hardware.
/// Gadgetbridge — https://codeberg.org/Freeyourgadget/Gadgetbridge (AGPL-3.0-or-later): realme Buds
/// T100/T110/T200/T300/Air 5 Pro/Air6 Pro use the OPPO headphones protocol, with the noise values
/// off 0x01, transparency 0x02, noise cancelling 0x08 and levels low 0x04 / normal 0x10 / dynamic 0x20.
/// </para>
/// Models whose owners have not confirmed a feature get it as <see cref="CapabilitySupport.Experimental"/>.
/// Writes are still gated by the command bitmap the earbuds report and confirmed by reading back.
/// </summary>
internal static partial class OppoCatalog
{
    // EQ preset types for the realme built-ins (not in the official OPPO list; numbered after it).
    internal const int RealmeEqPureRaw = 101;
    internal const int RealmeEqDeepBass = 102;
    internal const int RealmeEqPureVoice = 103;
    internal const int RealmeEqPureBass = 104;

    private enum RealmeNoise { None, Plain, Levels, LevelsWithSmart }

    /// <param name="Noise">Shape of the noise tree.</param>
    /// <param name="Equalizer">Has the four realme EQ presets.</param>
    /// <param name="NoiseConfirmed">Noise control confirmed on hardware (open-source report).</param>
    /// <param name="EqualizerConfirmed">EQ presets confirmed on hardware (open-source report).</param>
    private sealed record RealmeFacts(RealmeNoise Noise, bool Equalizer, bool NoiseConfirmed, bool EqualizerConfirmed);

    private static readonly Dictionary<uint, RealmeFacts> RealmeTable = new()
    {
        [0x050812] = new(RealmeNoise.Plain, true, true, true),               // Buds Wireless 3
        [0x050C12] = new(RealmeNoise.None, true, false, false),              // Buds Wireless 3 Neo
        [0x051412] = new(RealmeNoise.LevelsWithSmart, true, false, false),   // Buds Wireless 5 ANC
        [0x051812] = new(RealmeNoise.None, true, false, false),              // Buds Wireless 6 Neo
        [0x051C52] = new(RealmeNoise.None, true, false, false),              // Buds Wireless 6
        [0x052052] = new(RealmeNoise.LevelsWithSmart, true, false, false),   // Buds Wireless 6 ANC
        [0x061012] = new(RealmeNoise.None, true, false, false),              // Buds Air 3S
        [0x061C12] = new(RealmeNoise.LevelsWithSmart, true, true, false),    // Buds Air 5 Pro
        [0x062012] = new(RealmeNoise.LevelsWithSmart, true, false, false),   // Buds Air 5
        [0x062412] = new(RealmeNoise.Plain, true, true, true),               // Buds T300
        [0x063012] = new(RealmeNoise.LevelsWithSmart, true, true, true),     // Buds Air6 Pro
        [0x063412] = new(RealmeNoise.LevelsWithSmart, true, true, true),     // Buds Air6
        [0x063812] = new(RealmeNoise.LevelsWithSmart, true, false, false),   // Buds N1 Pro
        [0x063C12] = new(RealmeNoise.Levels, true, true, true),              // Buds T310
        [0x064012] = new(RealmeNoise.Levels, true, false, false),            // Buds N1
        [0x064812] = new(RealmeNoise.LevelsWithSmart, true, true, true),     // Buds Air7
        [0x064C12] = new(RealmeNoise.LevelsWithSmart, true, true, true),     // Buds Air7 Pro
        [0x065052] = new(RealmeNoise.None, true, false, false),              // Buds T200 Lite
        [0x065452] = new(RealmeNoise.Plain, true, true, true),               // Buds T200
        [0x065852] = new(RealmeNoise.Plain, true, true, true),               // Buds T200x
        [0x065C12] = new(RealmeNoise.None, true, false, true),               // Buds Clip
        [0x066012] = new(RealmeNoise.None, true, false, false),              // TechLife Buds
        [0x066452] = new(RealmeNoise.LevelsWithSmart, true, true, true),     // Buds T500 Pro
        [0x066812] = new(RealmeNoise.LevelsWithSmart, true, true, true),     // Buds Air8
        [0x066C12] = new(RealmeNoise.LevelsWithSmart, true, true, false),    // Buds Air8 Pro
        [0x067052] = new(RealmeNoise.Plain, true, false, false),             // Buds T500
        [0x067452] = new(RealmeNoise.LevelsWithSmart, true, false, false),   // Buds T500 Pro Harry Potter Edition
        [0x067852] = new(RealmeNoise.LevelsWithSmart, true, false, false),   // TechLife Buds T500 Pro
    };

    /// <summary>realme models the supplementary table does not list.</summary>
    // Expression-bodied: read from the lazy definitions list, whose initializer lives in another part of this class.
    internal static IReadOnlyList<CatalogModel> RealmeExtraModels =>
    [
        new("067852", "realme", "T2", ["realme TechLife Buds T500 Pro"], "0000079A-D102-11E1-9B23-00025B00A5A5"),
    ];

    private static RealmeFacts? Realme(CatalogModel model) =>
        model.Brand.Equals("realme", StringComparison.OrdinalIgnoreCase) && RealmeTable.TryGetValue(model.ProductIdValue, out var facts) ? facts : null;

    /// <summary>Fills in the realme noise tree and EQ presets where the model table has none.</summary>
    internal static CatalogModel WithRealmeFacts(CatalogModel model)
    {
        if (Realme(model) is not { } facts) return model;
        var next = model;
        if (model.Noise.Count == 0 && facts.Noise != RealmeNoise.None) next = next with { Noise = RealmeNoiseTree(facts.Noise) };
        if (model.Equalizer.Count == 0 && model.EqualizerCompat.Count == 0 && model.EqualizerByVersion.Count == 0 && facts.Equalizer)
        {
            next = next with
            {
                Equalizer =
                [
                    new(RealmeEqPureRaw, 0, 0, 0, -1),
                    new(RealmeEqDeepBass, 1, 1, 0, -1),
                    new(RealmeEqPureVoice, 2, 2, 0, -1),
                    new(RealmeEqPureBass, 3, 3, 0, -1),
                ],
            };
        }
        return next;
    }

    /// <summary>
    /// realme noise bits (one table for setting and reporting): off 0, transparency 1, noise
    /// cancelling 3 (also "high"), levels low 2, high 3, medium 4, smart 5.
    /// </summary>
    private static IReadOnlyList<CatalogNoiseNode> RealmeNoiseTree(RealmeNoise shape)
    {
        // Official mode types: 1 off, 2 transparency, 5 noise cancelling (parent), 7 smart, 4 strong, 8 medium, 3 weak.
        IReadOnlyList<CatalogNoiseNode> levels = shape switch
        {
            RealmeNoise.LevelsWithSmart => [new(7, 5, []), new(4, 3, []), new(8, 4, []), new(3, 2, [])],
            RealmeNoise.Levels => [new(4, 3, []), new(8, 4, []), new(3, 2, [])],
            _ => [],
        };
        return [new(1, 0, []), new(2, 1, []), new(5, 3, levels)];
    }

    /// <summary>
    /// Records where the realme noise / EQ facts come from, and marks the ones nobody has confirmed
    /// on hardware as experimental.
    /// </summary>
    private static void ApplyRealmeConfidence(CatalogModel model, DeviceCapabilities.Builder capabilities, bool hasNoise, bool hasEqualizer)
    {
        if (Realme(model) is not { } facts) return;
        static (CapabilitySupport, string) Level(bool confirmed) => confirmed
            ? (CapabilitySupport.Supported, "realme table (QuickBuds), confirmed on hardware by an owner: 0x0404 / 0x010C, 0x0406 / 0x010F.")
            : (CapabilitySupport.Experimental, "realme table (QuickBuds), not yet confirmed on this model's hardware.");
        if (hasNoise)
        {
            var (support, evidence) = Level(facts.NoiseConfirmed);
            capabilities.Set(DeviceCapability.NoiseCancellation, support, evidence);
            if (facts.Noise is RealmeNoise.Levels or RealmeNoise.LevelsWithSmart)
                capabilities.Set(DeviceCapability.NoiseCancellationLevel, support, evidence);
        }
        if (hasEqualizer)
        {
            var (support, evidence) = Level(facts.EqualizerConfirmed);
            capabilities.Set(DeviceCapability.Equalizer, support, evidence);
        }
    }
}
