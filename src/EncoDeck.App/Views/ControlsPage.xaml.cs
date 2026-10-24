// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using EncoDeck.App.Controls;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Protocol.Brands.Sennheiser;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Views;

/// <summary>
/// Game mode, dual connection, gestures, head gestures, find earbuds, fit test and the remaining
/// switches. Controls always show what the earbuds last confirmed; a change snaps back until the
/// earbuds acknowledge it and the official re-read reports the new value.
/// </summary>
public sealed partial class ControlsPage : Page
{
    private static readonly TimeSpan PromptVolumeDebounce = TimeSpan.FromMilliseconds(500);

    private readonly List<(DeviceToggleViewModel Toggle, ToggleSwitch Switch, SettingRow Row)> _switches = [];
    private readonly List<(int Type, RadioButton Button)> _gameSoundButtons = [];
    private readonly List<(OppoGestureRow Row, ComboBox Box)> _gestureBoxes = [];
    private readonly List<(EarSide Side, GestureAction Action, ComboBox Box, SettingRow Row)> _moondropGestures = [];
    private readonly List<(EarSide Side, GestureAction Action, ComboBox Box, SettingRow Row)> _sennheiserGestures = [];
    private bool _sennheiserGestureWrite;
    private readonly HashSet<string> _moondropWrites = [];
    private readonly List<(uint Bit, CheckBox Box)> _cycleBoxes = [];
    private readonly List<byte[]?> _preferredItems = [];
    private DispatcherQueueTimer? _promptVolumeTimer;
    private string? _builtFor;
    private string _sourceSignature = string.Empty;
    private bool _syncing;

    public ControlsPage()
    {
        InitializeComponent();
        EarSelector.Items.Add(Loc.Get("Gesture_Left"));
        EarSelector.Items.Add(Loc.Get("Gesture_Right"));
        EarSelector.SelectedIndex = 0;
        HeadMotionChoice.Items.Add(Loc.Get("Head_NodAnswers"));
        HeadMotionChoice.Items.Add(Loc.Get("Head_ShakeAnswers"));
        foreach (string key in new[] { "Moondrop_GainLow", "Moondrop_GainMid", "Moondrop_GainHigh" }) GainBox.Items.Add(Loc.Get(key));
        Loaded += (_, _) =>
        {
            ViewModel.Toggles.CollectionChanged += OnTogglesChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            Features.Changed += OnFeaturesChanged;
            RebuildToggles();
            BuildForModel(force: true);
            Sync();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.Toggles.CollectionChanged -= OnTogglesChanged;
            ViewModel.PropertyChanged -= OnViewModelChanged;
            Features.Changed -= OnFeaturesChanged;
            foreach (var (toggle, _, _) in _switches) toggle.PropertyChanged -= OnToggleChanged;
            _promptVolumeTimer?.Stop();
        };
    }

    public DeviceViewModel ViewModel => App.Instance.Device;

    private FeaturesViewModel Features => App.Instance.Features;

    private EarSide SelectedEar => EarSelector.SelectedIndex == 1 ? EarSide.Right : EarSide.Left;

    private void OnTogglesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RebuildToggles();
        Sync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceViewModel.ModelName) or nameof(DeviceViewModel.ProfileKey)) BuildForModel(force: false);
        Sync();
    }

    private void OnToggleChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnFeaturesChanged(object? sender, EventArgs e)
    {
        BuildForModel(force: false);
        Sync();
    }

    // ---- building ------------------------------------------------------------------------------

    private void RebuildToggles()
    {
        foreach (var (toggle, _, _) in _switches) toggle.PropertyChanged -= OnToggleChanged;
        _switches.Clear();
        ToggleHost.Children.Clear();
        foreach (var toggle in ViewModel.Toggles)
        {
            var sw = new ToggleSwitch { Name = $"Toggle{toggle.Toggle}", Style = (Style)Application.Current.Resources["TrailingSwitchStyle"], Tag = toggle };
            sw.Toggled += OnSwitchToggled;
            toggle.PropertyChanged += OnToggleChanged;
            var row = new SettingRow { Header = toggle.Label, Description = toggle.Description, Action = sw };
            _switches.Add((toggle, sw, row));
            ToggleHost.Children.Add(row);
        }
    }

    /// <summary>Builds the per-model rows once; values are filled in by <see cref="Sync"/>.</summary>
    private void BuildForModel(bool force)
    {
        var model = Features.Model;
        string key = ViewModel.ProfileKey;
        if (!force && key == _builtFor) return;
        _builtFor = key;
        _promptVolumeTimer?.Stop();

        _gameSoundButtons.Clear();
        GameSoundHost.Children.Clear();
        foreach (int type in model?.GameSoundTypes ?? [])
        {
            var button = new RadioButton { Content = Loc.Get($"GameSound_{type}"), GroupName = "GameSound", Tag = type };
            button.Click += OnGameSoundClick;
            _gameSoundButtons.Add((type, button));
            GameSoundHost.Children.Add(button);
        }

        _gestureBoxes.Clear();
        _moondropGestures.Clear();
        _sennheiserGestures.Clear();
        GestureHost.Children.Clear();
        CallGestureHost.Children.Clear();
        foreach (var row in Features.GestureRows(callControls: false)) GestureHost.Children.Add(GestureRow(row));
        foreach (var row in Features.GestureRows(callControls: true)) CallGestureHost.Children.Add(GestureRow(row));
        if (Features.IsSennheiserTw4)
        {
            foreach (var side in new[] { EarSide.Left, EarSide.Right })
            foreach (var action in SennheiserGestures.Actions)
            {
                var box = new ComboBox { Name = $"Sennheiser{side}{action}", MinWidth = 200, Tag = (side, action) };
                foreach (int function in SennheiserGestures.Functions(action))
                    box.Items.Add(new ComboBoxItem { Content = Loc.Get(SennheiserGestures.LabelKey(function)), Tag = function });
                box.Items.Add(new ComboBoxItem { IsEnabled = false }); // Preserve unrecognised device assignments in the display.
                box.SelectionChanged += OnSennheiserGestureChanged;
                string label = action switch
                {
                    GestureAction.SingleSqueeze => "Gesture_Tap1", GestureAction.DoubleSqueeze => "Gesture_Tap2",
                    GestureAction.TripleSqueeze => "Gesture_Tap3", _ => "Sennheiser_Hold",
                };
                var row = new SettingRow
                {
                    Header = $"{Loc.Get(side == EarSide.Left ? "Gesture_Left" : "Gesture_Right")} · {Loc.Get(label)}",
                    Action = box,
                };
                _sennheiserGestures.Add((side, action, box, row));
                GestureHost.Children.Add(row);
            }
        }
        if (Features.IsMoondrop)
        {
            foreach (var side in new[] { EarSide.Left, EarSide.Right })
            foreach (var (action, label) in new[]
            {
                (GestureAction.SingleSqueeze, "Gesture_Tap1"),
                (GestureAction.DoubleSqueeze, "Gesture_Tap2"),
                (GestureAction.TripleSqueeze, "Gesture_Tap3"),
                (GestureAction.SqueezeAndHold, "Moondrop_Hold1"),
                (GestureAction.LongHold, "Moondrop_Hold3"),
            })
            {
                var box = new ComboBox { Name = $"Moondrop{side}{action}", MinWidth = 200, Tag = (side, action) };
                for (int function = 0; function <= 15; function++)
                    box.Items.Add(new ComboBoxItem
                    {
                        Content = function <= 7 ? Loc.Get($"Moondrop_Function_{function}") : Loc.Format("Moondrop_UnknownAction", function.ToString("X")),
                        IsEnabled = function <= 7 && (function is not (4 or 5) || Features.AllowExperimental),
                    });
                box.SelectionChanged += OnMoondropGestureChanged;
                var row = new SettingRow
                {
                    Header = $"{Loc.Get(side == EarSide.Left ? "Gesture_Left" : "Gesture_Right")} · {Loc.Get(label)}",
                    Action = box,
                };
                _moondropGestures.Add((side, action, box, row));
                GestureHost.Children.Add(row);
            }
        }

        _cycleBoxes.Clear();
        CycleHost.Children.Clear();
        // The model's top-level modes in the official order (bit = 1 << protocol index).
        foreach (int index in model?.NoiseCycleIndices ?? [])
        {
            string label = model!.NoiseCycleLabels.TryGetValue(index, out var labelKey) ? labelKey : "NoiseMode_NoiseCancellation";
            uint bit = 1u << index;
            var box = new CheckBox { Content = Loc.Get(label), Tag = bit, MinWidth = 0 };
            box.Click += OnCycleClick;
            _cycleBoxes.Add((bit, box));
            CycleHost.Children.Add(box);
        }
        var hold = model?.GestureRows.FirstOrDefault(r => r.EditsNoiseCycle);
        CycleTitle.Text = Loc.Format("Gesture_CycleTitle", hold is null ? string.Empty : Loc.Get(hold.ResourceKey));
        CycleNote.Text = Loc.Format("Gesture_CycleNote", model?.NoiseCycleMinimum ?? 2);
        CallGestureTitle.Visibility = Show(Features.GestureRows(callControls: true).Count > 0);
        // One setting for both earbuds everywhere: no left/right choice.
        bool perEar = model is not null && (model.GestureRows.Any(r => r.IsWritten && !r.IsCallControl && !r.BothEars) || (model.NoiseCycleIndices.Count > 0 && !model.NoiseCycleShared));
        EarSelector.Visibility = Show(perEar);
        var (minVolume, maxVolume) = Features.State.Profile is EncoDeck.Protocol.Brands.Moondrop.MoondropProfile
            ? (0, 100) : model?.PromptVolumeRange ?? (1, 10);
        // Changing the range can un-coerce a previous value and raise ValueChanged; it is not a user edit.
        bool wasSyncing = _syncing;
        _syncing = true;
        try
        {
            PromptVolumeSlider.Minimum = minVolume;
            PromptVolumeSlider.Maximum = maxVolume;
        }
        finally { _syncing = wasSyncing; }
    }

    private Grid GestureRow(OppoGestureRow row)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = Loc.Get(row.ResourceKey), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var box = new ComboBox { MinWidth = 200, Tag = row };
        foreach (int function in row.AllowedFunctions) box.Items.Add(Loc.Get(Features.FunctionLabelKey(function)));
        box.SelectionChanged += OnGestureChanged;
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        _gestureBoxes.Add((row, box));
        return grid;
    }

    // ---- syncing -------------------------------------------------------------------------------

    private void Sync()
    {
        var f = Features;
        bool ready = f.IsReady;
        _syncing = true;
        try
        {
            NotReadyBar.IsOpen = !ready && ViewModel.HasDevice;

            foreach (var (toggle, sw, row) in _switches)
            {
                bool moondropSetting = f.IsMoondrop && toggle.Toggle is not (DeviceToggle.DualDeviceConnection or DeviceToggle.HeadTracking);
                bool supported = !moondropSetting || f.Has(EncoDeck.Protocol.Brands.Moondrop.MoondropProfile.ToggleCapability(toggle.Toggle));
                row.Visibility = Show(!moondropSetting || (ready && toggle.IsKnown && f.HasSwitch(toggle.Toggle) && supported));
                sw.IsOn = toggle.IsOn;
                sw.IsEnabled = toggle.IsKnown && supported && !toggle.IsPending && !_moondropWrites.Contains($"toggle:{toggle.Toggle}")
                    && (!moondropSetting || toggle.Toggle != DeviceToggle.PromptTone || (!f.IsPending("prompt-tone") && !f.IsPending("prompt-volume")));
            }

            bool gain = f.IsMoondrop && ready && f.Has(DeviceCapability.Gain) && f.State.Gain?.Value is >= 0 and <= 2;
            GainSection.Visibility = Show(gain);
            GainBox.SelectedIndex = gain ? f.State.Gain!.Value : -1;
            GainBox.IsEnabled = gain && !f.IsPending("gain") && !_moondropWrites.Contains("gain");

            SyncGame(f, ready);
            SyncDual(f, ready);
            SyncGestures(f, ready);
            SyncHead(f, ready);
            SyncFind(f, ready);
            SyncFit(f, ready);

            PromptVolumeSection.Visibility = Show(f.Has(DeviceCapability.PromptVolume) && f.State.PromptVolume is not null);
            // Don't fight the user: hold the slider while they drag and while the write is in flight.
            if (_promptVolumeTimer?.IsRunning != true && !f.IsPending("prompt-volume") && f.State.PromptVolume?.Value is int volume) PromptVolumeSlider.Value = volume;
            PromptVolumeValue.Text = ((int)Math.Round(PromptVolumeSlider.Value)).ToString(System.Globalization.CultureInfo.CurrentCulture);
            PromptVolumeSlider.IsEnabled = ready && !f.IsPending("prompt-volume")
                && (!f.IsMoondrop || (!f.IsPending("prompt-tone") && !f.IsPending("toggle:PromptTone") && !_moondropWrites.Contains("toggle:PromptTone")));
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncGame(FeaturesViewModel f, bool ready)
    {
        bool available = f.Has(DeviceCapability.GamingMode) && f.HasSwitch(DeviceToggle.GamingMode);
        GameCard.Visibility = Show(available);
        GameOptions.Visibility = Show(!f.IsSennheiserTw4);
        GameTitle.Text = Loc.Get(f.IsSennheiserTw4 ? "Sennheiser_LowLatency" : "Game_Title.Text");
        GameSummary.Text = Loc.Get(f.IsSennheiserTw4 ? "Sennheiser_LowLatencyDescription" : "Game_Summary.Text");
        if (!available) return;
        bool? master = f.Toggle(DeviceToggle.GamingMode);
        bool busy = f.IsPending("toggle:") || f.IsPending("game-sound");
        GameModeSwitch.IsOn = master == true;
        GameModeSwitch.IsEnabled = ready && master is not null && !busy;
        // Official: the sub-options are greyed out while game mode is off.
        bool subEnabled = ready && master == true && !busy;
        LowLatencySwitch.IsOn = f.Toggle(DeviceToggle.LowLatency) == true;
        LowLatencySwitch.IsEnabled = subEnabled && f.Toggle(DeviceToggle.LowLatency) is not null;
        GameSoundSection.Visibility = Show(f.State.GameSound is not null && _gameSoundButtons.Count > 0);
        var offered = f.GameSoundOptions;
        foreach (var (type, button) in _gameSoundButtons)
        {
            button.Visibility = Show(offered.Contains(type));
            button.IsChecked = f.GameSoundType == type;
            button.IsEnabled = subEnabled;
        }
    }

    private void SyncDual(FeaturesViewModel f, bool ready)
    {
        bool available = f.Has(DeviceCapability.DualDeviceConnection) && f.HasSwitch(DeviceToggle.DualDeviceConnection);
        DualCard.Visibility = Show(available);
        if (!available) return;
        bool? enabled = f.Toggle(DeviceToggle.DualDeviceConnection);
        DualSwitch.IsOn = enabled == true;
        DualSwitch.IsEnabled = ready && enabled is not null && !f.IsPending("toggle:DualDeviceConnection");

        var devices = f.SourceDevices;
        string signature = string.Join('|', devices.Select(d => $"{d.AddressText}/{d.State}/{d.IsThisDevice}/{d.IsAudioActive}/{d.Name}"))
            + $"#{ready}#{Convert.ToHexString(f.PreferredAddress ?? [])}#{f.IsPreferredKnown}";
        if (signature == _sourceSignature) return;
        _sourceSignature = signature;

        SourceHost.Children.Clear();
        foreach (var device in devices) SourceHost.Children.Add(SourceRow(device, ready));

        // Official options: auto select, the connected devices, and the current preferred device.
        _preferredItems.Clear();
        PreferredBox.Items.Clear();
        _preferredItems.Add(null);
        PreferredBox.Items.Add(Loc.Get("Dual_AutoSelect"));
        foreach (var device in devices.Where(d => d.IsConnected || (f.PreferredAddress is { } p && d.Address.AsSpan().SequenceEqual(p))))
        {
            _preferredItems.Add(device.Address);
            PreferredBox.Items.Add(DisplayName(device));
        }
        int selected = f.PreferredAddress is { } preferred
            ? _preferredItems.FindIndex(a => a is not null && a.AsSpan().SequenceEqual(preferred))
            : 0;
        PreferredBox.SelectedIndex = f.IsPreferredKnown ? selected : -1;
        PreferredSection.Visibility = Show(f.IsPreferredKnown || devices.Count > 0);
        PreferredBox.IsEnabled = ready && f.IsPreferredKnown && !f.IsPending("preferred-device");
    }

    private Grid SourceRow(SourceDevice device, bool ready)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Device type icons: 1 phone, 2 computer, 3 tablet, 4 watch.
        string glyph = device.DeviceType switch { 1 => "", 2 => "", 3 => "", 4 => "", _ => "" };
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = DisplayName(device), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(device.Name) ? DescribeSource(device) : $"{DescribeSource(device)} · {device.AddressText}",
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            IsTextSelectionEnabled = true,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        bool busy = device.State is 1 or 4;
        if (!device.IsConnected && !busy)
        {
            actions.Children.Add(ActionButton("Dual_Connect", device, ready, OnConnectSourceClick));
            actions.Children.Add(ActionButton("Dual_Remove", device, ready, OnRemoveSourceClick));
        }
        else if (device.IsConnected && !device.IsThisDevice)
        {
            // This PC disconnects through Windows, as the official client does for the phone.
            actions.Children.Add(ActionButton("Dual_Disconnect", device, ready, OnDisconnectSourceClick));
        }
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return grid;
    }

    private static Button ActionButton(string key, SourceDevice device, bool ready, RoutedEventHandler handler)
    {
        var button = new Button { Content = Loc.Get(key), Tag = device, IsEnabled = ready };
        button.Click += handler;
        return button;
    }

    private void SyncGestures(FeaturesViewModel f, bool ready)
    {
        if (f.IsSennheiserTw4)
        {
            bool tw4Available = f.Has(DeviceCapability.TouchCustomization)
                && _sennheiserGestures.Any(row => f.SennheiserFunction(row.Side, row.Action) is not null);
            GestureCard.Visibility = Show(tw4Available);
            EarSelector.Visibility = CycleSection.Visibility = CallGestureTitle.Visibility = Visibility.Collapsed;
            foreach (var (side, action, box, row) in _sennheiserGestures)
            {
                int? function = f.SennheiserFunction(side, action);
                row.Visibility = Show(tw4Available && function is not null);
                var items = box.Items.Cast<ComboBoxItem>().ToList();
                int selected = items.FindIndex(item => item.Tag is int code && code == function);
                if (function is int unknown && selected < 0)
                {
                    items[^1].Content = Loc.Format("Sennheiser_UnknownAction", unknown.ToString("X2"));
                    selected = items.Count - 1;
                }
                box.SelectedIndex = selected;
                box.IsEnabled = ready && tw4Available && function is not null && !f.IsPending("key-function") && !_sennheiserGestureWrite;
            }
            return;
        }
        if (f.IsMoondrop)
        {
            bool moondropAvailable = ready && f.Has(DeviceCapability.TouchCustomization)
                && _moondropGestures.Any(row => f.MoondropFunction(row.Side, row.Action) is >= 0 and <= 15);
            GestureCard.Visibility = Show(moondropAvailable);
            EarSelector.Visibility = Visibility.Collapsed;
            CycleSection.Visibility = Visibility.Collapsed;
            CallGestureTitle.Visibility = Visibility.Collapsed;
            foreach (var (side, action, box, row) in _moondropGestures)
            {
                int? function = f.MoondropFunction(side, action);
                bool known = function is >= 0 and <= 15;
                row.Visibility = Show(moondropAvailable && known);
                box.SelectedIndex = known ? function!.Value : -1;
                box.IsEnabled = moondropAvailable && known && !f.IsPending("key-function") && !_moondropWrites.Contains("gesture");
                ((ComboBoxItem)box.Items[4]).IsEnabled = f.AllowExperimental;
                ((ComboBoxItem)box.Items[5]).IsEnabled = f.AllowExperimental;
            }
            return;
        }
        bool hasCycle = f.Model is { NoiseCycleIndices.Count: > 0 } && _cycleBoxes.Count > 0;
        bool available = (f.Has(DeviceCapability.TouchCustomization) && _gestureBoxes.Count > 0) || hasCycle;
        GestureCard.Visibility = Show(available);
        if (!available) return;
        bool enabled = ready && f.State.KeyFunctions is not null && !f.IsPending("gesture");
        foreach (var (row, box) in _gestureBoxes)
        {
            int? function = f.CurrentFunction(row, SelectedEar);
            box.SelectedIndex = function is int code ? IndexOf(row.AllowedFunctions, code) : -1;
            box.IsEnabled = enabled;
        }

        // The long-press cycle editor applies while squeeze-and-hold is set to noise control.
        bool showCycle = f.HoldControlsNoise(SelectedEar) && f.CycleMask(SelectedEar) is not null;
        CycleSection.Visibility = Show(showCycle);
        uint mask = f.CycleMask(SelectedEar) ?? 0;
        foreach (var (bit, box) in _cycleBoxes)
        {
            box.IsChecked = (mask & bit) != 0;
            box.IsEnabled = ready && !f.IsPending("noise-cycle");
        }
    }

    private void SyncHead(FeaturesViewModel f, bool ready)
    {
        bool available = f.HasSwitch(DeviceToggle.HeadGestures);
        HeadCard.Visibility = Show(available);
        if (!available) return;
        bool? on = f.Toggle(DeviceToggle.HeadGestures);
        HeadSwitch.IsOn = on == true;
        HeadSwitch.IsEnabled = ready && on is not null && !f.IsPending("toggle:HeadGestures");
        HeadMotionChoice.Visibility = Show(on == true && f.State.HeadMotionType is not null);
        HeadMotionChoice.SelectedIndex = f.State.HeadMotionType?.Value ?? -1;
        HeadMotionChoice.IsEnabled = ready && !f.IsPending("head-motion");
    }

    private void SyncFind(FeaturesViewModel f, bool ready)
    {
        FindCard.Visibility = Show(f.Has(DeviceCapability.FindMyEarbuds));
        FindButton.Content = Loc.Get(f.State.IsFindingEarbuds ? "Find_Stop" : "Find_Play");
        FindButton.IsEnabled = ready;
    }

    private void SyncFit(FeaturesViewModel f, bool ready)
    {
        FitCard.Visibility = Show(f.Has(DeviceCapability.FitTest));
        bool running = f.State.IsFitTestRunning;
        FitProgress.IsActive = running;
        FitButton.IsEnabled = ready && !running;
        FitResultText.Text = running ? Loc.Get("Fit_Running") : f.State.FitTest is { } result ? DescribeFit(result) : string.Empty;
    }

    // ---- handlers ------------------------------------------------------------------------------

    private async void OnSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded || sender is not ToggleSwitch { Tag: DeviceToggleViewModel toggle } sw || !sw.IsEnabled) return;
        bool requested = sw.IsOn;
        string key = $"toggle:{toggle.Toggle}";
        bool moondrop = Features.IsMoondrop && toggle.Toggle is not (DeviceToggle.DualDeviceConnection or DeviceToggle.HeadTracking);
        if (moondrop && (!Features.IsReady || !toggle.IsKnown || !Features.HasSwitch(toggle.Toggle)
            || !Features.Has(EncoDeck.Protocol.Brands.Moondrop.MoondropProfile.ToggleCapability(toggle.Toggle))
            || !_moondropWrites.Add(key))) return;
        Sync(); // the device decides; snap back until it confirms
        try { await ViewModel.SetToggleAsync(toggle.Toggle, requested); }
        finally
        {
            if (moondrop) _moondropWrites.Remove(key);
            Sync();
        }
    }

    private async void OnGameModeToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool requested = GameModeSwitch.IsOn;
        Sync();
        await Features.SetGameModeAsync(requested);
        Sync();
    }

    private async void OnLowLatencyToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool requested = LowLatencySwitch.IsOn;
        Sync();
        await Features.SetLowLatencyAsync(requested);
        Sync();
    }

    private async void OnGameSoundClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: int type } || type == Features.GameSoundType) return;
        Sync();
        await Features.SetGameSoundAsync(type);
        Sync();
    }

    private async void OnDualToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool requested = DualSwitch.IsOn;
        Sync();
        await Features.SetToggleAsync(DeviceToggle.DualDeviceConnection, requested);
        Sync();
    }

    private async void OnConnectSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SourceDevice device }) return;
        // Official: with two devices already connected, connecting a third drops one of them.
        if (Features.SourceDevices.Count(d => d.IsConnected) >= 2
            && !await ConfirmAsync("Dual_ConnectNew_Title", Loc.Format("Dual_ConnectNew_Message", DisplayName(device)), "Dual_Connect"))
        {
            return;
        }
        await Features.MultiConnectAsync(MultiConnectOperation.Connect, device);
        Sync();
    }

    private async void OnDisconnectSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SourceDevice device }) return;
        if (!await ConfirmAsync("Dual_Disconnect_Title", Loc.Format("Dual_Disconnect_Message", DisplayName(device)), "Dual_Disconnect")) return;
        await Features.MultiConnectAsync(MultiConnectOperation.Disconnect, device);
        Sync();
    }

    private async void OnRemoveSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SourceDevice device }) return;
        if (!await ConfirmAsync("Dual_Remove_Title", DisplayName(device), "Dual_Remove")) return;
        await Features.MultiConnectAsync(MultiConnectOperation.Remove, device);
        Sync();
    }

    private async void OnPreferredChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || PreferredBox.SelectedIndex < 0 || PreferredBox.SelectedIndex >= _preferredItems.Count) return;
        var address = _preferredItems[PreferredBox.SelectedIndex];
        _sourceSignature = string.Empty; // re-render from the confirmed state afterwards
        await Features.SetPreferredDeviceAsync(address);
        Sync();
    }

    private void OnEarChanged(object sender, SelectionChangedEventArgs e) => Sync();

    private async void OnGestureChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || sender is not ComboBox { Tag: OppoGestureRow row } box || box.SelectedIndex < 0) return;
        int function = row.AllowedFunctions[box.SelectedIndex];
        if (function == Features.CurrentFunction(row, SelectedEar)) return;
        var side = SelectedEar;
        Sync();
        await Features.SetGestureAsync(row, side, function);
        Sync();
    }

    private async void OnGainChanged(object sender, SelectionChangedEventArgs e)
    {
        var f = Features;
        int level = GainBox.SelectedIndex;
        if (_syncing || !IsLoaded || !GainBox.IsEnabled || !f.IsMoondrop || !f.IsReady || !f.Has(DeviceCapability.Gain)
            || f.State.Gain?.Value is not (>= 0 and <= 2) || level is < 0 or > 2 || level == f.State.Gain?.Value
            || !_moondropWrites.Add("gain")) return;
        Sync();
        try { await f.SetGainAsync(level); }
        finally { _moondropWrites.Remove("gain"); Sync(); }
    }

    private async void OnSennheiserGestureChanged(object sender, SelectionChangedEventArgs e)
    {
        var f = Features;
        if (_syncing || !IsLoaded || sender is not ComboBox { IsEnabled: true, Tag: ValueTuple<EarSide, GestureAction> target,
                SelectedItem: ComboBoxItem { IsEnabled: true, Tag: int function } }
            || !f.IsSennheiserTw4 || !f.IsReady || !f.Has(DeviceCapability.TouchCustomization)
            || f.SennheiserFunction(target.Item1, target.Item2) is not int current || current == function || _sennheiserGestureWrite) return;
        _sennheiserGestureWrite = true;
        Sync();
        try { await f.SetSennheiserGestureAsync(target.Item1, target.Item2, function); }
        finally { _sennheiserGestureWrite = false; Sync(); }
    }

    private async void OnMoondropGestureChanged(object sender, SelectionChangedEventArgs e)
    {
        var f = Features;
        if (_syncing || !IsLoaded || sender is not ComboBox { IsEnabled: true, Tag: ValueTuple<EarSide, GestureAction> target } box
            || !f.IsMoondrop || !f.IsReady || !f.Has(DeviceCapability.TouchCustomization)
            || f.MoondropFunction(target.Item1, target.Item2) is not (>= 0 and <= 15)
            || box.SelectedIndex is < 0 or > 7 || (box.SelectedIndex is 4 or 5 && !f.AllowExperimental)
            || box.SelectedIndex == f.MoondropFunction(target.Item1, target.Item2)
            || !_moondropWrites.Add("gesture")) return;
        int function = box.SelectedIndex;
        Sync();
        try { await f.SetMoondropGestureAsync(target.Item1, target.Item2, function); }
        finally { _moondropWrites.Remove("gesture"); Sync(); }
    }

    private async void OnCycleClick(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        uint mask = 0;
        foreach (var (bit, box) in _cycleBoxes)
        {
            if (box.IsChecked == true) mask |= bit;
        }
        var side = SelectedEar;
        Sync();
        await Features.SetNoiseCycleAsync(side, mask);
        Sync();
    }

    private async void OnHeadToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool requested = HeadSwitch.IsOn;
        Sync();
        await Features.SetToggleAsync(DeviceToggle.HeadGestures, requested);
        Sync();
    }

    private async void OnHeadMotionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !IsLoaded || !Features.HasSwitch(DeviceToggle.HeadGestures) || HeadMotionChoice.SelectedIndex < 0) return;
        int type = HeadMotionChoice.SelectedIndex;
        if (type == Features.State.HeadMotionType?.Value) return;
        Sync();
        await Features.SetHeadMotionTypeAsync(type);
        Sync();
    }

    private async void OnFindClick(object sender, RoutedEventArgs e)
    {
        bool ring = !Features.State.IsFindingEarbuds;
        // Official in-ear safety check: playing the tone while worn can damage hearing.
        if (ring && Features.IsAnyEarbudInEar && !await ConfirmAsync("Find_InEar_Title", Loc.Get("Find_InEar_Message"), "Find_Play"))
        {
            return;
        }
        FindButton.IsEnabled = false;
        var result = await Features.FindEarbudsAsync(ring);
        if (ring && !result.IsSuccess && Features.State.Wear?.Value.BothInCase == true)
        {
            ViewModel.ErrorMessage = Loc.Get("Find_InCase");
        }
        Sync();
    }

    private async void OnFitClick(object sender, RoutedEventArgs e)
    {
        FitButton.IsEnabled = false;
        await Features.RunFitTestAsync();
        Sync();
    }

    private void OnPromptVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Setting Minimum while the XAML loads raises ValueChanged too: that is not the user, and
        // must never write a volume to the earbuds (nor touch controls that do not exist yet).
        if (_syncing || !IsLoaded) return;
        PromptVolumeValue.Text = ((int)Math.Round(e.NewValue)).ToString(System.Globalization.CultureInfo.CurrentCulture);
        // Official: written 500 ms after the slider stops.
        _promptVolumeTimer ??= CreateTimer(PromptVolumeDebounce, async () =>
        {
            await Features.SetPromptVolumeAsync((int)Math.Round(PromptVolumeSlider.Value));
            Sync();
        });
        _promptVolumeTimer.Stop();
        _promptVolumeTimer.Start();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private DispatcherQueueTimer CreateTimer(TimeSpan interval, Func<Task> action)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += async (t, _) =>
        {
            t.Stop();
            await action();
        };
        return timer;
    }

    private async Task<bool> ConfirmAsync(string titleKey, string message, string primaryKey)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get(titleKey),
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get(primaryKey),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }


    private static string DisplayName(SourceDevice device) => string.IsNullOrWhiteSpace(device.Name) ? device.AddressText : device.Name;

    /// <summary>Row summaries as in the official dual-connection page.</summary>
    private static string DescribeSource(SourceDevice device) => device switch
    {
        { State: 1 } => Loc.Get("Dual_Connecting"),
        { IsThisDevice: true, IsConnected: true, IsAudioActive: true } => Loc.Get("Dual_InUseThis"),
        { IsThisDevice: true, IsConnected: true } => Loc.Get("Dual_ConnectedThis"),
        { IsThisDevice: true } => Loc.Get("Dual_This"),
        { IsConnected: true, IsAudioActive: true } => Loc.Get("Dual_InUse"),
        { IsConnected: true } => Loc.Get("Dual_Connected"),
        _ => Loc.Get("Dual_NotConnected"),
    };

    private static string DescribeFit(FitTestResult result)
    {
        if (result.Left == FitQuality.Failed || result.Right == FitQuality.Failed) return Loc.Get("Fit_Failed");
        if (result is { Left: FitQuality.Good, Right: FitQuality.Good }) return Loc.Get("Fit_Perfect");
        return $"{Loc.Get("Battery_Left")} {Loc.Get($"Fit_{result.Left}")} · {Loc.Get("Battery_Right")} {Loc.Get($"Fit_{result.Right}")} — {Loc.Get("Fit_Adjust")}";
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == value) return i;
        }
        return -1;
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
