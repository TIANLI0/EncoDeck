// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// A Fluent UI System Icons glyph by name: <c>&lt;controls:FluentIcon Symbol="link" /&gt;</c>, anywhere a FontIcon goes
/// (content, NavigationViewItem.Icon, MenuFlyoutItem.Icon). <see cref="Filled"/> picks the filled glyph. Bind
/// <see cref="Symbol"/> for icons that change.
/// </summary>
public sealed partial class FluentIcon : FontIcon
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(string), typeof(FluentIcon), new PropertyMetadata(string.Empty, OnSymbolChanged));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(FluentIcon), new PropertyMetadata(false, OnSymbolChanged));

    public FluentIcon()
    {
        FontFamily = FluentIcons.FontFamily;
    }

    public FluentIcon(string symbol, double size = double.NaN) : this()
    {
        Symbol = symbol;
        if (!double.IsNaN(size)) FontSize = size;
    }

    /// <summary>The icon's name in snake case ("link", "battery_charge_5").</summary>
    public string Symbol
    {
        get => (string)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    private static void OnSymbolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (FluentIcon)d;
        icon.Glyph = string.IsNullOrEmpty(icon.Symbol) ? string.Empty : FluentIcons.Glyph(icon.Symbol, icon.Filled);
    }
}
