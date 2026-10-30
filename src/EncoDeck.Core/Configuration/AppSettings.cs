// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.Configuration;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Where the connection popup appears on the screen with the taskbar.</summary>
public enum PopupPosition
{
    /// <summary>The corner next to the notification area (follows the taskbar edge).</summary>
    NearTray,
    BottomRight,
    BottomCenter,
    BottomLeft,
    TopLeft,
    TopCenter,
    TopRight,
}

/// <summary>Where the main window was when it last closed: its restored bounds (physical pixels, virtual-screen coordinates).</summary>
public sealed record WindowBounds
{
    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public bool Maximized { get; init; }
}

/// <summary>User preferences persisted as JSON under %LOCALAPPDATA%\EncoDeck.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool StartWithWindows { get; init; }

    /// <summary>Keep running in the notification area when the main window is closed.</summary>
    public bool KeepRunningInTray { get; init; } = true;

    /// <summary>
    /// Windows 11: show the earbuds panel on top of Quick Settings (Win+A) while it is open.
    /// Nullable only so a file from an older version (which reads as null) gets the default, on.
    /// </summary>
    public bool? ShowAboveQuickSettings { get; init; } = true;

    /// <summary>Leave Quick Settings alone while the Bluetooth radio is off. Nullable for older files, like the above.</summary>
    public bool? HideQuickSettingsPanelWhenBluetoothOff { get; init; } = true;

    /// <summary>Theme of the panel on top of Quick Settings; independent of <see cref="Theme"/>, so it can match the shell.</summary>
    public AppTheme QuickSettingsPanelTheme { get; init; } = AppTheme.System;

    public bool ShowConnectionPopup { get; init; } = true;

    public double PopupDurationSeconds { get; init; } = 4.0;

    public PopupPosition PopupPosition { get; init; } = PopupPosition.NearTray;

    /// <summary>Popup size relative to its standard size (0.6–1.6).</summary>
    public double PopupScale { get; init; } = 1.0;

    /// <summary>Do not show the connection popup while a fullscreen app (e.g. a game) is in the foreground.</summary>
    public bool SuppressPopupWhenFullscreen { get; init; } = true;

    public bool LowBatteryNotification { get; init; } = true;

    public int LowBatteryThreshold { get; init; } = 20;

    /// <summary>Show controls whose protocol support is only experimental.</summary>
    public bool EnableExperimentalFeatures { get; init; }

    /// <summary>Record raw control-channel bytes (redacted) to the diagnostic log.</summary>
    public bool RawProtocolLogging { get; init; }

    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>BCP-47 language tag, or null to follow Windows.</summary>
    public string? Language { get; init; }

    /// <summary>Device id the user last selected, so it is preferred when several are paired.</summary>
    public string? PreferredDeviceId { get; init; }

    /// <summary>
    /// Check GitHub Releases for a new version daily and download it in the background (never installs
    /// without the user). Nullable only so a file from an older version (which reads as null) gets the default, on.
    /// </summary>
    public bool? AutoCheckUpdates { get; init; } = true;

    /// <summary>Also offer pre-releases (e.g. 0.6.0-beta.1).</summary>
    public bool IncludePrereleaseUpdates { get; init; }

    /// <summary>System-wide noise-control hotkeys; none are set by default (a hotkey takes the combination away from every other app).</summary>
    public HotkeyBindings? Hotkeys { get; init; }

    /// <summary>Start the A2DP Windows Bridge core (in the background) when EncoDeck starts, if it is installed and not running.</summary>
    public bool A2dpBridgeAutoStart { get; init; }

    /// <summary>Labs: hold the LE Audio link and let the user check (and rebuild) its left/right alignment.</summary>
    public bool LeAudioSyncGuard { get; init; }

    /// <summary>The main window's last size and position, or null for the default (centered).</summary>
    public WindowBounds? MainWindowBounds { get; init; }

    public AppSettings Normalize() => this with
    {
        MainWindowBounds = MainWindowBounds is { Width: > 0, Height: > 0 } ? MainWindowBounds : null,
        Hotkeys = Hotkeys?.Normalize(),
        AutoCheckUpdates = AutoCheckUpdates ?? true,
        PopupDurationSeconds = Math.Clamp(PopupDurationSeconds, 1.5, 30),
        // Missing in settings saved by older versions (read as 0): the standard size.
        PopupScale = double.IsFinite(PopupScale) && PopupScale > 0 ? Math.Clamp(PopupScale, 0.6, 1.6) : 1.0,
        ShowAboveQuickSettings = ShowAboveQuickSettings ?? true,
        HideQuickSettingsPanelWhenBluetoothOff = HideQuickSettingsPanelWhenBluetoothOff ?? true,
        QuickSettingsPanelTheme = Enum.IsDefined(QuickSettingsPanelTheme) ? QuickSettingsPanelTheme : AppTheme.System,
        PopupPosition = Enum.IsDefined(PopupPosition) ? PopupPosition : PopupPosition.NearTray,
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 5, 50),
    };
}
