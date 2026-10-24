// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.State;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace EncoDeck.App.Views;

/// <summary>
/// Earbud-side sound settings: EQ presets, custom EQ, the bass engine and spatial audio. Values shown
/// are always the ones the earbuds reported back after the official re-read.
/// </summary>
public sealed partial class SoundPage : Page
{
    // Official debounce for gain sliders (300 ms) and the bass slider (500 ms).
    private static readonly TimeSpan BandDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan BassDebounce = TimeSpan.FromMilliseconds(500);

    private readonly List<(EqualizerPresetViewModel Preset, RadioButton Button)> _buttons = [];
    private readonly List<(int Id, RadioButton Button)> _customButtons = [];
    private readonly List<(Slider Slider, TextBlock Value)> _bands = [];
    private readonly List<(SpatialAudioMode Mode, RadioButton Button)> _spatialButtons = [];
    private DispatcherQueueTimer? _bandTimer;
    private DispatcherQueueTimer? _bassTimer;
    private string _customSignature = string.Empty;
    private int? _editorPresetId;
    private int _bandCount = -1;
    private bool _syncing;
    private bool _dialogOpen;
    private string _spatialSignature = string.Empty;

    public SoundPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.EqualizerPresets.CollectionChanged += OnPresetsChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            Features.Changed += OnFeaturesChanged;
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.EqualizerPresets.CollectionChanged -= OnPresetsChanged;
            ViewModel.PropertyChanged -= OnViewModelChanged;
            Features.Changed -= OnFeaturesChanged;
            foreach (var (preset, _) in _buttons) preset.PropertyChanged -= OnPresetChanged;
            _bandTimer?.Stop();
            _bassTimer?.Stop();
        };
    }

    public DeviceViewModel ViewModel => App.Instance.Device;

    private FeaturesViewModel Features => App.Instance.Features;

    private void OnPresetsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnPresetChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnFeaturesChanged(object? sender, EventArgs e) => Sync();

    private void Rebuild()
    {
        foreach (var (preset, _) in _buttons) preset.PropertyChanged -= OnPresetChanged;
        _buttons.Clear();
        PresetHost.Children.Clear();
        foreach (var preset in ViewModel.EqualizerPresets)
        {
            var button = new RadioButton { Content = preset.Label, Tag = preset.Id, GroupName = "DeviceEq" };
            button.Click += OnPresetClick;
            preset.PropertyChanged += OnPresetChanged;
            _buttons.Add((preset, button));
            PresetHost.Children.Add(button);
        }
        _customSignature = string.Empty;
        Sync();
    }

    private void Sync()
    {
        var f = Features;
        bool ready = f.IsReady;
        _syncing = true;
        try
        {
            var selectedCustom = f.SelectedCustomEqualizer;
            bool eqBusy = f.IsPending("custom-eq") || f.IsPending("equalizer");
            EqCard.Visibility = Show(ViewModel.EqualizerPresets.Count > 0 || f.CustomEqualizerSpec is not null);
            foreach (var (preset, button) in _buttons)
            {
                // A selected custom preset replaces the built-in selection.
                button.IsChecked = selectedCustom is null && preset.IsSelected;
                button.IsEnabled = ready && !eqBusy;
            }
            SyncCustom(f, ready, eqBusy, selectedCustom);
            SyncBass(f, ready);
            SyncSpatial(f, ready);
            LayoutColumns();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncCustom(FeaturesViewModel f, bool ready, bool busy, CustomEqualizer? selected)
    {
        var spec = f.CustomEqualizerSpec;
        var presets = f.CustomEqualizers;
        string signature = string.Join('|', presets.Select(p => $"{p.Id}/{p.Name}"));
        if (signature != _customSignature)
        {
            _customSignature = signature;
            _customButtons.Clear();
            CustomHost.Children.Clear();
            foreach (var preset in presets)
            {
                var button = new RadioButton { Content = preset.Name, Tag = preset.Id, GroupName = "DeviceEq" };
                button.Click += OnCustomClick;
                _customButtons.Add((preset.Id, button));
                CustomHost.Children.Add(button);
            }
        }
        foreach (var (id, button) in _customButtons)
        {
            button.IsChecked = selected?.Id == id;
            button.IsEnabled = ready && !busy;
        }

        AddCustomButton.Visibility = Show(spec is not null && f.State.CustomEqualizers is not null);
        AddCustomButton.IsEnabled = ready && !busy && f.CanAddCustomEqualizer;
        CustomEditor.Visibility = Show(spec is not null && selected is not null);
        if (selected is null) return;

        if (_bandCount != selected.Bands.Count) BuildBands(selected, spec!.MinGain, spec.MaxGain);
        if (_editorPresetId != selected.Id)
        {
            _editorPresetId = selected.Id;
            CustomNameBox.Text = selected.Name;
        }
        // Never move a slider the user is still dragging (its write is pending).
        if (_bandTimer?.IsRunning != true)
        {
            for (int i = 0; i < _bands.Count && i < selected.Bands.Count; i++)
            {
                _bands[i].Slider.Value = selected.Bands[i].Gain;
                _bands[i].Value.Text = FormatGain(selected.Bands[i].Gain);
            }
        }
        foreach (var (slider, _) in _bands) slider.IsEnabled = ready;
        RenameButton.IsEnabled = ready && !busy;
        DeleteButton.IsEnabled = ready && !busy;
    }

    private void BuildBands(CustomEqualizer preset, int min, int max)
    {
        _bandCount = preset.Bands.Count;
        _bands.Clear();
        BandHost.Children.Clear();
        BandHost.ColumnDefinitions.Clear();
        for (int i = 0; i < preset.Bands.Count; i++)
        {
            BandHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var column = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] };
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = min,
                Maximum = max,
                StepFrequency = 1,
                TickFrequency = 1,
                TickPlacement = TickPlacement.Outside,
                Height = 160,
                HorizontalAlignment = HorizontalAlignment.Center,
                Tag = i,
            };
            slider.ValueChanged += OnBandChanged;
            column.Children.Add(value);
            column.Children.Add(slider);
            column.Children.Add(new TextBlock { Text = FormatFrequency(preset.Bands[i].FrequencyHz), HorizontalAlignment = HorizontalAlignment.Center, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
            Grid.SetColumn(column, i);
            BandHost.Children.Add(column);
            _bands.Add((slider, value));
        }
    }

    private void SyncBass(FeaturesViewModel f, bool ready)
    {
        bool available = f.Has(DeviceCapability.BassEngine) && f.HasSwitch(DeviceToggle.BassEngine);
        BassCard.Visibility = Show(available);
        if (!available) return;
        BassTitle.Text = Loc.Get(f.IsMoondrop ? "Moondrop_Bass_Title" : "Bass_Title.Text");
        BassSummary.Text = Loc.Get(f.IsMoondrop ? "Moondrop_Bass_Summary" : "Bass_Summary.Text");
        bool? on = f.Toggle(DeviceToggle.BassEngine);
        BassSwitch.IsOn = on == true;
        BassSwitch.IsEnabled = ready && on is not null && !f.IsPending("toggle:BassEngine");
        var bass = f.State.BassEngine?.Value;
        BassLevelRow.Visibility = Show(bass is not null && bass.Max > bass.Min);
        if (bass is null) return;
        BassSlider.Minimum = bass.Min;
        BassSlider.Maximum = bass.Max;
        // Official: incoming values are ignored while a write is pending.
        if (_bassTimer?.IsRunning != true && !f.IsPending("bass-engine")) BassSlider.Value = bass.Current;
        BassSlider.IsEnabled = ready && on == true;
    }

    private void SyncSpatial(FeaturesViewModel f, bool ready)
    {
        bool available = f.Has(DeviceCapability.SpatialAudio) && (f.Model is { SpatialTypes.Count: > 0 } || f.IsMoondrop) && f.State.Spatial is not null;
        SpatialCard.Visibility = Show(available);
        if (!available) return;

        // Official buttons: the model's spatial types; "fixed" reads "on" with spatial V1, which has no head tracking.
        var model = f.Model;
        var types = f.IsMoondrop
            ? new[] { 0, 1 }.Concat(f.Has(DeviceCapability.HeadTracking) ? new[] { 2 } : Array.Empty<int>()).ToArray()
            : model!.SpatialTypes.Where(t => t is 0 or 1 || (t == 2 && model.HasSpatialAudioV2)).Order().ToArray();
        string signature = $"{string.Join(',', types)}/{model?.HasSpatialAudioV2}";
        if (signature != _spatialSignature)
        {
            _spatialSignature = signature;
            _spatialButtons.Clear();
            SpatialHost.Children.Clear();
            foreach (int type in types)
            {
                string label = Loc.Get(type switch { 0 => "Spatial_Off", 1 => f.IsMoondrop || model?.HasSpatialAudioV2 == true ? "Spatial_Fixed" : "Spatial_On", _ => "Spatial_HeadTracking" });
                var button = new RadioButton { Content = label, Tag = (SpatialAudioMode)type, GroupName = "Spatial" };
                button.Click += OnSpatialClick;
                _spatialButtons.Add(((SpatialAudioMode)type, button));
                SpatialHost.Children.Add(button);
            }
        }
        bool enabled = ready && !f.IsPending("spatial");
        foreach (var (mode, button) in _spatialButtons)
        {
            button.IsChecked = f.SpatialMode == mode;
            button.IsEnabled = enabled;
        }
        SpatialHiResNote.Visibility = Show(f.IsHiResOn && !f.IsMoondrop);
    }

    /// <summary>Two columns when there is room; the side cards move under the EQ when narrow or alone.</summary>
    private void OnColumnsSizeChanged(object sender, SizeChangedEventArgs e) => LayoutColumns();

    private void LayoutColumns()
    {
        bool sideVisible = BassCard.Visibility == Visibility.Visible || SpatialCard.Visibility == Visibility.Visible;
        bool eqVisible = EqCard.Visibility == Visibility.Visible;
        bool twoColumns = SoundColumns.ActualWidth >= 720 && sideVisible && eqVisible;
        RightColumn.Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(SideCards, twoColumns ? 1 : 0);
        Grid.SetRow(SideCards, twoColumns || !eqVisible ? 0 : 1);
        Grid.SetColumnSpan(EqCard, twoColumns ? 1 : 2);
        Grid.SetColumnSpan(SideCards, twoColumns ? 1 : 2);
        // Side by side the EQ card keeps its own height instead of stretching to the side column.
        EqCard.VerticalAlignment = VerticalAlignment.Top;
    }

    // ---- handlers ------------------------------------------------------------------------------

    private async void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string id }) return;
        Sync();
        if (!await ReleaseGameSoundIfNeededAsync()) return;
        await ViewModel.SetEqualizerCommand.ExecuteAsync(id);
        Sync();
    }

    private async void OnCustomClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: int id } || Features.SelectedCustomEqualizer?.Id == id) return;
        Sync();
        if (!await ReleaseGameSoundIfNeededAsync()) return;
        await Features.SelectCustomEqualizerAsync(id);
        Sync();
    }

    private async void OnAddCustomClick(object sender, RoutedEventArgs e)
    {
        if (!await ReleaseGameSoundIfNeededAsync()) return;
        AddCustomButton.IsEnabled = false;
        // Like the official client: the earbuds create it, assign its id and select it.
        await Features.AddCustomEqualizerAsync();
        Sync();
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (Features.SelectedCustomEqualizer is not { } preset) return;
        string name = CustomNameBox.Text.Trim();
        if (name == preset.Name) return;
        if (name.Length == 0 || Features.CustomEqualizers.Any(p => p.Id != preset.Id && p.Name == name))
        {
            ViewModel.ErrorMessage = Loc.Get("Error_InvalidName");
            ViewModel.HasError = true;
            return;
        }
        await Features.SaveCustomEqualizerAsync(preset with { Name = name });
        _editorPresetId = null; // show the confirmed name
        Sync();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Features.SelectedCustomEqualizer is not { } preset) return;
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Eq_Delete_Title"),
            Content = preset.Name,
            PrimaryButtonText = Loc.Get("Eq_Delete_Confirm"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        await Features.DeleteCustomEqualizerAsync(preset.Id);
        _editorPresetId = null;
        Sync();
    }

    private void OnBandChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !IsLoaded || sender is not Slider { Tag: int index }) return;
        _bands[index].Value.Text = FormatGain((int)Math.Round(e.NewValue));
        _bandTimer ??= CreateTimer(BandDebounce, SaveBandsAsync);
        _bandTimer.Stop();
        _bandTimer.Start();
    }

    private async Task SaveBandsAsync()
    {
        if (Features.SelectedCustomEqualizer is not { } preset) return;
        var bands = preset.Bands.Select((b, i) => b with { Gain = i < _bands.Count ? (int)Math.Round(_bands[i].Slider.Value) : b.Gain }).ToArray();
        await Features.SaveCustomEqualizerAsync(preset with { Bands = bands });
        Sync();
    }

    private async void OnBassToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool requested = BassSwitch.IsOn;
        Sync();
        // Official mutex: the bass engine switch needs enhanced game sound off.
        if (!await ReleaseGameSoundIfNeededAsync()) return;
        await Features.SetToggleAsync(DeviceToggle.BassEngine, requested);
        Sync();
    }

    private void OnBassLevelChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        _bassTimer ??= CreateTimer(BassDebounce, async () =>
        {
            await Features.SetBassEngineLevelAsync((int)Math.Round(BassSlider.Value));
            Sync();
        });
        _bassTimer.Stop();
        _bassTimer.Start();
    }

    private async void OnSpatialClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: SpatialAudioMode mode }) return;
        Sync(); // the earbuds decide; the buttons show the confirmed mode until then
        if (mode == Features.SpatialMode) return;
        // Official Hi-Res mutex: confirm, then spatial on and Hi-Res off.
        if (mode != SpatialAudioMode.Off && Features.IsHiResOn && Features.Model?.SpatialHiResMutex == true)
        {
            var dialog = new ContentDialog
            {
                Title = Loc.Get("Spatial_HiRes_Title"),
                Content = new TextBlock { Text = Loc.Get("Spatial_HiRes_Message"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("Common_TurnOn"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        }
        await Features.SetSpatialAudioAsync(mode);
        Sync();
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>
    /// Official game-sound mutex: while enhanced game sound is on, EQ changes and the bass switch
    /// ask to turn it off first.
    /// </summary>
    private async Task<bool> ReleaseGameSoundIfNeededAsync()
    {
        if (!Features.GameSoundBlocksSoundChanges) return true;
        if (_dialogOpen) return false;
        // The stored game mode may be stale (changed on the phone or with a gesture): ask the earbuds.
        if (!await Features.CheckGameSoundBlocksAsync()) return true;
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Mutex_GameSound_Title"),
            Content = new TextBlock { Text = Loc.Get("Mutex_GameSound_Message"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get("Common_OK"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            Sync();
            return false;
        }
        bool released = await Features.ReleaseGameSoundAsync();
        Sync();
        return released;
    }

    /// <summary>
    /// One dialog at a time (a second ContentDialog throws, and an exception in these async void
    /// handlers would leave the page waiting for nothing). Returns None when it could not be shown.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen || XamlRoot is null) return ContentDialogResult.None;
        _dialogOpen = true;
        try
        {
            dialog.XamlRoot = XamlRoot;
            return await dialog.ShowAsync();
        }
        catch (Exception)
        {
            return ContentDialogResult.None;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

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

    private static string FormatGain(int gain) => gain > 0 ? $"+{gain}" : gain.ToString();

    private static string FormatFrequency(int hz) => hz >= 1000 ? $"{hz / 1000.0:0.#}k" : hz.ToString();

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
