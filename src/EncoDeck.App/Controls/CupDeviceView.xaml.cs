// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using EncoDeck.App.Services;
using EncoDeck.Bluetooth.Cups;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace EncoDeck.App.Controls;

/// <summary>
/// Home's view of the connected cup device: level per channel, artwork, feature support, Stop all
/// and a slider per channel. Shown only while a session exists; connecting happens on the Devices
/// page. Sliders start at zero for every new connection.
/// </summary>
public sealed partial class CupDeviceView : UserControl
{
    private readonly Dictionary<ActuatorChannel, (Slider Slider, TextBlock Value)> _sliders = [];
    private readonly Dictionary<ActuatorChannel, TextBlock> _levels = [];
    private ICupSession? _builtFor;
    private bool _syncing;

    public CupDeviceView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Cups.Changed += OnCupsChanged;
            App.Instance.Host.Devices.KnownDevicesChanged += OnCupsChanged;
            Sync();
        };
        Unloaded += (_, _) =>
        {
            // Hiding the view does not stop output; picking a headset in the switcher and the tray item do.
            Cups.Changed -= OnCupsChanged;
            App.Instance.Host.Devices.KnownDevicesChanged -= OnCupsChanged;
        };
    }

    private static CupService Cups => App.Instance.Host.Cups;

    private void OnCupsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Sync);

    private void Sync()
    {
        var session = Cups.Session;
        bool several = DeviceSwitcherMenu.EntryCount > 1;
        TitleText.Text = SwitcherText.Text = session?.Identity.Name ?? string.Empty;
        Switcher.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = several ? Visibility.Collapsed : Visibility.Visible;
        if (session is null)
        {
            _builtFor = null;
            _sliders.Clear();
            _levels.Clear();
            ChannelHost.Children.Clear();
            LevelHost.Children.Clear();
            SupportHost.Children.Clear();
            StillHost.Child = null;
            Model.ModelPath = null;
            return;
        }

        var connection = session.Store.Current.Connection;
        StatusText.Text = DescribeState(session);
        bool busy = connection is DeviceConnectionState.ControlConnecting or DeviceConnectionState.Reconnecting;
        Busy.IsActive = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(_builtFor, session))
        {
            _builtFor = session;
            CommandResult.Text = string.Empty;
            DetailText.Text = $"{Cups.BrandOf(session.Profile.Brand)?.DisplayName ?? session.Profile.Brand} · {session.Profile.DisplayName} · {session.Identity.Address.ToRedactedString()}";
            BuildArtwork(session);
            BuildLevels(session);
            BuildChannels(session);
            BuildSupport(session);
        }

        bool ready = connection == DeviceConnectionState.ControlReady && Cups.IsEnabled;
        _syncing = true;
        try
        {
            foreach (var (channel, row) in _sliders)
            {
                int level = Cups.LevelOf(channel);
                row.Slider.Value = level;
                row.Value.Text = level.ToString(CultureInfo.CurrentCulture);
                row.Slider.IsEnabled = ready;
                ShowLevel(channel, level);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    internal static string DescribeState(ICupSession session)
    {
        var state = session.Store.Current;
        string status = Loc.Get(state.Connection switch
        {
            DeviceConnectionState.ControlReady => "Cup_State_Ready",
            DeviceConnectionState.Reconnecting => "Cup_State_Reconnecting",
            DeviceConnectionState.ControlUnavailable => "Cup_State_Unavailable",
            DeviceConnectionState.Disconnected => "Cup_State_Disconnected",
            _ => "Cup_State_Connecting",
        });
        if (state.LastError is { } last && state.Connection != DeviceConnectionState.ControlReady) status += " · " + last.Message;
        return status;
    }

    private void OnSwitcherOpening(object? sender, object e) => DeviceSwitcherMenu.Populate(SwitcherMenu, cupShown: true);

    // ---- stage -----------------------------------------------------------------------------------

    /// <summary>The 3D model when the model has one, else its 2D image or the capsule placeholder.</summary>
    private void BuildArtwork(ICupSession session)
    {
        string? model = CupArtwork.ModelPath(session.Profile);
        Model.ModelPath = model;
        Model.Visibility = model is null ? Visibility.Collapsed : Visibility.Visible;
        StillHost.Child = model is null ? CupArtwork.CreateImage(session.Profile, 240) : null;
    }

    private static string GlyphOf(ActuatorChannel channel) => channel switch
    {
        ActuatorChannel.Vibration => "\uE877",
        ActuatorChannel.Linear => "\uE8CB",
        _ => "\uE7AD",
    };

    private void BuildLevels(ICupSession session)
    {
        _levels.Clear();
        LevelHost.Children.Clear();
        foreach (var channel in session.Channels)
        {
            string name = Loc.Get($"Cup_Channel_{channel}");
            var badge = new Border
            {
                Style = (Style)Resources["BadgeStyle"],
                Child = new FontIcon { Glyph = GlyphOf(channel), FontSize = 11, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] },
            };
            var value = new TextBlock { Style = (Style)Resources["LevelTextStyle"] };
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            item.Children.Add(badge);
            item.Children.Add(value);
            ToolTipService.SetToolTip(item, name);
            AutomationProperties.SetName(item, name);
            LevelHost.Children.Add(item);
            _levels[channel] = value;
            ShowLevel(channel, Cups.LevelOf(channel));
        }
        LevelDivider.Visibility = _levels.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowLevel(ActuatorChannel channel, int level)
    {
        if (_levels.TryGetValue(channel, out var text)) text.Text = level.ToString(CultureInfo.CurrentCulture) + "%";
    }

    // ---- output ----------------------------------------------------------------------------------

    private void BuildChannels(ICupSession session)
    {
        _sliders.Clear();
        ChannelHost.Children.Clear();
        foreach (var channel in session.Channels)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            var label = new TextBlock { Text = Loc.Get($"Cup_Channel_{channel}"), VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                StepFrequency = 1,
                SmallChange = 1,
                LargeChange = 10,
                Tag = channel,
                IsEnabled = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(slider, label.Text);
            var value = new TextBlock { Text = "0", VerticalAlignment = VerticalAlignment.Center, HorizontalTextAlignment = TextAlignment.Right };
            slider.ValueChanged += OnSliderChanged;
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(value, 2);
            grid.Children.Add(label);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            ChannelHost.Children.Add(grid);
            _sliders[channel] = (slider, value);
        }
    }

    private void BuildSupport(ICupSession session)
    {
        SupportHost.Children.Clear();
        foreach (var channel in Enum.GetValues<ActuatorChannel>())
        {
            string key = !session.Profile.Channels.Contains(channel) ? "Support_Unsupported"
                : session.Profile.ChannelsNeedingSleeve.Contains(channel) ? "Cup_Support_NeedsSleeve"
                : "Support_Supported";
            SupportHost.Children.Add(SupportRow(Loc.Get($"Cup_Channel_{channel}"), Loc.Get(key)));
        }
        SupportHost.Children.Add(SupportRow(Loc.Get("Cup_Support_Headset"), Loc.Get("Support_Unsupported")));
    }

    private static SettingRow SupportRow(string header, string value) => new()
    {
        Header = header,
        Action = new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private async void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || sender is not Slider { Tag: ActuatorChannel channel }) return;
        int level = (int)Math.Round(e.NewValue);
        if (_sliders.TryGetValue(channel, out var row)) row.Value.Text = level.ToString(CultureInfo.CurrentCulture);
        ShowLevel(channel, level);
        try
        {
            var result = await Cups.SetLevelAsync(channel, level);
            if (result.Status == CommandStatus.Cancelled) return;
            CommandResult.Text = result.IsSuccess ? Loc.Get("Cup_Written") : Loc.Format("Cup_CommandFailed", result.Detail ?? result.Status.ToString());
        }
        catch (Exception ex)
        {
            CommandResult.Text = Loc.Format("Cup_CommandFailed", ex.Message);
        }
    }

    private async void OnStopAll(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await Cups.EmergencyStopAsync();
            CommandResult.Text = result.IsSuccess ? Loc.Get("Cup_Stopped") : Loc.Format("Cup_StopFailed", result.Detail ?? result.Status.ToString());
        }
        catch (Exception ex)
        {
            CommandResult.Text = Loc.Format("Cup_StopFailed", ex.Message);
        }
    }

    private void OnReconnect(object sender, RoutedEventArgs e) => Cups.Session?.Reconnect();

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        try
        {
            await Cups.DisconnectAsync();
        }
        catch (Exception ex)
        {
            CommandResult.Text = Loc.Format("Cup_CommandFailed", ex.Message);
        }
    }

    // ---- layout ----------------------------------------------------------------------------------

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= 820;
        Grid.SetColumnSpan(MainColumn, wide ? 1 : 2);
        Grid.SetRow(SideColumn, wide ? 0 : 1);
        Grid.SetColumn(SideColumn, wide ? 1 : 0);
        Grid.SetColumnSpan(SideColumn, wide ? 1 : 2);
    }
}
