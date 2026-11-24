// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.EarFun;
using EncoDeck.Protocol.Brands.Moondrop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace EncoDeck.App.Controls;

/// <summary>
/// The device switches and reported options of one <see cref="SettingGroup"/>, as a titled card or as bare rows
/// inside another card. It shows only what the connected earbuds offer and collapses when nothing is left, so a
/// page can place a group for every brand. Values are always the device-confirmed ones: a change snaps back until
/// the earbuds acknowledge it.
/// </summary>
public sealed partial class SettingsGroupView : UserControl
{
    private static readonly TimeSpan SliderDebounce = TimeSpan.FromMilliseconds(500);

    private readonly StackPanel _rows = new() { Spacing = 12 };
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
    private readonly StackPanel _content = new() { Spacing = 12 };
    private readonly Border _card;
    private readonly List<(DeviceToggleViewModel Toggle, ToggleSwitch Switch, SettingRow Row)> _switches = [];
    private readonly List<(string Id, FrameworkElement Control, TextBlock? Value, SettingRow Row)> _options = [];
    private readonly HashSet<string> _writes = [];
    private SettingRow? _gainRow;
    private ComboBox? _gainBox;
    private SettingRow? _promptRow;
    private Slider? _promptSlider;
    private TextBlock? _promptValue;
    private Debouncer? _promptWrite;
    private Debouncer? _optionWrite;
    private (string Id, int Value)? _optionTarget;
    private string _optionSignature = string.Empty;
    private string? _builtFor;
    private bool _syncing;
    private bool _attached;
    private bool _hasRows;

    public SettingsGroupView()
    {
        _content.Children.Add(_title);
        _content.Children.Add(_rows);
        _card = new Border { Style = (Style)Application.Current.Resources["CardStyle"], Child = _content };
        Content = _card;
        Visibility = Visibility.Collapsed;
        Loaded += (_, _) => Attach();
        // Unloaded can follow the next Loaded when a page is reused; only detach while really off the tree.
        Unloaded += (_, _) => { if (!IsLoaded) Detach(); };
    }

    /// <summary>The settings shown.</summary>
    public SettingGroup Group { get; set; }

    /// <summary>The card's title; rows only (no card) when <see cref="Bare"/> is set.</summary>
    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    /// <summary>Only the rows, for a group placed inside another card.</summary>
    public bool Bare
    {
        get => ReferenceEquals(Content, _rows);
        set
        {
            if (value == Bare) return;
            Content = null;
            _content.Children.Remove(_rows);
            if (value)
            {
                Content = _rows;
            }
            else
            {
                _content.Children.Add(_rows);
                Content = _card;
            }
        }
    }

    /// <summary>Whether any row is shown (the view is collapsed otherwise).</summary>
    public bool HasRows => _hasRows;

    /// <summary>Raised when <see cref="HasRows"/> changes, for pages that show something else when a group is empty.</summary>
    public event EventHandler? HasRowsChanged;

    private DeviceViewModel ViewModel => App.Instance.Device;

    private FeaturesViewModel Features => App.Instance.Features;

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        ViewModel.Toggles.CollectionChanged += OnTogglesChanged;
        ViewModel.PropertyChanged += OnViewModelChanged;
        Features.Changed += OnFeaturesChanged;
        RebuildToggles();
        _builtFor = null;
        Sync();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        ViewModel.Toggles.CollectionChanged -= OnTogglesChanged;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        Features.Changed -= OnFeaturesChanged;
        foreach (var (toggle, _, _) in _switches) toggle.PropertyChanged -= OnToggleChanged;
        // The sliders already show the new values: a write still waiting for its debounce is sent now
        // rather than dropped (it must not refresh this view any more).
        _promptWrite?.Flush();
        _optionWrite?.Flush();
    }

    private void OnTogglesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RebuildToggles();
        Sync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnToggleChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnFeaturesChanged(object? sender, EventArgs e) => Sync();

    // ---- building ------------------------------------------------------------------------------

    private void RebuildToggles()
    {
        foreach (var (toggle, _, row) in _switches)
        {
            toggle.PropertyChanged -= OnToggleChanged;
            _rows.Children.Remove(row);
        }
        _switches.Clear();
        int at = 0;
        foreach (var toggle in ViewModel.Toggles.Where(t => SettingGroups.Of(t.Toggle) == Group))
        {
            var sw = new ToggleSwitch { Name = $"Toggle{toggle.Toggle}", Style = (Style)Application.Current.Resources["TrailingSwitchStyle"], Tag = toggle };
            sw.Toggled += OnSwitchToggled;
            toggle.PropertyChanged += OnToggleChanged;
            var row = new SettingRow { Header = toggle.Label, Description = toggle.Description, Action = sw };
            _switches.Add((toggle, sw, row));
            // Switches first, then the device-reported options and the special rows.
            _rows.Children.Insert(at++, row);
        }
    }

    /// <summary>The gain and prompt-volume rows, built once per model (their range depends on the brand).</summary>
    private void BuildSpecialRows()
    {
        string key = ViewModel.ProfileKey;
        if (key == _builtFor) return;
        _builtFor = key;
        if (Group == SettingGroup.AudioQuality && _gainRow is null)
        {
            _gainBox = new ComboBox { Name = "GainBox", MinWidth = 200 };
            foreach (string label in new[] { "Moondrop_GainLow", "Moondrop_GainMid", "Moondrop_GainHigh" }) _gainBox.Items.Add(Loc.Get(label));
            _gainBox.SelectionChanged += OnGainChanged;
            _gainRow = new SettingRow { Name = "GainSection", Header = Loc.Get("Moondrop_Gain/Header"), Action = _gainBox };
            _rows.Children.Add(_gainRow);
        }
        if (Group == SettingGroup.General)
        {
            if (_promptRow is null)
            {
                _promptSlider = new Slider
                {
                    Width = (double)Application.Current.Resources["RowSliderWidth"], VerticalAlignment = VerticalAlignment.Center,
                    TickPlacement = TickPlacement.BottomRight,
                };
                _promptSlider.ValueChanged += OnPromptVolumeChanged;
                _promptValue = new TextBlock { MinWidth = 20, VerticalAlignment = VerticalAlignment.Center };
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                panel.Children.Add(_promptSlider);
                panel.Children.Add(_promptValue);
                _promptRow = new SettingRow { Name = "PromptVolumeSection", Header = Loc.Get("Prompt_Title/Header"), Action = panel };
                _rows.Children.Add(_promptRow);
            }
            // Another model: a volume still waiting was meant for the previous earbuds.
            _promptWrite?.Cancel();
            var profile = Features.State.Profile;
            bool earFun = profile is EarFunProfile;
            var (min, max) = profile is MoondropProfile or EarFunProfile ? (0, 100) : Features.Model?.PromptVolumeRange ?? (1, 10);
            // A new range can re-coerce the value and raise ValueChanged; that is not a user edit.
            bool was = _syncing;
            _syncing = true;
            try
            {
                _promptSlider!.Minimum = min;
                _promptSlider.Maximum = max;
                _promptSlider.StepFrequency = earFun ? 25 : 1;
                _promptSlider.TickFrequency = earFun ? 25 : 1;
            }
            finally { _syncing = was; }
        }
    }

    // ---- syncing -------------------------------------------------------------------------------

    private void Sync()
    {
        if (!_attached) return;
        var f = Features;
        bool ready = f.IsReady;
        BuildSpecialRows();
        _syncing = true;
        try
        {
            bool any = false;
            foreach (var (toggle, sw, row) in _switches)
            {
                // MOONDROP lists a switch only once the earbuds reported it (and its capability).
                bool moondrop = f.IsMoondrop && toggle.Toggle is not (DeviceToggle.DualDeviceConnection or DeviceToggle.HeadTracking);
                bool supported = !moondrop || f.Has(MoondropProfile.ToggleCapability(toggle.Toggle));
                bool visible = !moondrop || (ready && toggle.IsKnown && f.HasSwitch(toggle.Toggle) && supported);
                row.Visibility = Show(visible);
                any |= visible;
                sw.IsOn = toggle.IsOn;
                sw.IsEnabled = toggle.IsKnown && supported && !toggle.IsPending && !_writes.Contains($"toggle:{toggle.Toggle}")
                    && (!moondrop || toggle.Toggle != DeviceToggle.PromptTone || (!f.IsPending("prompt-tone") && !f.IsPending("prompt-volume")));
            }
            any |= SyncOptions(f, ready);
            any |= SyncGain(f, ready);
            any |= SyncPromptVolume(f, ready);
            Visibility = Show(any);
            if (any != _hasRows)
            {
                _hasRows = any;
                HasRowsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private bool SyncGain(FeaturesViewModel f, bool ready)
    {
        if (_gainRow is null || _gainBox is null) return false;
        bool gain = f.IsMoondrop && ready && f.Has(DeviceCapability.Gain) && f.State.Gain?.Value is >= 0 and <= 2;
        _gainRow.Visibility = Show(gain);
        _gainBox.SelectedIndex = gain ? f.State.Gain!.Value : -1;
        _gainBox.IsEnabled = gain && !f.IsPending("gain") && !_writes.Contains("gain");
        return gain;
    }

    private bool SyncPromptVolume(FeaturesViewModel f, bool ready)
    {
        if (_promptRow is null || _promptSlider is null || _promptValue is null) return false;
        bool shown = f.Has(DeviceCapability.PromptVolume) && f.State.PromptVolume is not null;
        _promptRow.Visibility = Show(shown);
        // Don't fight the user: hold the slider while they drag and while the write is in flight.
        if (_promptWrite?.IsPending != true && !f.IsPending("prompt-volume") && f.State.PromptVolume?.Value is int volume) _promptSlider.Value = volume;
        _promptValue.Text = ((int)Math.Round(_promptSlider.Value)).ToString(System.Globalization.CultureInfo.CurrentCulture);
        _promptSlider.IsEnabled = ready && !f.IsPending("prompt-volume")
            && (!f.IsMoondrop || (!f.IsPending("prompt-tone") && !f.IsPending("toggle:PromptTone") && !_writes.Contains("toggle:PromptTone")));
        return shown;
    }

    /// <summary>Device-reported options of this group: a list or a slider each, always showing the confirmed value.</summary>
    private bool SyncOptions(FeaturesViewModel f, bool ready)
    {
        var options = f.Options.Where(o => SettingGroups.Of(o) == Group).ToArray();
        string signature = string.Join('|', options.Select(o =>
            $"{o.Id}/{o.LabelKey}/{o.DescriptionKey}/{o.Minimum}/{o.Maximum}/{string.Join(',', o.Choices.Select(c => $"{c.Value}:{c.LabelKey}:{c.Argument}"))}"));
        if (signature != _optionSignature)
        {
            _optionSignature = signature;
            foreach (var (_, _, _, row) in _options) _rows.Children.Remove(row);
            _options.Clear();
            int at = _switches.Count;
            foreach (var option in options)
            {
                FrameworkElement control;
                FrameworkElement action;
                TextBlock? valueText = null;
                if (option.IsRange)
                {
                    var slider = new Slider
                    {
                        Width = (double)Application.Current.Resources["RowSliderWidth"], Minimum = option.Minimum, Maximum = option.Maximum,
                        StepFrequency = 1, TickFrequency = 1, TickPlacement = TickPlacement.BottomRight,
                        VerticalAlignment = VerticalAlignment.Center, Tag = option.Id,
                    };
                    slider.ValueChanged += OnOptionSliderChanged;
                    valueText = new TextBlock { MinWidth = 20, VerticalAlignment = VerticalAlignment.Center };
                    var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                    panel.Children.Add(slider);
                    panel.Children.Add(valueText);
                    control = slider;
                    action = panel;
                }
                else
                {
                    var box = new ComboBox { MinWidth = 200, Tag = option.Id };
                    foreach (var choice in option.Choices)
                        box.Items.Add(new ComboBoxItem { Content = choice.Argument is null ? Loc.Get(choice.LabelKey) : Loc.Format(choice.LabelKey, choice.Argument), Tag = choice.Value });
                    box.SelectionChanged += OnOptionChanged;
                    control = action = box;
                }
                var row = new SettingRow { Header = Loc.Get(option.LabelKey), Description = option.DescriptionKey is { } key ? Loc.Get(key) : null, Action = action };
                _rows.Children.Insert(at++, row);
                _options.Add((option.Id, control, valueText, row));
            }
        }
        foreach (var (id, control, valueText, _) in _options)
        {
            var option = options.First(o => o.Id == id);
            bool busy = _writes.Contains(id);
            if (control is Control enabled) enabled.IsEnabled = ready && !busy;
            if (control is ComboBox box)
                box.SelectedIndex = option.Choices.ToList().FindIndex(c => c.Value == option.Value);
            else if (control is Slider slider && !busy && _optionTarget?.Id != id)
                slider.Value = option.Value;
            if (valueText is not null && control is Slider shown)
                valueText.Text = ((int)Math.Round(shown.Value)).ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
        return options.Length > 0;
    }

    // ---- handlers ------------------------------------------------------------------------------

    private async void OnSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded || sender is not ToggleSwitch { Tag: DeviceToggleViewModel toggle } sw || !sw.IsEnabled) return;
        bool requested = sw.IsOn;
        string key = $"toggle:{toggle.Toggle}";
        var f = Features;
        bool moondrop = f.IsMoondrop && toggle.Toggle is not (DeviceToggle.DualDeviceConnection or DeviceToggle.HeadTracking);
        if (moondrop && (!f.IsReady || !toggle.IsKnown || !f.HasSwitch(toggle.Toggle)
            || !f.Has(MoondropProfile.ToggleCapability(toggle.Toggle)) || !_writes.Add(key))) return;
        Sync(); // the device decides; snap back until it confirms
        try { await ViewModel.SetToggleAsync(toggle.Toggle, requested); }
        finally
        {
            if (moondrop) _writes.Remove(key);
            Sync();
        }
    }

    private async void OnOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !IsLoaded || sender is not ComboBox { IsEnabled: true, Tag: string id, SelectedItem: ComboBoxItem { Tag: int value } }
            || Features.Options.FirstOrDefault(o => o.Id == id) is not { } option || option.Value == value || !_writes.Add(id)) return;
        Sync();
        try { await Features.SetOptionAsync(id, value); }
        finally { _writes.Remove(id); Sync(); }
    }

    private void OnOptionSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !IsLoaded || sender is not Slider { Tag: string id } slider) return;
        if (_options.FirstOrDefault(c => c.Id == id).Value is { } text)
            text.Text = ((int)Math.Round(slider.Value)).ToString(System.Globalization.CultureInfo.CurrentCulture);
        _optionTarget = (id, (int)Math.Round(slider.Value));
        // Written 500 ms after the slider stops, like the prompt volume.
        _optionWrite ??= new Debouncer(DispatcherQueue, SliderDebounce, async refresh =>
        {
            if (_optionTarget is not { } target) return;
            if (Features.Options.FirstOrDefault(o => o.Id == target.Id) is not { } option || option.Value == target.Value || !_writes.Add(target.Id))
            {
                _optionTarget = null;
                if (refresh) Sync();
                return;
            }
            try { await Features.SetOptionAsync(target.Id, target.Value); }
            finally
            {
                _writes.Remove(target.Id);
                _optionTarget = null;
                if (refresh) Sync();
            }
        });
        _optionWrite.Restart();
    }

    private async void OnGainChanged(object sender, SelectionChangedEventArgs e)
    {
        var f = Features;
        int level = _gainBox?.SelectedIndex ?? -1;
        if (_syncing || !IsLoaded || _gainBox is not { IsEnabled: true } || !f.IsMoondrop || !f.IsReady || !f.Has(DeviceCapability.Gain)
            || f.State.Gain?.Value is not (>= 0 and <= 2) || level is < 0 or > 2 || level == f.State.Gain?.Value
            || !_writes.Add("gain")) return;
        Sync();
        try { await f.SetGainAsync(level); }
        finally { _writes.Remove("gain"); Sync(); }
    }

    private void OnPromptVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Setting the range raises ValueChanged too: that is not the user, and must never write a volume.
        if (_syncing || !IsLoaded || _promptSlider is null || _promptValue is null) return;
        _promptValue.Text = ((int)Math.Round(e.NewValue)).ToString(System.Globalization.CultureInfo.CurrentCulture);
        // Official: written 500 ms after the slider stops.
        _promptWrite ??= new Debouncer(DispatcherQueue, SliderDebounce, async refresh =>
        {
            await Features.SetPromptVolumeAsync((int)Math.Round(_promptSlider.Value));
            if (refresh) Sync();
        });
        _promptWrite.Restart();
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
