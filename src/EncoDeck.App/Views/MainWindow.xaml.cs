// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.Core.Configuration;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace EncoDeck.App.Views;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Home"] = typeof(HomePage),
        ["Sound"] = typeof(SoundPage),
        ["Controls"] = typeof(ControlsPage),
        ["Connections"] = typeof(ConnectionsPage),
        ["Devices"] = typeof(DevicesPage),
        ["Labs"] = typeof(LabsPage),
        ["Settings"] = typeof(SettingsPage),
        ["About"] = typeof(AboutPage),
    };

    public MainWindow()
    {
        InitializeComponent();
        Loc.ApplyLanguage(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var appWindow = WindowInterop.GetAppWindow(this);
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "EncoDeck.ico"));
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        double scale = WindowInterop.GetScale(this);
        // Landscape by default, so the home page shows the device stage and its controls side by side.
        appWindow.Resize(new Windows.Graphics.SizeInt32((int)(1160 * scale), (int)(760 * scale)));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(640 * scale);
            presenter.PreferredMinimumHeight = (int)(480 * scale);
        }
        CenterOnScreen(appWindow);
        TrackBounds(appWindow);
        appWindow.Changed += (sender, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange) TrackBounds(sender);
        };

        Navigate("Home");

        var settings = App.Instance.Settings;
        settings.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            settings.PropertyChanged -= OnSettingsChanged;
            // WinUI does not promise Unloaded for the pages of a closing window, so a slider write still waiting for
            // its debounce (the earbuds' EQ, a settings slider) is sent here; the writes skip the UI refresh.
            Debouncer.FlushAll();
        };
        ShowUpdateBadge();
        AboutItem.Loaded += (_, _) => PinUpdateBadgeToIcon();
    }

    /// <summary>
    /// Puts the update dot on the icon's top-right corner in the open pane too, where WinUI puts it
    /// while the pane is closed (NavigationViewItemPresenter, ClosedCompactAndTopLevelItem). These are
    /// local values; that state's setters still apply over them and give the same place.
    /// </summary>
    private void PinUpdateBadgeToIcon()
    {
        if (FindNamed(AboutItem, "InfoBadgePresenter") is not FrameworkElement presenter)
        {
            // The item's presenter template is applied after the item loads.
            void Retry(object? sender, object e)
            {
                if (FindNamed(AboutItem, "InfoBadgePresenter") is null) return;
                AboutItem.LayoutUpdated -= Retry;
                PinUpdateBadgeToIcon();
            }
            AboutItem.LayoutUpdated += Retry;
            return;
        }
        Grid.SetColumn(presenter, 0);
        Grid.SetColumnSpan(presenter, 1);
        presenter.VerticalAlignment = VerticalAlignment.Top;
        presenter.HorizontalAlignment = HorizontalAlignment.Right;
        presenter.Margin = new Thickness(0, 2, 2, 0);
    }

    private static DependencyObject? FindNamed(DependencyObject root, string name)
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var childName } && childName == name) return child;
            if (FindNamed(child, name) is { } found) return found;
        }
        return null;
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModels.SettingsViewModel.IsUpdateAvailable)) ShowUpdateBadge();
    }

    private void ShowUpdateBadge()
    {
        bool available = App.Instance.Settings.IsUpdateAvailable;
        UpdateBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(AboutItem, available ? Loc.Get("Update_BadgeHint") : null);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(AboutItem, available ? Loc.Get("Update_BadgeHint") : string.Empty);
    }

    /// <summary>
    /// The restored size and position (and whether it is maximized), kept up to date while the window
    /// is open so it can still be read once the window has closed.
    /// </summary>
    public WindowBounds? SavedBounds { get; private set; }

    /// <summary>
    /// Puts the window where it was last time, unless that is no longer on any screen (a monitor was
    /// removed or rearranged). Maximizing waits for <see cref="ApplyMaximized"/>, after it is shown.
    /// </summary>
    public void RestoreBounds(WindowBounds saved)
    {
        // The title bar must be reachable: its top middle has to be on a screen.
        var titlePoint = new Windows.Graphics.PointInt32(saved.X + saved.Width / 2, saved.Y + 8);
        if (DisplayArea.GetFromPoint(titlePoint, DisplayAreaFallback.None) is not { } display) return;
        var area = display.WorkArea;
        int width = Math.Min(saved.Width, area.Width);
        int height = Math.Min(saved.Height, area.Height);
        int x = Math.Clamp(saved.X, area.X - width / 2, area.X + area.Width - width / 2);
        int y = Math.Clamp(saved.Y, area.Y, area.Y + area.Height - 48);
        var appWindow = WindowInterop.GetAppWindow(this);
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        TrackBounds(appWindow);
        SavedBounds = SavedBounds! with { Maximized = saved.Maximized };
    }

    /// <summary>Maximizes the window when it was maximized last time; call once it is shown.</summary>
    public void ApplyMaximized()
    {
        if (SavedBounds is { Maximized: true } && WindowInterop.GetAppWindow(this).Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }

    public void Navigate(string tag)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null && !ReferenceEquals(Nav.SelectedItem, item))
        {
            Nav.SelectedItem = item;
        }
        else if (item is not null && ContentFrame.Content is null)
        {
            NavigateFrame(tag);
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag }) NavigateFrame(tag);
    }

    private void NavigateFrame(string tag)
    {
        if (Pages.TryGetValue(tag, out var page) && ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        }
    }

    /// <summary>Diagnostics (<see cref="UiSnapshot"/>): open or collapse the navigation pane.</summary>
    internal void SetPaneOpenForDiagnostics(bool open) => Nav.IsPaneOpen = open;

    private void TrackBounds(AppWindow appWindow)
    {
        var state = (appWindow.Presenter as OverlappedPresenter)?.State ?? OverlappedPresenterState.Restored;
        // Minimized keeps what was there before; maximized keeps the restored bounds under it.
        if (state == OverlappedPresenterState.Minimized) return;
        if (state == OverlappedPresenterState.Maximized)
        {
            if (SavedBounds is not null) SavedBounds = SavedBounds with { Maximized = true };
            return;
        }
        SavedBounds = new WindowBounds
        {
            X = appWindow.Position.X,
            Y = appWindow.Position.Y,
            Width = appWindow.Size.Width,
            Height = appWindow.Size.Height,
        };
    }

    private static void CenterOnScreen(AppWindow appWindow)
    {
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int x = area.X + Math.Max(0, (area.Width - appWindow.Size.Width) / 2);
        int y = area.Y + Math.Max(0, (area.Height - appWindow.Size.Height) / 2);
        appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }
}
