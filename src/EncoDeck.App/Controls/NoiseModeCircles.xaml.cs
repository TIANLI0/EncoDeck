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
    private const int SubModesPerRow = 4;
    private readonly List<(NoiseLevelViewModel Level, ToggleButton Button)> _subModeButtons = [];
    private bool _subModesStale = true;
    private readonly List<(NoiseLevelViewModel Level, ToggleButton Button)> _manualLevelButtons = [];
    private bool _manualLevelsStale = true;

    public NoiseModeCircles()
    {
        InitializeComponent();
        ManualLevelsTitle.Text = Loc.Get("NoiseSelector_ManualLevel");
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
        vm.NoiseSubModes.CollectionChanged += OnSubModesChanged;
        vm.NoiseManualLevels.CollectionChanged += OnManualLevelsChanged;
        _shownLevels = null;
        _subModesStale = true;
        _manualLevelsStale = true;
        Sync();
    }

    private void Detach(DeviceViewModel? vm)
    {
        if (vm is null) return;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.NoiseLevels.CollectionChanged -= OnLevelsChanged;
        vm.AdaptiveLevels.CollectionChanged -= OnLevelsChanged;
        vm.NoiseSubModes.CollectionChanged -= OnSubModesChanged;
        vm.NoiseManualLevels.CollectionChanged -= OnManualLevelsChanged;
        foreach (var (level, _) in _levelButtons.Concat(_subModeButtons).Concat(_manualLevelButtons)) level.PropertyChanged -= OnLevelPropertyChanged;
    }

    private void OnSubModesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _subModesStale = true;
        Sync();
    }

    private void OnManualLevelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _manualLevelsStale = true;
        Sync();
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
        if (_subModesStale) RebuildSubModes(vm);
        SubModesTitle.Text = vm.NoiseSubModesTitle;
        SubModesPanel.Visibility = vm.ShowNoiseSubModes ? Visibility.Visible : Visibility.Collapsed;
        if (_manualLevelsStale)
        {
            _manualLevelsStale = false;
            RebuildRow(ManualLevelsHost, vm.NoiseManualLevels, _manualLevelButtons, OnSubModeClick);
        }
        ManualLevelsPanel.Visibility = vm.ShowNoiseManualLevels ? Visibility.Visible : Visibility.Collapsed;
        SyncLevels();
    }

    private void RebuildRow(Grid host, IEnumerable<NoiseLevelViewModel> levels, List<(NoiseLevelViewModel Level, ToggleButton Button)> buttons, RoutedEventHandler click)
    {
        foreach (var (level, _) in buttons) level.PropertyChanged -= OnLevelPropertyChanged;
        buttons.Clear();
        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        foreach (var level in levels)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = CreateLevelButton(level, click);
            Grid.SetColumn(button, buttons.Count);
            buttons.Add((level, button));
            host.Children.Add(button);
        }
    }

    private void RebuildSubModes(DeviceViewModel vm)
    {
        _subModesStale = false;
        foreach (var (level, _) in _subModeButtons) level.PropertyChanged -= OnLevelPropertyChanged;
        _subModeButtons.Clear();
        SubModesHost.Children.Clear();
        SubModesHost.ColumnDefinitions.Clear();
        SubModesHost.RowDefinitions.Clear();
        int count = vm.NoiseSubModes.Count;
        for (int i = 0; i < Math.Min(count, SubModesPerRow); i++) SubModesHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < (count + SubModesPerRow - 1) / SubModesPerRow; i++) SubModesHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int index = 0;
        foreach (var mode in vm.NoiseSubModes)
        {
            var button = CreateLevelButton(mode, OnSubModeClick);
            Grid.SetColumn(button, index % SubModesPerRow);
            Grid.SetRow(button, index / SubModesPerRow);
            index++;
            _subModeButtons.Add((mode, button));
            SubModesHost.Children.Add(button);
        }
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
            var button = CreateLevelButton(level, OnLevelClick);
            Grid.SetColumn(button, column++);
            _levelButtons.Add((level, button));
            LevelsHost.Children.Add(button);
        }
    }

    private ToggleButton CreateLevelButton(NoiseLevelViewModel level, RoutedEventHandler click)
    {
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
        button.Click += click;
        level.PropertyChanged += OnLevelPropertyChanged;
        return button;
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
        foreach (var (mode, button) in _subModeButtons.Concat(_manualLevelButtons))
        {
            button.IsChecked = mode.IsSelected;
            button.IsEnabled = enabled && !vm.IsNoiseSubModePending;
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

    private void OnSubModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string value } && ViewModel is { } vm)
        {
            SyncLevels();
            vm.SetNoiseSubModeCommand.Execute(value);
        }
    }
}
