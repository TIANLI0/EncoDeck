// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace EncoDeck.App.Controls;

/// <summary>The noise modes as both noise controls show them.</summary>
internal static class NoiseModes
{
    // The confirmed state can reach the view model a moment after the command returns.
    private static readonly TimeSpan ConfirmGrace = TimeSpan.FromMilliseconds(400);

    public static bool IsAvailable(DeviceViewModel vm, NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.NoiseCancellation => vm.HasAncMode,
        NoiseControlMode.Adaptive => vm.HasAdaptiveMode,
        NoiseControlMode.Transparency => vm.HasTransparencyMode,
        _ => vm.HasOffMode,
    };

    public static bool IsSelected(DeviceViewModel vm, NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.NoiseCancellation => vm.IsAncSelected,
        NoiseControlMode.Adaptive => vm.IsAdaptiveSelected,
        NoiseControlMode.Transparency => vm.IsTransparencySelected,
        _ => vm.IsOffSelected,
    };

    /// <summary>
    /// Shows <paramref name="mode"/> as chosen (through <paramref name="setPending"/>) until the earbuds
    /// answer, then hands back to the confirmed state, which is the old mode if they refused.
    /// </summary>
    public static async Task ChooseAsync(DeviceViewModel vm, NoiseControlMode mode, Action<NoiseControlMode?> setPending, Func<Task>? command = null)
    {
        setPending(mode);
        vm.NoiseModePreview = mode; // the rows below follow the click at once
        try
        {
            await (command?.Invoke() ?? vm.SetNoiseModeCommand.ExecuteAsync(mode.ToString()));
            var deadline = DateTime.UtcNow + ConfirmGrace;
            while (!IsSelected(vm, mode) && DateTime.UtcNow < deadline) await Task.Delay(50);
        }
        catch
        {
            // The view model reports command failures itself.
        }
        finally
        {
            vm.NoiseModePreview = null;
            setPending(null);
        }
    }
}

/// <summary>
/// The rows under the noise modes, shared by <see cref="NoiseControlSelector"/> and <see cref="NoiseModeCircles"/>:
/// the levels of the selected mode, the earbuds' own sub-modes and EarFun's manual levels. Rows that
/// come and go with the mode fade while their host opens or closes over them, so the card (and what
/// sits below it) moves smoothly instead of jumping.
/// </summary>
internal sealed class NoiseRows
{
    private const int SubModesPerRow = 4;
    private static readonly Duration Resize = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration FadeOut = new(TimeSpan.FromMilliseconds(140));
    private static readonly Duration FadeIn = new(TimeSpan.FromMilliseconds(220));

    private readonly SegmentedRow _levels;
    private readonly SegmentedRow _subModes;
    private readonly SegmentedRow _manualLevels;
    private readonly Func<bool> _modePending;
    private readonly FrameworkElement _host;
    private readonly FrameworkElement _widthSource;
    private readonly Storyboard _resize = new();
    private readonly DoubleAnimation _resizeHeight;
    private readonly Dictionary<SegmentedRow, bool> _target = [];
    private DeviceViewModel? _vm;
    private ObservableCollection<NoiseLevelViewModel>? _shownLevels;
    private bool _subModesStale = true;
    private bool _manualLevelsStale = true;

    /// <param name="host">Holds the rows (collapsed while none shows); its height animates.</param>
    /// <param name="widthSource">An element as wide as the rows, for measuring them while the host is collapsed.</param>
    public NoiseRows(FrameworkElement host, FrameworkElement widthSource, SegmentedRow levels, SegmentedRow subModes, SegmentedRow manualLevels, Func<bool> modePending)
    {
        _host = host;
        _widthSource = widthSource;
        _resizeHeight = new DoubleAnimation { Duration = Resize, EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(_resizeHeight, host);
        Storyboard.SetTargetProperty(_resizeHeight, "Height");
        _resize.Children.Add(_resizeHeight);
        _resize.Completed += (_, _) => Settle();
        // Rows the host closes over leave through its bottom edge instead of spilling over the card.
        host.SizeChanged += (_, e) => host.Clip = new RectangleGeometry { Rect = new Rect(-6, -6, e.NewSize.Width + 12, e.NewSize.Height + 6) };
        _levels = levels;
        _subModes = subModes;
        _manualLevels = manualLevels;
        _modePending = modePending;
        _manualLevels.Title = Loc.Get("NoiseSelector_ManualLevel");
        _levels.Invoke = id => _vm?.SetNoiseLevelCommand.ExecuteAsync(id) ?? Task.CompletedTask;
        _subModes.Invoke = _manualLevels.Invoke = value => _vm?.SetNoiseSubModeCommand.ExecuteAsync(value) ?? Task.CompletedTask;
        _levels.IsBusy = _subModes.IsBusy = _manualLevels.IsBusy = () => IsPending || _vm is { IsNoisePending: true } or { IsNoiseSubModePending: true };
    }

    /// <summary>A choice in one of the rows waits for the earbuds.</summary>
    public bool IsPending => _levels.IsPending || _subModes.IsPending || _manualLevels.IsPending || _modePending();

    /// <summary>Called by the owner on every view model change.</summary>
    public Action? Changed { get; set; }

    public void Attach(DeviceViewModel vm)
    {
        Detach();
        _vm = vm;
        vm.NoiseLevels.CollectionChanged += OnLevelsChanged;
        vm.AdaptiveLevels.CollectionChanged += OnLevelsChanged;
        vm.TransparencyLevels.CollectionChanged += OnLevelsChanged;
        vm.NoiseSubModes.CollectionChanged += OnSubModesChanged;
        vm.NoiseManualLevels.CollectionChanged += OnManualLevelsChanged;
        _shownLevels = null;
        _subModesStale = true;
        _manualLevelsStale = true; // the next Sync rebuilds every row and listens to its items again
    }

    public void Detach()
    {
        if (_vm is not { } vm) return;
        vm.NoiseLevels.CollectionChanged -= OnLevelsChanged;
        vm.AdaptiveLevels.CollectionChanged -= OnLevelsChanged;
        vm.TransparencyLevels.CollectionChanged -= OnLevelsChanged;
        vm.NoiseSubModes.CollectionChanged -= OnSubModesChanged;
        vm.NoiseManualLevels.CollectionChanged -= OnManualLevelsChanged;
        _levels.Detach();
        _subModes.Detach();
        _manualLevels.Detach();
        _vm = null;
    }

    private void OnLevelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _shownLevels = null;
        Changed?.Invoke();
    }

    private void OnSubModesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _subModesStale = true;
        Changed?.Invoke();
    }

    private void OnManualLevelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _manualLevelsStale = true;
        Changed?.Invoke();
    }

    public void Sync()
    {
        if (_vm is not { } vm) return;
        var levels = vm.ShowNoiseLevels ? vm.NoiseLevels : vm.ShowAdaptiveLevels ? vm.AdaptiveLevels
            : vm.ShowTransparencyLevels ? vm.TransparencyLevels : null;
        // A row keeps its old content while it fades out; it is rebuilt only to be shown.
        bool levelsRefreshed = false, subModesRefreshed = false, manualRefreshed = false;
        if (levels is not null && !ReferenceEquals(levels, _shownLevels))
        {
            _shownLevels = levels;
            _levels.SetItems(levels);
            levelsRefreshed = true;
        }
        if (levels is not null)
            _levels.Title = Loc.Get(vm.ShowNoiseLevels ? "NoiseSelector_Level" : vm.ShowAdaptiveLevels ? "NoiseSelector_AdaptiveLevel" : "NoiseSelector_TransparencyLevel");

        if (_subModesStale && vm.ShowNoiseSubModes)
        {
            _subModesStale = false;
            _subModes.SetItems(vm.NoiseSubModes, SubModesPerRow);
            subModesRefreshed = true;
        }
        if (vm.ShowNoiseSubModes && _subModes.Title != vm.NoiseSubModesTitle)
        {
            _subModes.Title = vm.NoiseSubModesTitle;
            subModesRefreshed = true;
        }

        if (_manualLevelsStale && vm.ShowNoiseManualLevels)
        {
            _manualLevelsStale = false;
            _manualLevels.SetItems(vm.NoiseManualLevels);
            manualRefreshed = true;
        }

        _levels.Interactive = _subModes.Interactive = _manualLevels.Interactive = vm.CanSetNoise;
        Transition([(_levels, levels is not null, levelsRefreshed), (_subModes, vm.ShowNoiseSubModes, subModesRefreshed),
            (_manualLevels, vm.ShowNoiseManualLevels, manualRefreshed)]);
    }

    private void Transition((SegmentedRow Row, bool Visible, bool Refreshed)[] rows)
    {
        if (!rows.Any(r => _target.GetValueOrDefault(r.Row) != r.Visible || (r.Visible && r.Refreshed))) return;
        bool animate = _host.IsLoaded && _widthSource.ActualWidth > 0;
        // Mid-transition the host's height is the animated one: start from what is on screen.
        double before = _host.Visibility == Visibility.Visible ? _host.ActualHeight : 0;
        var leaving = rows.Where(r => !r.Visible && r.Row.Visibility == Visibility.Visible).Select(r => r.Row).ToList();
        var entering = rows.Where(r => r.Visible && (!_target.GetValueOrDefault(r.Row) || r.Refreshed)).Select(r => r.Row).ToList();
        _resize.Stop();
        _host.Height = double.NaN;
        foreach (var (row, visible, _) in rows)
        {
            _target[row] = visible;
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        bool any = rows.Any(r => r.Visible);
        if (!animate)
        {
            foreach (var (row, _, _) in rows) row.Opacity = 1;
            _host.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        double after = 0;
        _host.Visibility = Visibility.Visible;
        if (any)
        {
            _host.Measure(new Size(_widthSource.ActualWidth, double.PositiveInfinity));
            after = _host.DesiredSize.Height;
        }
        // Leaving rows stay on screen, fading, while the host closes over them; Settle collapses them.
        foreach (var row in leaving)
        {
            row.Visibility = Visibility.Visible;
            Fade(row, 0, FadeOut, TimeSpan.Zero);
        }
        foreach (var row in entering)
        {
            row.Opacity = 0;
            Fade(row, 1, FadeIn, TimeSpan.FromMilliseconds(leaving.Count > 0 ? 80 : 40));
        }
        _resizeHeight.From = before;
        _resizeHeight.To = after;
        _host.Height = before;
        _resize.Begin();
    }

    private void Settle()
    {
        _resize.Stop();
        _host.Height = double.NaN;
        foreach (var (row, visible) in _target)
        {
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) row.Opacity = 1;
        }
        _host.Visibility = _target.Values.Any(v => v) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Fade(UIElement element, double to, Duration duration, TimeSpan delay)
    {
        var animation = new DoubleAnimation { To = to, Duration = duration, BeginTime = delay };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var story = new Storyboard();
        story.Children.Add(animation);
        // Hand the final value to the element itself so later fades start from it.
        story.Completed += (_, _) => { story.Stop(); element.Opacity = to; };
        story.Begin();
    }
}
