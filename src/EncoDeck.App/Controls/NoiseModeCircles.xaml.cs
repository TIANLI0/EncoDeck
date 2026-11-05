// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
/// adaptive, transparency, off) with its label underneath, and the level chips of the selected
/// mode below. Checked states always mirror the device-confirmed state.
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

    private readonly List<(NoiseControlMode Mode, ToggleButton Button, FrameworkElement Cell)> _modes = [];
    private readonly List<(NoiseLevelViewModel Level, ToggleButton Button)> _levelButtons = [];
    private ObservableCollection<NoiseLevelViewModel>? _shownLevels;

    public NoiseModeCircles()
    {
        InitializeComponent();
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
            var cell = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(button);
            cell.Children.Add(FitLabel.Create(label));
            _modes.Add((mode, button, cell));
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
        vm.NoiseLevels.CollectionChanged += OnLevelsChanged;
        vm.AdaptiveLevels.CollectionChanged += OnLevelsChanged;
        _shownLevels = null;
        Sync();
    }

    private void Detach(DeviceViewModel? vm)
    {
        if (vm is null) return;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.NoiseLevels.CollectionChanged -= OnLevelsChanged;
        vm.AdaptiveLevels.CollectionChanged -= OnLevelsChanged;
        foreach (var (level, _) in _levelButtons) level.PropertyChanged -= OnLevelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnLevelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _shownLevels = null;
        Sync();
    }

    private void OnLevelPropertyChanged(object? sender, PropertyChangedEventArgs e) => SyncLevels();

    private void Sync()
    {
        if (ViewModel is not { } vm) return;
        bool enabled = vm.CanSetNoise && !vm.IsNoisePending;
        int visible = 0;
        foreach (var (mode, button, cell) in _modes)
        {
            bool show = mode switch
            {
                NoiseControlMode.NoiseCancellation => vm.HasAncMode,
                NoiseControlMode.Adaptive => vm.HasAdaptiveMode,
                NoiseControlMode.Transparency => vm.HasTransparencyMode,
                _ => vm.HasOffMode,
            };
            cell.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) Grid.SetColumn(cell, visible++);
            button.IsChecked = mode switch
            {
                NoiseControlMode.NoiseCancellation => vm.IsAncSelected,
                NoiseControlMode.Adaptive => vm.IsAdaptiveSelected,
                NoiseControlMode.Transparency => vm.IsTransparencySelected,
                _ => vm.IsOffSelected,
            };
            button.IsEnabled = enabled;
        }
        if (ModeGrid.ColumnDefinitions.Count != visible)
        {
            ModeGrid.ColumnDefinitions.Clear();
            for (int i = 0; i < visible; i++) ModeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var levels = vm.ShowNoiseLevels ? vm.NoiseLevels : vm.ShowAdaptiveLevels ? vm.AdaptiveLevels : null;
        if (!ReferenceEquals(levels, _shownLevels)) RebuildLevels(levels, vm.ShowNoiseLevels);
        LevelsPanel.Visibility = levels is null ? Visibility.Collapsed : Visibility.Visible;
        SyncLevels();
    }

    private void RebuildLevels(ObservableCollection<NoiseLevelViewModel>? levels, bool isNoiseCancelling)
    {
        foreach (var (level, _) in _levelButtons) level.PropertyChanged -= OnLevelPropertyChanged;
        _levelButtons.Clear();
        LevelsHost.Children.Clear();
        LevelsHost.ColumnDefinitions.Clear();
        _shownLevels = levels;
        if (levels is null) return;

        LevelsTitle.Text = Loc.Get(isNoiseCancelling ? "NoiseSelector_Level" : "NoiseSelector_AdaptiveLevel");
        int column = 0;
        foreach (var level in levels)
        {
            LevelsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = new ToggleButton
            {
                Content = FitLabel.Create(level.Label),
                Tag = level.Id,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinHeight = 36,
                Padding = new Thickness(4, 6, 4, 6),
                CornerRadius = new CornerRadius(18),
            };
            AutomationProperties.SetName(button, level.Label);
            Grid.SetColumn(button, column++);
            button.Click += OnLevelClick;
            level.PropertyChanged += OnLevelPropertyChanged;
            _levelButtons.Add((level, button));
            LevelsHost.Children.Add(button);
        }
    }

    private void SyncLevels()
    {
        if (ViewModel is not { } vm) return;
        bool enabled = vm.CanSetNoise && !vm.IsNoisePending;
        foreach (var (level, button) in _levelButtons)
        {
            button.IsChecked = level.IsSelected;
            button.IsEnabled = enabled;
        }
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: NoiseControlMode mode } && ViewModel is { } vm)
        {
            Sync(); // the device decides; snap back until it confirms
            vm.SetNoiseModeCommand.Execute(mode.ToString());
        }
    }

    private void OnLevelClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string id } && ViewModel is { } vm)
        {
            SyncLevels();
            vm.SetNoiseLevelCommand.Execute(id);
        }
    }
}
