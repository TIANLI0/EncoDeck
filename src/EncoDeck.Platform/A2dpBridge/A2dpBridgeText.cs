// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EncoDeck.Platform.A2dpBridge;

/// <summary>
/// The core's status texts and errors arrive as keys of BlueCaster's language files (app/lang, Apache-2.0 OR MIT,
/// embedded from the pinned release). Formats them as BlueCaster's own window does; English, then the key, when a
/// text is missing.
/// </summary>
public static partial class A2dpBridgeText
{
    private static readonly Lazy<FrozenDictionary<string, string>> English = new(() => Load("en"));
    private static readonly Lazy<FrozenDictionary<string, string>> Chinese = new(() => Load("zh-CN"));

    public static string Format(BridgeMessage message, CultureInfo? culture = null)
    {
        if (message.IsEmpty) return "";
        string template = Get(message.Key, culture) ?? message.Key;
        return Printf(template, message.Args);
    }

    public static string? Get(string key, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        bool chinese = culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        return (chinese && Chinese.Value.TryGetValue(key, out var zh)) ? zh
            : English.Value.TryGetValue(key, out var en) ? en : null;
    }

    [GeneratedRegex(@"%(%|([-+#0]*)(\d+)?(?:\.(\d+))?(?:hh|h|ll|l|z|j|t)?([diuxXfFeEgGcsp]))", RegexOptions.CultureInvariant)]
    private static partial Regex Spec();

    /// <summary>printf as BlueCaster's printf.ts: the core's arguments are strings, inserted as they are (numbers keep the requested precision).</summary>
    internal static string Printf(string template, IReadOnlyList<string> args)
    {
        int next = 0;
        return Spec().Replace(template, m =>
        {
            if (m.Groups[1].Value == "%") return "%";
            if (next >= args.Count) return "";
            string arg = args[next++];
            string flags = m.Groups[2].Value, conversion = m.Groups[5].Value;
            if (m.Groups[4].Success && conversion is "f" or "F"
                && double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                arg = value.ToString("F" + m.Groups[4].Value, CultureInfo.InvariantCulture);
                if (flags.Contains('+') && value >= 0) arg = "+" + arg;
            }
            else if (flags.Contains('+') && conversion is "d" or "i" && !arg.StartsWith('-') && !arg.StartsWith('+')) arg = "+" + arg;
            if (m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out int width) && arg.Length < width)
                arg = flags.Contains('-') ? arg.PadRight(width) : arg.PadLeft(width, flags.Contains('0') ? '0' : ' ');
            return arg;
        });
    }

    private static FrozenDictionary<string, string> Load(string language)
    {
        try
        {
            using var stream = typeof(A2dpBridgeText).Assembly.GetManifestResourceStream($"EncoDeck.Platform.A2dpBridge.Lang.{language}.json");
            if (stream is null) return FrozenDictionary<string, string>.Empty;
            using var document = JsonDocument.Parse(stream);
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String && !property.Name.StartsWith('_')) texts[property.Name] = property.Value.GetString()!;
            return texts.ToFrozenDictionary(StringComparer.Ordinal);
        }
        catch (JsonException) { return FrozenDictionary<string, string>.Empty; }
    }

    /// <summary>A codec key of a profile or stream ("ldac", "lhdcv5") as BlueCaster names it ("LDAC", "LHDC V5").</summary>
    public static string Codec(string key, CultureInfo? culture = null) => key.ToLowerInvariant() switch
    {
        "auto" => Get("codec.auto", culture) ?? "Auto",
        "ldac" => "LDAC",
        "aptxhd" => "aptX HD",
        "aptxll" => "aptX LL",
        "aptx" => "aptX",
        "sbcxq" => "SBC XQ",
        "sbc" => "SBC",
        "aac" => "AAC",
        "lhdcv5" => "LHDC V5",
        "opus" => "Opus",
        "opus05" => "Opus 05",
        "faststream" => "FastStream",
        "lc3" => "LC3",
        _ => key,
    };
}
