// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Configuration;
using EncoDeck.Platform.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EncoDeck.App.Services;

/// <summary>Applies the user's light/dark/system theme to every open window.</summary>
public sealed class ThemeService
{
    private readonly ISettingsStore _settings;
    private readonly Lock _lock = new();
    private readonly List<Registration> _windows = [];

    public ThemeService(ISettingsStore settings)
    {
        _settings = settings;
        _seenThemes = (settings.Current.Theme, settings.Current.QuickSettingsPanelTheme);
        _settings.Changed += OnSettingsChanged;
    }

    // The theme settings last applied: the app's and the Quick Settings panel's own (the only themeOf in use).
    private (AppTheme App, AppTheme QuickSettingsPanel) _seenThemes;

    /// <summary>Re-themes the windows only when a theme setting changed, not on every settings change.</summary>
    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var themes = (settings.Theme, settings.QuickSettingsPanelTheme);
        lock (_lock)
        {
            if (themes == _seenThemes) return;
            _seenThemes = themes;
        }
        ApplyAll();
    }

    public ElementTheme Theme => ToElementTheme(_settings.Current.Theme);

    /// <summary>
    /// Keeps <paramref name="window"/> in the user's theme, or in the one <paramref name="themeOf"/>
    /// picks from the settings (a window with a theme setting of its own).
    /// </summary>
    public void Register(Window window, Func<AppSettings, AppTheme>? themeOf = null)
    {
        var registration = new Registration(new WeakReference<Window>(window), window.DispatcherQueue, themeOf ?? (s => s.Theme));
        lock (_lock)
        {
            _windows.RemoveAll(r => !r.Window.TryGetTarget(out _));
            _windows.Add(registration);
        }
        // WinUI throws for any use of a closed window (0x800710DD), and from a dispatcher callback
        // that ends the app: forget the window as soon as it closes.
        window.Closed += (_, _) =>
        {
            lock (_lock) _windows.Remove(registration);
        };
        Apply(window, registration);
    }

    /// <summary>Diagnostics: what a settings change does.</summary>
    internal void ReapplyForDiagnostics() => ApplyAll();

    private void ApplyAll()
    {
        Registration[] windows;
        lock (_lock) windows = [.. _windows];
        foreach (var registration in windows)
        {
            registration.Queue.TryEnqueue(() =>
            {
                // Closed after this was queued: leave it alone.
                bool open;
                lock (_lock) open = _windows.Contains(registration);
                if (open && registration.Window.TryGetTarget(out var window)) Apply(window, registration);
            });
        }
    }

    private void Apply(Window window, Registration registration)
    {
        if (window.Content is FrameworkElement root) root.RequestedTheme = ToElementTheme(registration.ThemeOf(_settings.Current));
    }

    private static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private sealed record Registration(WeakReference<Window> Window, DispatcherQueue Queue, Func<AppSettings, AppTheme> ThemeOf);
}
