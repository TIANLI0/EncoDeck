// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;

namespace EncoDeck.App.Services;

/// <summary>
/// The app's icons: Fluent UI System Icons (github.com/microsoft/fluentui-system-icons, MIT), the same design language
/// as Windows 11's Segoe Fluent Icons with a much larger set (smartwatch, link_dismiss, battery_charge_5, …). An icon
/// is named as in that repository, in snake case without size or style: "link", "battery_charge_5". The font is the
/// size-independent "Resizable" one; every icon has a regular (outline) and a filled glyph. Only the battery levels
/// stay Windows' Segoe Fluent Icons (BatteryItemViewModel). XAML uses
/// <see cref="Controls.FluentIcon"/> (<c>&lt;controls:FluentIcon Symbol="link" /&gt;</c>), code <see cref="Create"/>.
/// </summary>
public static class FluentIcons
{
    private const string FontUri = "ms-appx:///Assets/Fonts/FluentSystemIcons-Resizable.ttf#FluentSystemIcons-Resizable";

    public static FontFamily FontFamily { get; } = new(FontUri);

    private static readonly Lazy<FrozenDictionary<string, int>> CodePoints = new(() =>
    {
        using var stream = typeof(FluentIcons).Assembly.GetManifestResourceStream("EncoDeck.App.FluentSystemIcons.json")
            ?? throw new InvalidOperationException("The Fluent icon map is not embedded.");
        return (JsonSerializer.Deserialize<Dictionary<string, int>>(stream) ?? []).ToFrozenDictionary(StringComparer.Ordinal);
    });

    /// <summary>Whether the font has <paramref name="name"/>.</summary>
    public static bool Exists(string name, bool filled = false) => CodePoints.Value.ContainsKey(Key(name, filled));

    /// <summary>The glyph of <paramref name="name"/> (outline, or filled); a missing name is a bug, caught by the icon test.</summary>
    public static string Glyph(string name, bool filled = false) =>
        CodePoints.Value.TryGetValue(Key(name, filled), out int code) ? char.ConvertFromUtf32(code)
            : throw new KeyNotFoundException($"No Fluent icon '{name}'.");

    /// <summary>An icon element showing <paramref name="name"/>; NaN keeps the control's default size.</summary>
    public static Controls.FluentIcon Create(string name, double size = double.NaN, bool filled = false) =>
        new(name, size) { Filled = filled };

    /// <summary>The icon of a device the earbuds are paired with, by the type they report: 1 phone, 2 computer, 3 tablet, 4 watch.</summary>
    public static string ForDeviceType(int deviceType) => deviceType switch
    {
        1 => "phone",
        2 => "laptop",
        3 => "tablet",
        4 => "smartwatch",
        _ => "bluetooth",
    };

    private static string Key(string name, bool filled) => $"ic_fluent_{name}_20_{(filled ? "filled" : "regular")}";
}
