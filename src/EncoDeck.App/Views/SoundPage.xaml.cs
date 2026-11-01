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
    private readonly List<(Slider Slider, TextBlock Value)> _sonyBands = [];
    private DispatcherQueueTimer? _bandTimer;
    private DispatcherQueueTimer? _bassTimer;
    private DispatcherQueueTimer? _sonyTimer;
    private string _customSignature = string.Empty;
    private int? _editorPresetId;
    private string _bandLayout = string.Empty;
    private int _gainScale = 1;
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
        ActualThemeChanged += (_, _) => SyncCurve();
        Unloaded += (_, _) =>
        {
            ViewModel.EqualizerPresets.CollectionChanged -= OnPresetsChanged;
            ViewModel.PropertyChanged -= OnViewModelChanged;
            Features.Changed -= OnFeaturesChanged;
            foreach (var (preset, _) in _buttons) preset.PropertyChanged -= OnPresetChanged;
            _bandTimer?.Stop();
            _bassTimer?.Stop();
            _sonyTimer?.Stop();
        };
    }

    public DeviceViewModel ViewModel => App.Instance.Device;

    private FeaturesViewModel Features => App.Instance.Features;

    private void OnPresetsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnPresetChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnFeaturesChanged(object? sender, EventArgs e) => Sync();

    private static readonly string[] CurveFrequencies = ["31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    private string? _curveKey;
    private byte[]? _curveBands;

    /// <summary>Re-draws the curve when the card resizes, so the plot keeps matching the fader row.</summary>
    private void OnCurveSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_curveBands is null || CurveChart.Visibility != Visibility.Visible) return;
        DrawCurve(_curveBands);
    }

    /// <summary>Draws the band values the device reported for the active preset; nothing when it reported none.</summary>
    private void SyncCurve()
    {
        var deviceBands = ViewModel.EqualizerBands;
        string key = $"{ActualTheme}:{string.Join(',', deviceBands)}";
        if (deviceBands.Count == 0)
        {
            _curveKey = null;
            _curveBands = null;
            CurveChart.Children.Clear();
            CurveChart.Visibility = Visibility.Collapsed;
            return;
        }
        CurveChart.Visibility = Visibility.Visible;
        if (key == _curveKey) return;
        _curveKey = key;
        DrawCurve(ToRawBands(deviceBands));
    }

    private static byte[] ToRawBands(IReadOnlyList<int> decibels)
    {
        var raw = new byte[decibels.Count];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)Math.Clamp(decibels[i] + 6, 0, 12);
        return raw;
    }

    /// <summary>
    /// A compact vector response chart: +6 dB … -6 dB grid, the band frequencies and a smoothed curve
    /// with a soft fill. Drawn rather than shipped as a bitmap so it stays crisp at
    /// any scaling and follows the theme colours.
    /// </summary>
    private void DrawCurve(byte[] bands)
    {
        CurveChart.Children.Clear();
        _curveBands = bands;
        // The plot area is laid out from the live canvas size and spans exactly the ten fader columns
        // below (each column is 1/10 of the width), so tick i lands on the centre of fader i.
        double totalWidth = CurveChart.ActualWidth > 1 ? CurveChart.ActualWidth : 600;
        double totalHeight = CurveChart.ActualHeight > 1 ? CurveChart.ActualHeight : 318;
        int count = bands.Length;
        double column = count > 0 ? totalWidth / count : totalWidth;
        double left = column / 2, right = totalWidth - column / 2;
        const double top = 16;
        double bottom = totalHeight - 30;
        double width = right - left, height = bottom - top;
        double perDb = height / 12.0;
        double step = count > 1 ? width / (count - 1) : 0;

        // The page's own theme (the app theme can differ from Windows'), and the accent shade WinUI uses
        // for accent fills in that theme.
        bool dark = ActualTheme == ElementTheme.Dark;
        Windows.UI.Color Ink(byte alpha) => dark ? Windows.UI.Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(alpha, 0, 0, 0);
        var accent = (Windows.UI.Color)Application.Current.Resources[dark ? "SystemAccentColorLight2" : "SystemAccentColorDark1"];
        Windows.UI.Color Accent(byte alpha) => Windows.UI.Color.FromArgb(alpha, accent.R, accent.G, accent.B);
        var gridBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Ink(0x1C));
        var zeroBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Ink(0x3D));
        var labelBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Ink(0x9E));

        var plot = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = width + 26,
            Height = height + 12,
            RadiusX = 12,
            RadiusY = 12,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Ink(0x0A)),
        };
        Canvas.SetLeft(plot, left - 13);
        Canvas.SetTop(plot, top - 6);
        CurveChart.Children.Add(plot);

        foreach (var (db, brush) in new[] { (6.0, gridBrush), (3.0, gridBrush), (0.0, zeroBrush), (-3.0, gridBrush), (-6.0, gridBrush) })
        {
            double y = top + (6 - db) * perDb;
            CurveChart.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = left, X2 = right, Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = 1,
            });
            var lbl = new TextBlock
            {
                Text = db > 0 ? $"+{db:0}" : $"{db:0}",
                FontSize = 11,
                Foreground = labelBrush,
                Width = 28,
                TextAlignment = TextAlignment.Right,
            };
            Canvas.SetLeft(lbl, left - 34);
            Canvas.SetTop(lbl, y - 8);
            CurveChart.Children.Add(lbl);
        }

        for (int i = 0; i < count; i++)
        {
            double x = left + i * step;
            var freq = new TextBlock
            {
                Text = i < CurveFrequencies.Length ? CurveFrequencies[i] : string.Empty,
                FontSize = 10,
                Foreground = labelBrush,
                Width = 30,
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(freq, x - 15);
            Canvas.SetTop(freq, bottom + 5);
            CurveChart.Children.Add(freq);
            CurveChart.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = x, X2 = x, Y1 = bottom, Y2 = bottom + 5, Stroke = gridBrush, StrokeThickness = 1,
            });
        }

        var points = new Windows.Foundation.Point[count];
        for (int i = 0; i < count; i++)
        {
            int db = bands[i] - 6;
            points[i] = new Windows.Foundation.Point(left + i * step, top + (6 - db) * perDb);
        }

        var line = SmoothedCurve(points);
        var fill = new Microsoft.UI.Xaml.Shapes.Path
        {
            Fill = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new Microsoft.UI.Xaml.Media.GradientStop { Color = Accent(0x4D), Offset = 0 },
                    new Microsoft.UI.Xaml.Media.GradientStop { Color = Accent(0x00), Offset = 1 },
                },
            },
        };
        var fillFigure = line.Figures[0];
        fillFigure.IsClosed = true;
        fillFigure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Windows.Foundation.Point(right, bottom) });
        fillFigure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Windows.Foundation.Point(left, bottom) });
        fill.Data = line;
        CurveChart.Children.Add(fill);

        var stroke = new Microsoft.UI.Xaml.Shapes.Path
        {
            Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(Accent(0xFF)),
            StrokeThickness = 3,
            StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round,
            Data = SmoothedCurve(points),
        };
        CurveChart.Children.Add(stroke);

        foreach (var p in points)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Accent(0xFF)),
            };
            Canvas.SetLeft(dot, p.X - 3);
            Canvas.SetTop(dot, p.Y - 3);
            CurveChart.Children.Add(dot);
        }
    }

    private static Microsoft.UI.Xaml.Media.PathGeometry SmoothedCurve(Windows.Foundation.Point[] points)
    {
        var figure = new Microsoft.UI.Xaml.Media.PathFigure { StartPoint = points[0] };
        for (int i = 0; i < points.Length - 1; i++)
        {
            var p0 = points[Math.Max(i - 1, 0)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(i + 2, points.Length - 1)];
            figure.Segments.Add(new Microsoft.UI.Xaml.Media.BezierSegment
            {
                Point1 = new Windows.Foundation.Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6),
                Point2 = new Windows.Foundation.Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6),
                Point3 = p2,
            });
        }
        var geometry = new Microsoft.UI.Xaml.Media.PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
    /// <summary>
    /// Band faders for the presets whose values the headset lets us overwrite: manual and the two custom
    /// slots. Values are dB offsets (-6..+6) read back from the device, written after a short debounce so
    /// dragging a fader sends one command instead of one per step.
    /// </summary>
    private void SyncBandEditor()
    {
        bool editable = ViewModel.CanEditEqualizerBands;
        SonyBandEditor.Visibility = Show(editable);
        if (!editable) return;
        if (_sonyBands.Count == 0) BuildBandFaders();
        if (_sonyTimer is { IsRunning: true }) return; // the user is still dragging
        var bands = ViewModel.EqualizerBands;
        _syncing = true;
        try
        {
            for (int i = 0; i < _sonyBands.Count; i++)
            {
                int value = Math.Clamp(i < bands.Count ? bands[i] : 0, -6, 6);
                _sonyBands[i].Slider.Value = value;
                _sonyBands[i].Value.Text = FormatGain(value);
            }
        }
        finally { _syncing = false; }
    }

    private void BuildBandFaders()
    {
        SonyBandHost.Children.Clear();
        SonyBandHost.ColumnDefinitions.Clear();
        _sonyBands.Clear();
        for (int i = 0; i < CurveFrequencies.Length; i++)
        {
            SonyBandHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (int i = 0; i < CurveFrequencies.Length; i++)
        {
            var value = new TextBlock { Text = "0", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.7 };
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -6,
                Maximum = 6,
                StepFrequency = 1,
                TickFrequency = 1,
                Height = 132,
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                Tag = i,
            };
            slider.ValueChanged += OnSonyBandChanged;
            var freq = new TextBlock { Text = CurveFrequencies[i], FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.55 };
            var column = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            column.Children.Add(value);
            column.Children.Add(slider);
            column.Children.Add(freq);
            Grid.SetColumn(column, i);
            SonyBandHost.Children.Add(column);
            _sonyBands.Add((slider, value));
        }
    }

    private void OnSonyBandChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !IsLoaded || sender is not Slider { Tag: int index } || index >= _sonyBands.Count) return;
        _sonyBands[index].Value.Text = FormatGain((int)Math.Round(e.NewValue));
        _sonyTimer ??= CreateTimer(BandDebounce, SaveSonyBandsAsync);
        _sonyTimer.Stop();
        _sonyTimer.Start();
    }

    private async Task SaveSonyBandsAsync()
    {
        if (!ViewModel.CanEditEqualizerBands || _sonyBands.Count == 0) return;
        var bands = _sonyBands.Select(b => (int)Math.Round(b.Slider.Value)).ToArray();
        await ViewModel.SetEqualizerBandsAsync(bands);
    }

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
            EqCard.Visibility = Show(ViewModel.EqualizerPresets.Count > 0 || f.CustomEqualizerSpec is not null || f.BandEqualizer is not null);
            foreach (var (preset, button) in _buttons)
            {
                // A selected custom preset replaces the built-in selection.
                button.IsChecked = selectedCustom is null && preset.IsSelected;
                button.IsEnabled = ready && !eqBusy;
            }
            SyncCustom(f, ready, eqBusy, selectedCustom);
            SyncCurve();
            SyncBandEditor();
            SyncBass(f, ready);
            SyncSpatial(f, ready);
            EmptyCard.Visibility = Show(ready && EqCard.Visibility == Visibility.Collapsed
                && BassCard.Visibility == Visibility.Collapsed && SpatialCard.Visibility == Visibility.Collapsed);
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
        // Other brands: one fixed device curve (steps set by the brand), without add / rename / delete.
        var band = f.BandEqualizer;
        var presets = f.CustomEqualizers;
        string signature = string.Join('|', presets.Select(p => $"{p.Id}/{p.Name}")) + $"#{band is not null}";
        if (signature != _customSignature)
        {
            _customSignature = signature;
            _customButtons.Clear();
            CustomHost.Children.Clear();
            foreach (var preset in presets)
            {
                // Xiaomi's single custom curve and fixed device curves usually have no stored name.
                string name = preset.Name.Length == 0 ? Loc.Get("Eq_DeviceCustom") : preset.Name;
                var button = new RadioButton { Content = name, Tag = preset.Id, GroupName = "DeviceEq" };
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
        CustomEditor.Visibility = Show((spec is not null || band is not null) && selected is not null);
        // A device's single fixed curve has no name to edit and cannot be deleted.
        CustomNameRow.Visibility = CustomNameBox.Visibility = RenameButton.Visibility = DeleteButton.Visibility = Show(spec is not null);
        if (selected is null || (spec is null && band is null)) return;

        int min = spec?.MinGain ?? band!.MinGain, max = spec?.MaxGain ?? band!.MaxGain, scale = spec is null ? band!.GainScale : 1;
        string layout = $"{string.Join(',', selected.Bands.Select(b => b.FrequencyHz))}/{min}/{max}/{scale}";
        if (_bandLayout != layout)
        {
            _bandLayout = layout;
            _gainScale = scale;
            BuildBands(selected, min, max);
        }
        if (_editorPresetId != selected.Id)
        {
            _editorPresetId = selected.Id;
            CustomNameBox.Text = selected.Name;
        }
        // Never move a slider the user is still dragging, nor while its write and read-back are in flight.
        if (_bandTimer?.IsRunning != true && !f.IsPending("custom-eq"))
        {
            for (int i = 0; i < _bands.Count && i < selected.Bands.Count; i++)
            {
                _bands[i].Slider.Value = selected.Bands[i].Gain;
                _bands[i].Value.Text = FormatGain(selected.Bands[i].Gain, _gainScale);
            }
        }
        foreach (var (slider, _) in _bands) slider.IsEnabled = ready;
        RenameButton.IsEnabled = ready && !busy;
        DeleteButton.IsEnabled = ready && !busy;
    }

    private void BuildBands(CustomEqualizer preset, int min, int max)
    {
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
                TickFrequency = _gainScale,
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
        BassTitle.Text = Loc.Get(f.IsMoondrop ? "Moondrop_Bass_Title" : "Bass_Title/Text");
        BassSummary.Text = Loc.Get(f.IsMoondrop ? "Moondrop_Bass_Summary" : "Bass_Summary/Text");
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
        // Xiaomi earbuds report off / on / head tracking in one confirmed setting (config 29).
        bool xiaomi = f.IsXiaomi && f.Has(DeviceCapability.HeadTracking);
        bool available = f.Has(DeviceCapability.SpatialAudio) && (f.Model is { SpatialTypes.Count: > 0 } || f.IsMoondrop || xiaomi) && f.State.Spatial is not null;
        SpatialCard.Visibility = Show(available);
        if (!available) return;

        // Official buttons: the model's spatial types; "fixed" reads "on" with spatial V1, which has no head tracking.
        var model = f.Model;
        bool v2 = xiaomi || f.IsMoondrop || model?.HasSpatialAudioV2 == true;
        int[] types = xiaomi ? [0, 1, 2]
            : f.IsMoondrop ? new[] { 0, 1 }.Concat(f.Has(DeviceCapability.HeadTracking) ? new[] { 2 } : Array.Empty<int>()).ToArray()
            : model!.SpatialTypes.Where(t => t is 0 or 1 || (t == 2 && model.HasSpatialAudioV2)).Order().ToArray();
        string signature = $"{string.Join(',', types)}/{v2}";
        if (signature != _spatialSignature)
        {
            _spatialSignature = signature;
            _spatialButtons.Clear();
            SpatialHost.Children.Clear();
            foreach (int type in types)
            {
                string label = Loc.Get(type switch { 0 => "Spatial_Off", 1 => v2 ? "Spatial_Fixed" : "Spatial_On", _ => "Spatial_HeadTracking" });
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
        _bands[index].Value.Text = FormatGain((int)Math.Round(e.NewValue), _gainScale);
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

    /// <summary>Gain in steps of 1 / <paramref name="scale"/> dB (OPPO whole dB, other brands 0.1 dB).</summary>
    /// <summary>Formats a gain given in 1 / <paramref name="scale"/> dB steps (1 = whole dB).</summary>
    private static string FormatGain(int gain, int scale = 1) => scale == 1
        ? gain > 0 ? $"+{gain}" : gain.ToString()
        : (gain / (double)scale).ToString("+0.0;-0.0;0", System.Globalization.CultureInfo.CurrentCulture);

    private static string FormatFrequency(int hz) => hz >= 1000 ? $"{hz / 1000.0:0.#}k" : hz.ToString();

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
