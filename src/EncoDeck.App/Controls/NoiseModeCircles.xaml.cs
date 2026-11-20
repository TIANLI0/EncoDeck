// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace EncoDeck.App.Controls;

/// <summary>
/// Noise control as on HeyMelody's device page: one round button per mode (noise cancelling,
/// adaptive, transparency, off) with its label underneath, and the level rows of the selected
/// mode below. A click shows the choice at once with a ring turning around it until the earbuds
/// answer; afterwards the buttons mirror the device-confirmed state again.
/// </summary>
public sealed partial class NoiseModeCircles : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(DeviceViewModel), typeof(NoiseModeCircles), new PropertyMetadata(null, OnViewModelChanged));

    // Official order: noise cancelling, adaptive, transparency, off.
    private static readonly (NoiseControlMode Mode, string Symbol, string LabelKey)[] Modes =
    [
        (NoiseControlMode.NoiseCancellation, "headphones", "NoiseMode_NoiseCancellation"),
        (NoiseControlMode.Adaptive, "sound_wave_circle_sparkle", "NoiseMode_Adaptive"),
        (NoiseControlMode.Transparency, "person_sound_spatial", "NoiseMode_Transparency"),
        (NoiseControlMode.Off, "prohibited", "NoiseMode_Off"),
    ];

    private readonly List<(NoiseControlMode Mode, ToggleButton Button, ProgressRing Ring, FrameworkElement Cell)> _modes = [];
    private readonly NoiseRows _rows;
    private NoiseControlMode? _pendingMode;

    public NoiseModeCircles()
    {
        InitializeComponent();
        _rows = new NoiseRows(RowsHost, ModeGrid, LevelsRow, SubModesRow, ManualLevelsRow, () => _pendingMode is not null) { Changed = Sync };
        foreach (var (mode, symbol, labelKey) in Modes)
        {
            string label = Loc.Get(labelKey);
            var button = new ToggleButton
            {
                Style = (Style)Resources["CircleModeButtonStyle"],
                Content = new FluentIcon(symbol, 22),
                Tag = mode,
            };
            AutomationProperties.SetName(button, label);
            button.Click += OnModeClick;
            // Turns around the circle while the mode waits for the earbuds' answer.
            var ring = new ProgressRing { Width = 74, Height = 74, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            var circle = new Grid { Width = 74, Height = 74, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -5, 0, -5) };
            circle.Children.Add(button);
            circle.Children.Add(ring);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.VerticalAlignment = VerticalAlignment.Center;
            var cell = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(circle);
            cell.Children.Add(FitLabel.Create(label));
            _modes.Add((mode, button, ring, cell));
            ModeGrid.Children.Add(cell);
        }
        // Moving the control to another parent (HomePage on wide windows) can raise Unloaded after the
        // new Loaded; detaching then would leave the buttons deaf to the device state.
        Unloaded += (_, _) => { if (!IsLoaded) Detach(ViewModel); };
        Loaded += (_, _) => Attach(ViewModel);
    }

    public DeviceViewModel? ViewModel
    {
        get => (DeviceViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (NoiseModeCircles)d;
        control.Detach(e.OldValue as DeviceViewModel);
        control.Attach(e.NewValue as DeviceViewModel);
    }

    private void Attach(DeviceViewModel? vm)
    {
        if (vm is null) return;
        Detach(vm);
        vm.PropertyChanged += OnViewModelPropertyChanged;
        _rows.Attach(vm);
        Sync();
    }

    private void Detach(DeviceViewModel? vm)
    {
        if (vm is null) return;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        _rows.Detach();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        if (ViewModel is not { } vm) return;
        int visible = 0;
        foreach (var (mode, button, ring, cell) in _modes)
        {
            bool show = NoiseModes.IsAvailable(vm, mode);
            cell.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) Grid.SetColumn(cell, visible++);
            button.IsChecked = _pendingMode is { } pending ? mode == pending : NoiseModes.IsSelected(vm, mode);
            button.IsEnabled = vm.CanSetNoise;
            bool waiting = mode == _pendingMode;
            ring.IsActive = waiting;
            ring.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        }
        if (ModeGrid.ColumnDefinitions.Count != visible)
        {
            ModeGrid.ColumnDefinitions.Clear();
            for (int i = 0; i < visible; i++) ModeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        _rows.Sync();
    }

    private async void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: NoiseControlMode mode } || ViewModel is not { } vm) return;
        if (_rows.IsPending || vm.IsNoisePending || NoiseModes.IsSelected(vm, mode))
        {
            Sync(); // undo the local toggle
            return;
        }
        await NoiseModes.ChooseAsync(vm, mode, pending => { _pendingMode = pending; Sync(); });
    }

    /// <summary>Off-screen preview: a mode click answered by <paramref name="command"/> instead of the earbuds.</summary>
    internal Task PreviewChooseAsync(NoiseControlMode mode, Func<Task> command) =>
        ViewModel is { } vm ? NoiseModes.ChooseAsync(vm, mode, pending => { _pendingMode = pending; Sync(); }, command) : Task.CompletedTask;

    internal SegmentedRow SubModes => SubModesRow;
    internal SegmentedRow ManualLevels => ManualLevelsRow;
}
