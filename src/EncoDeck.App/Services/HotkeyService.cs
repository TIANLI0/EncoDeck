// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.ViewModels;
using EncoDeck.Core.Configuration;
using EncoDeck.Platform.Input;
using EncoDeck.Platform.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace EncoDeck.App.Services;

/// <summary>
/// The noise-control hotkeys from the settings, registered system-wide while EncoDeck runs (also
/// with only the tray icon). A press switches the mode like the noise buttons do; presses while a
/// switch is still being confirmed are ignored.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    /// <summary>UI v2 order, as on the noise buttons and the earbuds' long-press cycle.</summary>
    private static readonly string[] CycleOrder = ["NoiseCancellation", "Adaptive", "Transparency", "Off"];

    private readonly ISettingsStore _settings;
    private readonly DeviceViewModel _device;
    private readonly GlobalHotkeys _hotkeys;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger _logger;
    private readonly Dictionary<HotkeyAction, HotkeyRegistration> _status = [];
    private readonly Lock _bindingsGate = new();
    private HotkeyBindings? _seenBindings;
    private int _suspended;
    private bool _running;

    public HotkeyService(ISettingsStore settings, DeviceViewModel device, GlobalHotkeys hotkeys, DispatcherQueue dispatcher, ILogger<HotkeyService> logger)
    {
        _settings = settings;
        _device = device;
        _hotkeys = hotkeys;
        _dispatcher = dispatcher;
        _logger = logger;
        _hotkeys.Pressed += OnPressed;
        _seenBindings = _settings.Current.Hotkeys;
        _settings.Changed += OnSettingsChanged;
        Apply();
    }

    /// <summary>Raised on the UI thread after the registrations changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Result of registering the action's hotkey; null when none is set (or while suspended).</summary>
    public HotkeyRegistration? StatusOf(HotkeyAction action) => _status.TryGetValue(action, out var status) ? status : null;

    /// <summary>
    /// While a new combination is being recorded the hotkeys are released, so pressing one that is
    /// already set reaches the recorder instead of switching the mode. Calls nest with <see cref="Resume"/>.
    /// </summary>
    public void Suspend()
    {
        if (_suspended++ == 0) Apply();
    }

    public void Resume()
    {
        if (_suspended > 0 && --_suspended == 0) Apply();
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _hotkeys.Pressed -= OnPressed;
        _hotkeys.Dispose();
    }

    /// <summary>
    /// Re-registers only when the bindings changed: every other setting (a slider, the theme) would otherwise release all
    /// hotkeys for a moment, and another app could take a combination in that gap.
    /// </summary>
    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        lock (_bindingsGate)
        {
            if (Equals(e.Hotkeys, _seenBindings)) return;
            _seenBindings = e.Hotkeys;
        }
        _dispatcher.TryEnqueue(Apply);
    }

    private void Apply()
    {
        _hotkeys.UnregisterAll();
        _status.Clear();
        if (_suspended == 0 && _settings.Current.Hotkeys is { } bindings)
        {
            foreach (var action in Enum.GetValues<HotkeyAction>())
            {
                if (!Hotkey.TryParse(bindings.Get(action), out var hotkey)) continue;
                _status[action] = _hotkeys.Register(IdOf(action), hotkey);
            }
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int IdOf(HotkeyAction action) => (int)action + 1;

    private async void OnPressed(object? sender, int id)
    {
        var action = (HotkeyAction)(id - 1);
        if (!Enum.IsDefined(action) || _running) return;
        if (!_device.CanSetNoise || _device.IsNoisePending) return;
        string? mode = TargetOf(action);
        if (mode is null) return;
        _logger.LogInformation("Hotkey {Action}: switching to {Mode}", action, mode);
        _running = true;
        try
        {
            await _device.SetNoiseModeCommand.ExecuteAsync(mode);
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>The mode to switch to, or null when the earbuds are already there or do not offer it.</summary>
    private string? TargetOf(HotkeyAction action)
    {
        var d = _device;
        string? current = d.IsAncSelected ? "NoiseCancellation" : d.IsAdaptiveSelected ? "Adaptive" : d.IsTransparencySelected ? "Transparency" : d.IsOffSelected ? "Off" : null;
        string? target = action switch
        {
            HotkeyAction.ToggleNoise => current == "NoiseCancellation" ? "Transparency" : "NoiseCancellation",
            HotkeyAction.CycleNoise => NextInCycle(current),
            HotkeyAction.NoiseCancellation => "NoiseCancellation",
            HotkeyAction.Adaptive => "Adaptive",
            HotkeyAction.Transparency => "Transparency",
            HotkeyAction.NoiseOff => "Off",
            _ => null,
        };
        return target is not null && target != current && IsOffered(target) ? target : null;
    }

    private string? NextInCycle(string? current)
    {
        var offered = CycleOrder.Where(IsOffered).ToArray();
        if (offered.Length == 0) return null;
        int index = Array.IndexOf(offered, current);
        return offered[(index + 1) % offered.Length];
    }

    private bool IsOffered(string mode) => mode switch
    {
        "NoiseCancellation" => _device.HasAncMode,
        "Adaptive" => _device.HasAdaptiveMode,
        "Transparency" => _device.HasTransparencyMode,
        "Off" => _device.HasOffMode,
        _ => false,
    };
}
