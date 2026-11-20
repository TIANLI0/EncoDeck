// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using EncoDeck.App.ViewModels;
using EncoDeck.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace EncoDeck.App.Controls;

/// <summary>
/// Noise cancelling / adaptive / transparency / off selector with a level row for the selected
/// mode. A click shows the choice at once with a spinner until the earbuds answer; afterwards the
/// buttons mirror the device-confirmed state in <see cref="DeviceViewModel"/> again.
/// </summary>
public sealed partial class NoiseControlSelector : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(DeviceViewModel), typeof(NoiseControlSelector), new PropertyMetadata(null, OnViewModelChanged));

    private readonly NoiseRows _rows;
    private readonly (NoiseControlMode Mode, ToggleButton Button, UIElement Icon, ProgressRing Ring)[] _modes;
    private NoiseControlMode? _pendingMode;

    public NoiseControlSelector()
    {
        InitializeComponent();
        _modes =
        [
            (NoiseControlMode.NoiseCancellation, AncButton, AncIcon, AncRing),
            (NoiseControlMode.Adaptive, AdaptiveButton, AdaptiveIcon, AdaptiveRing),
            (NoiseControlMode.Transparency, TransparencyButton, TransparencyIcon, TransparencyRing),
            (NoiseControlMode.Off, OffButton, OffIcon, OffRing),
        ];
        _rows = new NoiseRows(RowsHost, ModeGrid, LevelsRow, SubModesRow, ManualLevelsRow, () => _pendingMode is not null) { Changed = Sync };
        foreach (var label in new[] { AncLabel, AdaptiveLabel, TransparencyLabel, OffLabel }) FitLabel.Attach(label);
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
        var control = (NoiseControlSelector)d;
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
        foreach (var (mode, button, icon, ring) in _modes)
        {
            bool show = NoiseModes.IsAvailable(vm, mode);
            button.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) Grid.SetColumn(button, visible++);
            button.IsChecked = _pendingMode is { } pending ? mode == pending : NoiseModes.IsSelected(vm, mode);
            button.IsEnabled = vm.CanSetNoise;
            bool waiting = mode == _pendingMode;
            ring.IsActive = waiting;
            ring.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
            icon.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
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
        if (sender is not ToggleButton { Tag: string tag } || !Enum.TryParse<NoiseControlMode>(tag, out var mode) || ViewModel is not { } vm) return;
        if (_rows.IsPending || vm.IsNoisePending || NoiseModes.IsSelected(vm, mode))
        {
            Sync(); // undo the local toggle
            return;
        }
        await NoiseModes.ChooseAsync(vm, mode, pending => { _pendingMode = pending; Sync(); });
    }
}
