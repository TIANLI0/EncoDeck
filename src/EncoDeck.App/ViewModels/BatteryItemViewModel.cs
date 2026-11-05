// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using EncoDeck.App.Services;
using EncoDeck.Core.State;

namespace EncoDeck.App.ViewModels;

/// <summary>One battery (left, right or case) as shown in the UI.</summary>
public sealed partial class BatteryItemViewModel(BatteryComponent component) : ObservableObject
{
    /// <summary>Readings older than this, or restored from cache, are labelled "last read".</summary>
    public static readonly TimeSpan LiveThreshold = TimeSpan.FromMinutes(10);

    public BatteryComponent Component { get; } = component;

    [ObservableProperty]
    public partial string Label { get; set; } = component switch
    {
        BatteryComponent.Left => Loc.Get("Battery_Left"),
        BatteryComponent.Right => Loc.Get("Battery_Right"),
        _ => Loc.Get("Battery_Case"),
    };

    /// <summary>False for the right earbud and case of headphones, which report a single battery.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasVisibleValue))]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>Headphones report one battery, carried in the left reading.</summary>
    public void SetSingleBattery(bool single)
    {
        if (Component == BatteryComponent.Left) Label = Loc.Get(single ? "Battery_Headphones" : "Battery_Left");
        else IsVisible = !single;
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasVisibleValue))]
    public partial bool HasValue { get; set; }

    public bool HasVisibleValue => IsVisible && HasValue;

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string LevelText { get; set; } = "--";

    [ObservableProperty]
    public partial bool IsCharging { get; set; }

    [ObservableProperty]
    public partial bool IsStale { get; set; }

    /// <summary>At or below the low-battery threshold (Settings) and not charging: shown in the critical colour.</summary>
    [ObservableProperty]
    public partial bool IsLow { get; set; }

    /// <summary>"Last read 10:21" for stale values, empty for live ones.</summary>
    [ObservableProperty]
    public partial string FreshnessText { get; set; } = string.Empty;

    /// <summary>
    /// The level in tenths as Windows' own battery glyph (Segoe Fluent Icons, with or without the charging bolt): the
    /// one icon the app keeps from Windows, as Fluent UI System Icons' batteries are too slender (20 px) or too heavy (24 px).
    /// </summary>
    [ObservableProperty]
    public partial string Glyph { get; set; } = UnknownGlyph;

    private const string UnknownGlyph = "";

    [ObservableProperty]
    public partial string AutomationText { get; set; } = string.Empty;

    public void Apply(Observed<BatteryReading>? reading, bool channelReady, DateTimeOffset now, int lowThreshold)
    {
        if (reading?.Value.Level is not { } level)
        {
            HasValue = false;
            Percent = 0;
            LevelText = "--";
            IsCharging = false;
            IsStale = false;
            IsLow = false;
            FreshnessText = Loc.Get("Battery_Unknown");
            Glyph = UnknownGlyph;
            AutomationText = $"{Label} {Loc.Get("Battery_Unknown")}";
            return;
        }

        bool stale = !channelReady || reading.IsStale(now, LiveThreshold);
        HasValue = true;
        Percent = level;
        LevelText = $"{level}%";
        IsCharging = reading.Value.IsCharging == true && !stale;
        IsStale = stale;
        IsLow = level <= lowThreshold && !IsCharging;
        FreshnessText = stale ? Loc.Format("Battery_LastRead", reading.Timestamp.ToLocalTime().ToString("t")) : string.Empty;
        int step = Math.Clamp((int)Math.Round(level / 10.0), 0, 10);
        Glyph = ((char)((IsCharging ? 0xEBAB : 0xEBA0) + step)).ToString();
        AutomationText = $"{Label} {LevelText}{(IsCharging ? " " + Loc.Get("Battery_Charging") : string.Empty)}{(stale ? " " + FreshnessText : string.Empty)}";
    }
}
