// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EncoDeck.Tests.Platform;

/// <summary>
/// The app's icons are named Fluent UI System Icons (Controls\FluentIcon, Services\FluentIcons); a misspelled name only
/// fails at run time, so every literal name in the app's XAML and code must be in the font's name map.
/// </summary>
public sealed partial class FluentIconNameTests
{
    [GeneratedRegex("""(?:<controls:FluentIcon\b[^>]*\bSymbol="|new FluentIcon\(\s*"|FluentIcons\.(?:Glyph|Create|Exists)\(\s*")([a-z0-9_]+)"|=> "([a-z0-9_]+)",""")]
    private static partial Regex IconName();

    [GeneratedRegex("""^\s*\(NoiseControlMode\.\w+, "([a-z0-9_]+)",""", RegexOptions.Multiline)]
    private static partial Regex NoiseModeSymbol();

    [Fact]
    public void EveryIconNameIsInTheFont()
    {
        string app = Path.Combine(RepositoryRoot(), "src", "EncoDeck.App");
        using var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "Assets", "Fonts", "FluentSystemIcons-Resizable.json")));
        var known = map.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var used = new List<(string File, string Name)>();
        foreach (string file in Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            bool iconSource = text.Contains("FluentIcon", StringComparison.Ordinal);
            if (!iconSource) continue;
            foreach (Match m in IconName().Matches(text))
            {
                // "=> \"…\"," only counts in the device-type table of FluentIcons.
                if (m.Groups[2].Success && !file.EndsWith("FluentIcons.cs", StringComparison.Ordinal)) continue;
                used.Add((Path.GetFileName(file), m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
            }
            foreach (Match m in NoiseModeSymbol().Matches(text)) used.Add((Path.GetFileName(file), m.Groups[1].Value));
        }
        Assert.True(used.Count > 20, $"Only {used.Count} icon names found: the scan is broken.");
        var missing = used.Where(u => !known.Contains($"ic_fluent_{u.Name}_20_regular")).Select(u => $"{u.File}: {u.Name}").ToList();
        Assert.Empty(missing);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "EncoDeck.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("EncoDeck.slnx not found above the test output.");
    }
}
