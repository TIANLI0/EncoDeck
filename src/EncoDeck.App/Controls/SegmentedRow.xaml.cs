// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using EncoDeck.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;

namespace EncoDeck.App.Controls;

/// <summary>
/// A titled single-choice row (noise levels, sub-modes): the options share one track and an accent
/// pill slides to the chosen one. A click moves the pill at once; a spinner by the title appears only
/// if the earbuds are slow to answer (<see cref="PendingSpinner"/>); if they keep another choice, the
/// pill slides to what they report.
/// </summary>
public sealed partial class SegmentedRow : UserControl
{
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(220);
    // The confirmed state can reach the view model a moment after the command returns.
    private static readonly TimeSpan ConfirmGrace = TimeSpan.FromMilliseconds(400);

    private readonly List<(NoiseLevelViewModel Item, ToggleButton Button)> _items = [];
    private readonly Storyboard _slide = new();
    private readonly DoubleAnimation _slideX;
    private readonly DoubleAnimation _slideY;
    private readonly PendingSpinner _spinner;
    private string? _pendingId;
    private bool _placed;
    private double _x, _y;
    private bool _interactive = true;

    public SegmentedRow()
    {
        InitializeComponent();
        _spinner = new PendingSpinner(Spinner);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _slideX = new DoubleAnimation { Duration = SlideDuration, EasingFunction = ease };
        _slideY = new DoubleAnimation { Duration = SlideDuration, EasingFunction = ease };
        Storyboard.SetTarget(_slideX, Move);
        Storyboard.SetTargetProperty(_slideX, "X");
        Storyboard.SetTarget(_slideY, Move);
        Storyboard.SetTargetProperty(_slideY, "Y");
        _slide.Children.Add(_slideX);
        _slide.Children.Add(_slideY);
        Host.SizeChanged += (_, _) => Place(animate: false);
    }

    /// <summary>Sends the choice to the earbuds; the row shows it as chosen until the task ends.</summary>
    public Func<string, Task>? Invoke { get; set; }

    /// <summary>True while another command the choice depends on runs; clicks are ignored then.</summary>
    public Func<bool>? IsBusy { get; set; }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public double Spacing
    {
        get => Root.Spacing;
        set => Root.Spacing = value;
    }

    public bool IsPending => _pendingId is not null;

    public bool Interactive
    {
        get => _interactive;
        set
        {
            if (_interactive == value) return;
            _interactive = value;
            Refresh(animate: false);
        }
    }

    /// <summary>Shows <paramref name="items"/>, <paramref name="perRow"/> to a row.</summary>
    public void SetItems(IEnumerable<NoiseLevelViewModel> items, int perRow = int.MaxValue)
    {
        Detach();
        _items.Clear();
        _pendingId = null;
        _placed = false;
        Host.Children.Clear();
        Host.ColumnDefinitions.Clear();
        Host.RowDefinitions.Clear();
        var list = items.ToList();
        int columns = Math.Min(list.Count, perRow);
        for (int i = 0; i < columns; i++) Host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; columns > 0 && i < (list.Count + columns - 1) / columns; i++) Host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var item in list)
        {
            var button = new ToggleButton
            {
                Style = (Style)Application.Current.Resources["SegmentedItemStyle"],
                Content = FitLabel.Create(item.Label),
                Tag = item.Id,
            };
            AutomationProperties.SetName(button, item.Label);
            Grid.SetColumn(button, _items.Count % columns);
            Grid.SetRow(button, _items.Count / columns);
            button.Click += OnClick;
            button.SizeChanged += (_, _) => Place(animate: false);
            item.PropertyChanged += OnItemChanged;
            _items.Add((item, button));
            Host.Children.Add(button);
        }
        Refresh(animate: false);
    }

    /// <summary>Stops listening to the shown items (the view model outlives the control).</summary>
    public void Detach()
    {
        foreach (var (item, _) in _items) item.PropertyChanged -= OnItemChanged;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Refresh(animate: true);

    private string? ConfirmedId => _items.FirstOrDefault(i => i.Item.IsSelected).Item?.Id;

    private void Refresh(bool animate)
    {
        string? shown = _pendingId ?? ConfirmedId;
        foreach (var (item, button) in _items)
        {
            button.IsChecked = item.Id == shown;
            button.IsEnabled = _interactive;
        }
        _spinner.Waiting = _pendingId is not null;
        Place(animate);
    }

    private void Place(bool animate)
    {
        string? shown = _pendingId ?? ConfirmedId;
        var target = _items.FirstOrDefault(i => i.Item.Id == shown).Button;
        if (target is null || target.ActualWidth <= 0 || target.Visibility != Visibility.Visible)
        {
            Indicator.Opacity = 0;
            _placed = false;
            return;
        }
        var at = target.TransformToVisual(Layer).TransformPoint(default);
        Indicator.Width = target.ActualWidth;
        Indicator.Height = target.ActualHeight;
        Indicator.Opacity = _interactive ? 1 : 0.45;
        if (_placed && Math.Abs(at.X - _x) < 0.5 && Math.Abs(at.Y - _y) < 0.5) return;
        // Start from where the pill rests (the last target): local values are what Stop() reverts to.
        _slide.Stop();
        Move.X = _x;
        Move.Y = _y;
        _x = at.X;
        _y = at.Y;
        if (animate && _placed)
        {
            _slideX.To = _x;
            _slideY.To = _y;
            _slide.Begin();
        }
        else
        {
            Move.X = _x;
            Move.Y = _y;
        }
        _placed = true;
    }

    private async void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string id }) return;
        if (_pendingId is not null || Invoke is not { } invoke || IsBusy?.Invoke() == true || id == ConfirmedId)
        {
            Refresh(animate: false); // undo the local toggle
            return;
        }
        await ChooseAsync(id, invoke);
    }

    /// <summary>Shows <paramref name="id"/> as chosen while <paramref name="invoke"/> runs (also the off-screen preview's path).</summary>
    internal async Task ChooseAsync(string id, Func<string, Task> invoke)
    {
        _pendingId = id;
        Refresh(animate: true);
        try
        {
            await invoke(id);
            var deadline = DateTime.UtcNow + ConfirmGrace;
            while (ConfirmedId != id && DateTime.UtcNow < deadline) await Task.Delay(50);
        }
        catch
        {
            // The view model reports command failures itself; the row only shows the confirmed state again.
        }
        finally
        {
            if (_pendingId == id) _pendingId = null;
            Refresh(animate: true);
        }
    }
}
