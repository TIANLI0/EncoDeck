// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Text.Json;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Configuration;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Resources;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Moondrop;
using EncoDeck.Protocol.Brands.Sennheiser;
using EncoDeck.App.ViewModels;
using EncoDeck.Protocol.Oppo.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EncoDeck.App.Views;
using EncoDeck.Platform.Shell;

namespace EncoDeck.App.Services;

/// <summary>Explicitly launched, persistent manual previews; the Bluetooth runtime is never started.</summary>
internal sealed class BrandDemo
{
    /// <summary>A demo model. Without <see cref="Directory"/> its artwork comes from EncoAPI like a real connection.</summary>
    internal sealed record Entry(string Brand, string Name, string? Directory = null, string? ProfileId = null, int? ColorId = null,
        string? Popup = null, string? PopupDark = null, string? Model = null, string? Motion = null, string? MotionDark = null);
    internal sealed record ArtworkReport(string? Key, int? Color, bool Ok, int Files, bool FromCache, string? Failure, string[] Roles);
    private readonly App _app;
    private readonly Entry[] _entries;
    private readonly Window _picker = new() { Title = "EncoDeck · 多品牌手动演示" };
    private readonly ComboBox _brands = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TrayIconService _tray;
    private Entry? _current;
    private ArtworkReport? _artwork;
    private bool _exiting;

    public BrandDemo(App app, string manifest, DispatcherQueue dispatcher)
    {
        _app = app;
        _entries = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(manifest), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("No demo models.");
        if (_entries.Length == 0) throw new InvalidDataException("No demo models.");
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifest))!, "session");
        app.SetHostForDiagnostics(AppHost.Create(rootOverride: root));
        // Downloaded artwork lands in the demo session's own cache and is picked up by the watcher.
        app.Host.Resources.StartWatching();
        app.CreateViewModelsForDiagnostics(dispatcher, new DemoCommands(app));
        app.Windows = ForDemoWindows(app);
        _tray = new TrayIconService(app.Device, app.Host.Settings, app.Windows, Exit);
        _tray.Create();

        var content = new StackPanel { Padding = new Thickness(24), Spacing = 14 };
        content.Children.Add(new TextBlock { Text = "多品牌耳机 · 手动演示", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = "选择型号后查看实际 WinUI 页面。电量与连接状态为演示数据；素材与真机连接时一样通过 EncoAPI 拉取（缓存在演示会话目录）；本模式不连接蓝牙。", TextWrapping = TextWrapping.Wrap });
        foreach (var entry in _entries) _brands.Items.Add($"{entry.Brand} · {entry.Name}");
        _brands.SelectionChanged += async (_, _) =>
        {
            if (_brands.SelectedIndex < 0) return;
            await SelectAsync(_entries[_brands.SelectedIndex]);
        };
        content.Children.Add(_brands);
        Add("打开该型号首页", () => app.Windows.ShowMain("Home"));
        Add("重播连接／开盖弹窗", () => app.Windows.ShowConnectionPopup(TimeSpan.FromSeconds(12)));
        Add("打开托盘小窗口（也可单击系统托盘图标）", () => app.Windows.ToggleTrayPanel(_tray.Id));
        var theme = new ComboBox { Header = "预览主题", HorizontalAlignment = HorizontalAlignment.Stretch };
        theme.Items.Add("跟随系统"); theme.Items.Add("浅色"); theme.Items.Add("深色");
        theme.SelectedIndex = 0;
        theme.SelectionChanged += (_, _) => app.Host.Settings.Update(s => s with { Theme = theme.SelectedIndex switch { 1 => AppTheme.Light, 2 => AppTheme.Dark, _ => AppTheme.System } });
        content.Children.Add(theme);
        content.Children.Add(_status);
        Add("退出全部演示窗口", Exit);
        _picker.Content = new ScrollViewer { Content = content };
        WindowInterop.GetAppWindow(_picker).Resize(new Windows.Graphics.SizeInt32(560, 690));
        _picker.Closed += (_, _) => Exit();
        void Add(string text, Action action)
        {
            var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => action(); content.Children.Add(button);
        }
    }

    private WindowManager ForDemoWindows(App app) => new(new ThemeService(app.Host.Settings), static () => true, Exit);

    public async void Show(bool checkAll = false, string? initialBrand = null, bool exitAfterCheck = false)
    {
        if (checkAll) await CheckAllAsync();
        // Unattended check: never show or activate the picker.
        if (checkAll && exitAfterCheck) { Exit(); return; }
        _brands.SelectedIndex = Math.Max(0, Array.FindIndex(_entries, e => string.Equals(e.Brand, initialBrand, StringComparison.OrdinalIgnoreCase)));
        _picker.Activate();
    }

    private async Task SelectAsync(Entry entry, bool show = true)
    {
        _current = entry;
        _app.Windows.CloseAll();
        var device = new DeviceIdentity("brand-demo-" + entry.Brand, entry.Name, new BluetoothAddress(0));
        var profiles = BuiltInProfiles.Create();
        var profile = entry.ProfileId is { } id ? profiles.Single(p => p.ModelId == id)
            : profiles.OrderByDescending(p => p.MatchScore(device)).First(p => p.MatchScore(device) > 0);
        bool local = entry.Directory is { } folder && Directory.Exists(folder);
        // Prepared folders stay pinned; otherwise the normal resolver shows what EncoAPI provided.
        _app.Device.ArtworkPinned = local;
        // Demo state simulates a successful session; no Bluetooth session runs.
        var state = CreateState(DemoDiscovery.AfterSession(profile, entry.Name), device, DateTimeOffset.UtcNow) with { ColorId = entry.ColorId };
        _app.Host.Store.Reset(state);
        _artwork = null;
        _status.Text = $"当前：{entry.Name}\n素材：{(local ? "本地素材文件夹" : "正在从 EncoAPI 拉取…")}";
        if (!local) _artwork = await FetchArtworkAsync(entry, state);
        await Task.Delay(300);
        if (_current != entry || _exiting) return;
        ApplyArtwork(entry, local);
        _status.Text = $"当前：{entry.Name}\n素材：{DescribeArtwork(local)}\n首页：{DescribeHome()}；弹窗：{DescribePopup()}。"
            + "可重复打开弹窗；托盘图标提示当前型号。所有设置只修改演示状态。";
        if (show)
        {
            _app.Windows.ShowMain("Home");
            _picker.Activate();
        }
    }

    /// <summary>
    /// A state like the one a successful session reads: every feature the profile offers (including
    /// MOONDROP experimental ones, which the settings switch still gates) gets a plausible value.
    /// </summary>
    internal static DeviceState CreateState(IDeviceProfile profile, DeviceIdentity device, DateTimeOffset now)
    {
        Observed<T> O<T>(T value) => Observed.From(value, now, StateSource.QueryResponse);
        var model = (profile as OppoDeviceProfile)?.Definition;
        bool Has(DeviceCapability cap) => model is not null
            ? model.Capabilities.Get(cap).Support == CapabilitySupport.Supported
            : profile.Capabilities.Get(cap).Support is CapabilitySupport.Supported or CapabilitySupport.Experimental;
        var toggles = (model is not null
            ? model.FeatureSwitches.Where(f => f.Support == CapabilitySupport.Supported).Select(f => f.Toggle)
            : (profile as IControllableProfile)?.Toggles ?? []).Distinct().ToArray();
        var noise = profile.NoiseControlOptions.FirstOrDefault(o => o.Mode == NoiseControlMode.NoiseCancellation && o.Support == CapabilitySupport.Supported)
            ?? profile.NoiseControlOptions.FirstOrDefault();
        var eq = profile.EqualizerPresets.FirstOrDefault(p => p.Support == CapabilitySupport.Supported);
        bool earbuds = profile.Form != DeviceForm.Headphones;
        uint cycle = model?.NoiseCycleIndices.Aggregate(0u, (mask, index) => mask | 1u << index) ?? 0;
        bool dual = Has(DeviceCapability.DualDeviceConnection);
        return DeviceState.Empty with
        {
            Device = device, Profile = profile, Connection = DeviceConnectionState.ControlReady,
            ProductId = model?.ProductIds.Count > 0 ? model.ProductIds[0] : null,
            Battery = new BatteryStatus(O(new BatteryReading(85, false)), earbuds ? O(new BatteryReading(80, false)) : null,
                earbuds ? O(new BatteryReading(65, false)) : null),
            Wear = O(new WearStatus(true, true, false, false, false)),
            NoiseControl = Has(DeviceCapability.NoiseCancellation) && noise is not null ? O(noise.ToState()) : null,
            Equalizer = Has(DeviceCapability.Equalizer) && eq is not null ? O(new EqualizerState(eq.Id, 0)) : null,
            Toggles = toggles.ToImmutableDictionary(t => t, t => O(t is DeviceToggle.DualDeviceConnection or DeviceToggle.InEarDetection
                or DeviceToggle.LowLatency or DeviceToggle.IndicatorLight or DeviceToggle.PromptTone)),
            Spatial = Has(DeviceCapability.SpatialAudio) ? O(SpatialAudioMode.Off) : null,
            Firmware = Has(DeviceCapability.FirmwareInformation) ? O(new FirmwareInfo("1.0.0", null, new Dictionary<string, string>())) : null,
            GameSound = model is { GameSoundTypes.Count: > 0 } ? O(new GameSoundState(0, model.GameSoundTypes)) : null,
            PromptVolume = Has(DeviceCapability.PromptVolume)
                ? O(model is not null ? (model.PromptVolumeRange.Min + model.PromptVolumeRange.Max) / 2 : profile is MoondropProfile ? 40 : 5) : null,
            Gain = Has(DeviceCapability.Gain) ? O(1) : null,
            BassEngine = Has(DeviceCapability.BassEngine) ? O(profile is MoondropProfile ? new BassEngineState(0, 0, 0) : new BassEngineState(-5, 5, 0)) : null,
            KeyFunctions = Has(DeviceCapability.TouchCustomization) ? O(DemoKeys(profile, model)) : null,
            NoiseCycle = cycle != 0 ? O(new NoiseCycleState(cycle, cycle)) : null,
            HeadMotionType = toggles.Contains(DeviceToggle.HeadGestures) ? O(0) : null,
            SourceDevices = dual ? O<IReadOnlyList<SourceDevice>>(
            [
                new([0x11, 0x22, 0x33, 0x44, 0x55, 0x66], "This PC", 2, true, true, 2),
                new([0x01, 0x02, 0x03, 0x04, 0x05, 0x06], "Phone", 2, false, false, 1),
                new([0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F], "Tablet", 0, false, false, 3),
            ]) : null,
            PreferredDevice = dual ? O(new PreferredDeviceState(null)) : null,
            CustomEqualizers = Has(DeviceCapability.CustomEqualizer) && model?.CustomEqualizer is { } spec
                ? O<IReadOnlyList<CustomEqualizer>>([DemoCustomEqualizer(spec, 1, false)]) : null,
        };
    }

    private static IReadOnlyList<KeyFunction> DemoKeys(IDeviceProfile profile, OppoModelDefinition? model)
    {
        if (profile is SennheiserProfile { IsTw4: true })
            return (from side in new[] { EarSide.Left, EarSide.Right }
                    from action in SennheiserGestures.Actions
                    select new KeyFunction(side, 0, action, action == GestureAction.SqueezeAndHold ? (side == EarSide.Left ? 5 : 4) : 1)).ToArray();
        if (profile is MoondropProfile)
            return [.. from side in new[] { EarSide.Left, EarSide.Right }
                       from pair in new[] { (GestureAction.SingleSqueeze, 1), (GestureAction.DoubleSqueeze, 2), (GestureAction.TripleSqueeze, 3),
                                            (GestureAction.SqueezeAndHold, 6), (GestureAction.LongHold, 0) }
                       select new KeyFunction(side, 0, pair.Item1, pair.Item2)];
        var keys = new List<KeyFunction>();
        foreach (var row in model?.GestureRows.Where(r => r.IsWritten) ?? [])
        {
            // The hold row reports noise control so the long-press cycle editor is shown too.
            int function = row.EditsNoiseCycle && row.AllowedFunctions.Contains(FeaturesViewModel.NoiseControlFunction)
                ? FeaturesViewModel.NoiseControlFunction : row.DefaultFunction ?? row.AllowedFunctions.FirstOrDefault(f => f != 0);
            if (row.BothEars) keys.Add(new KeyFunction(EarSide.Both, row.Button, row.Action, function));
            else keys.AddRange(new[] { EarSide.Left, EarSide.Right }.Select(side => new KeyFunction(side, row.Button, row.Action, function)));
        }
        return keys;
    }

    private static CustomEqualizer DemoCustomEqualizer(OppoCustomEqualizerSpec spec, int id, bool selected) =>
        new(id, $"自定义{id}", spec.MinGain, spec.MaxGain, selected, spec.BandFrequencies.Select(f => new EqualizerBand(f, 0)).ToArray());

    /// <summary>
    /// Downloads through the app's own <see cref="Platform.Resources.DeviceResourceSync"/>, with the key a real
    /// connection would report. OPPO-family keys need a colour; without one the common colour ids are tried.
    /// </summary>
    private async Task<ArtworkReport> FetchArtworkAsync(Entry entry, DeviceState state)
    {
        if (DeviceResourceKey.FromState(state, state.ProductId) is not { } key) return new(null, null, false, 0, false, "No resource identity", []);
        int?[] colors = key.UsesOplusId && entry.ColorId is null ? [1, 0, 2, 3, 4, 5] : [entry.ColorId];
        string? failure = null;
        foreach (int? color in colors)
        {
            var attempt = key with { ColorId = color };
            if (!attempt.CanDownload) { failure = "Key is not downloadable"; continue; }
            if (_current != entry) break;
            if (color != _app.Host.Store.Current.ColorId) _app.Host.Store.Reset(_app.Host.Store.Current with { ColorId = color });
            try
            {
                var result = await _app.Host.ResourceSync.RefreshAsync(attempt);
                _app.Host.Resources.Invalidate();
                var roles = Enum.GetValues<DeviceResourceRole>().Where(r => _app.Host.Resources.Resolve(attempt, r) is not null).Select(r => r.ToString()).ToArray();
                return new(attempt.CacheIdentity, color, true, result.Files, result.FromCache, null, roles);
            }
            catch (Exception ex)
            {
                failure = $"{Platform.Resources.DeviceResourceStatus.Classify(ex)}: {ex.Message}";
            }
        }
        return new(key.CacheIdentity, entry.ColorId, false, 0, false, failure, []);
    }

    private string DescribeArtwork(bool local) => local ? "本地素材文件夹" : _artwork switch
    {
        { Ok: true } a => $"EncoAPI {(a.FromCache ? "缓存" : "已下载")} {a.Files} 个文件{(a.Color is { } c ? $"（颜色 {c}）" : "")}",
        { } a => $"EncoAPI 未取得（{a.Failure}），使用通用插画",
        null => "未请求",
    };

    private string DescribeHome()
    {
        var d = _app.Device;
        return d.DeviceModelPath is { } model ? Path.GetExtension(model) == ".vfxms" ? "官方 3D 场景，可拖动旋转" : "官方动态转盘，可拖动旋转"
            : d.DeviceMotionPath is not null ? "官方产品动画"
            : d.HasDeviceImage || d.HasComponentArtwork ? "官方产品图（通用悬浮动效，非原厂开盖）" : "通用插画";
    }

    private string DescribePopup()
    {
        var d = _app.Device;
        if (d.PopupMediaPath is not { } popup || popup == d.DeviceImagePath) return d.HasDeviceImage ? "产品图入场动效（非原厂开盖）" : "通用插画";
        return Path.GetExtension(popup).ToLowerInvariant() switch
        {
            ".json" => "官方 Lottie 动画",
            ".webp" => "官方逐帧开盖动画",
            _ when d.PopupMediaIsVideo => "官方连接视频",
            _ => "官方弹窗图",
        };
    }

    private void ApplyArtwork(Entry entry, bool local)
    {
        _app.Device.DeviceName = entry.Name;
        if (!local) return;
        UiSnapshot.ApplyArtworkDirectory(_app, entry.Directory);
        _app.Device.DeviceModelPath = entry.Model;
        _app.Device.DeviceMotionIsVideo = IsVideo(entry.Motion);
        _app.Device.DeviceMotionPath = entry.Motion;
        _app.Device.DeviceMotionDarkPath = entry.MotionDark;
        if (entry.Model is not null || entry.Motion is not null) _app.Device.HasComponentArtwork = false;
        _app.Device.PopupMediaPath = entry.Popup ?? _app.Device.DeviceImagePath;
        _app.Device.PopupMediaDarkPath = entry.PopupDark ?? _app.Device.PopupMediaPath;
        _app.Device.PopupMediaIsVideo = IsVideo(entry.Popup);
        _app.Device.PopupMediaDarkIsVideo = IsVideo(entry.PopupDark ?? entry.Popup);
    }

    private static bool IsVideo(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() is ".mp4" or ".webm";

    private async Task CheckAllAsync()
    {
        string output = Path.Combine(_app.Host.Paths.Root, "previews");
        Directory.CreateDirectory(output);
        var results = new List<object>();
        foreach (var entry in _entries)
        {
            try
            {
                await SelectAsync(entry, show: false);
                var main = new MainWindow();
                double scale = WindowInterop.GetScale(main);
                WindowInterop.GetAppWindow(main).MoveAndResize(new Windows.Graphics.RectInt32(-24000, -24000, (int)(1160 * scale), (int)(760 * scale)));
                WindowInterop.GetAppWindow(main).Show(activateWindow: false);
                main.Navigate("Home");
                await Task.Delay(900);
                string[] mediaBefore = UiSnapshot.Descendants(main.Content).OfType<Controls.DeviceMedia>().Select(m => m.DiagnosticState).ToArray();
                string[] modelBefore = UiSnapshot.Descendants(main.Content).OfType<Controls.DeviceModelView>().Select(m => m.DiagnosticState).ToArray();
                await UiSnapshot.CaptureAsync(main, Path.Combine(output, entry.Brand + "-home.png"));
                await Task.Delay(1300);
                string[] modelAfter = UiSnapshot.Descendants(main.Content).OfType<Controls.DeviceModelView>().Select(m => m.DiagnosticState).ToArray();
                string[] homeMedia = UiSnapshot.Descendants(main.Content).OfType<Controls.DeviceMedia>().Select(m => m.DiagnosticState).ToArray();
                await UiSnapshot.CaptureAsync(main, Path.Combine(output, entry.Brand + "-home-motion.png"));
                string[] themeMedia = [];
                if (entry.MotionDark is not null && main.Content is FrameworkElement themed)
                {
                    themed.RequestedTheme = ElementTheme.Light;
                    await Task.Delay(700);
                    await UiSnapshot.CaptureAsync(main, Path.Combine(output, entry.Brand + "-home-light.png"));
                    themed.RequestedTheme = ElementTheme.Dark;
                    await Task.Delay(700);
                    await UiSnapshot.CaptureAsync(main, Path.Combine(output, entry.Brand + "-home-dark.png"));
                    themeMedia = UiSnapshot.Descendants(main.Content).OfType<Controls.DeviceMedia>().Select(m => m.DiagnosticState).ToArray();
                }
                var features = new Dictionary<string, string[]>();
                foreach (string page in new[] { "Sound", "Controls", "Devices" })
                {
                    main.Navigate(page);
                    await Task.Delay(700);
                    features[page] = VisibleFeatures(main.Content);
                    var scroller = UiSnapshot.FindScrollViewer(main.Content);
                    for (int shot = 0; ; shot++)
                    {
                        await UiSnapshot.CaptureAsync(main, Path.Combine(output, $"{entry.Brand}-{page.ToLowerInvariant()}-{shot}.png"));
                        if (scroller is null || scroller.VerticalOffset >= scroller.ScrollableHeight || shot >= 5) break;
                        scroller.ChangeView(null, Math.Min(scroller.ScrollableHeight, scroller.VerticalOffset + scroller.ViewportHeight * 0.85), null, disableAnimation: true);
                        await Task.Delay(150);
                    }
                }
                main.Close();
                var popup = _app.Windows.ShowConnectionPopup(TimeSpan.FromSeconds(8), placement: new PixelRect(-24000, -24000, 500, 500));
                await Task.Delay(1500);
                if (popup is null) throw new InvalidOperationException("Preview popup was not created.");
                await UiSnapshot.CaptureAsync(popup, Path.Combine(output, entry.Brand + "-popup.png"));
                string state = popup.DiagnosticState;
                _app.Windows.CloseAll();
                var panel = new TrayPanelWindow();
                _app.Windows.Theme.Register(panel);
                panel.ShowForDiagnostics(new PixelPoint(-24000, -24000), scale);
                await Task.Delay(400);
                await UiSnapshot.CaptureAsync(panel, Path.Combine(output, entry.Brand + "-tray.png"));
                panel.Close();
                results.Add(new { entry.Brand, entry.Name, Profile = _app.Host.Store.Current.Profile?.ModelId, Ok = true, Artwork = _artwork, Home = DescribeHome(), PopupKind = DescribePopup(), Features = features, ModelBefore = modelBefore, ModelAfter = modelAfter, HomeMediaBefore = mediaBefore, HomeMedia = homeMedia, ThemeMedia = themeMedia, Popup = state });
            }
            catch (Exception ex)
            {
                results.Add(new { entry.Brand, Ok = false, Error = ex.ToString() });
                _app.Windows.CloseAll();
            }
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    /// <summary>Named feature sections and switches actually shown (no collapsed ancestor).</summary>
    private static string[] VisibleFeatures(DependencyObject? root) => UiSnapshot.Descendants(root).OfType<FrameworkElement>()
        .Where(e => e.Name.Length > 0 && (e.Name.EndsWith("Card", StringComparison.Ordinal) || e.Name.EndsWith("Section", StringComparison.Ordinal)
            || e.Name.StartsWith("Toggle", StringComparison.Ordinal) || e.Name.StartsWith("Moondrop", StringComparison.Ordinal)))
        .Where(e =>
        {
            for (DependencyObject? node = e; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
                if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
            return true;
        })
        .Select(e => e.Name).Distinct().ToArray();

    private void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _tray.Dispose(); _app.Windows.CloseAll(); _picker.Close();
        _app.Host.Loggers.Dispose(); _app.Host.FileLog.Dispose(); _app.Exit();
    }

    /// <summary>Every command succeeds at once and changes only the demo state, as the earbuds would confirm it.</summary>
    private sealed class DemoCommands(App app) : IDeviceCommandService, IAdvancedDeviceCommands
    {
        private Task<CommandResult> Apply(Func<DeviceState, DeviceState> update)
        {
            app.Host.Store.Reset(update(app.Host.Store.Current));
            return Task.FromResult(CommandResult.Ok);
        }
        private static Observed<T> O<T>(T value) => Observed.From(value, DateTimeOffset.UtcNow, StateSource.QueryResponse);
        private static Task<CommandResult> Ok => Task.FromResult(CommandResult.Ok);
        public Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken = default) => Apply(s => s with { NoiseControl = O(target) });
        public Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken = default) => Apply(s => s with { Equalizer = O(new EqualizerState(presetId, 0)) });
        public Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken = default) => Apply(s => s with { Toggles = s.Toggles.SetItem(toggle, O(enabled)) });
        public Task<CommandResult> RefreshAsync(CancellationToken cancellationToken = default) => Ok;
        public Task<CommandResult> ReconnectAsync(CancellationToken cancellationToken = default) => Ok;

        public Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default) => Apply(s => s with { Spatial = O(mode) });
        public Task<CommandResult> SetGameSoundAsync(int type, CancellationToken cancellationToken = default) =>
            Apply(s => s with { GameSound = O(new GameSoundState(type, s.GameSound?.Value.SupportedTypes ?? [type])) });
        public Task<CommandResult> RefreshGameModeAsync(CancellationToken cancellationToken = default) => Ok;
        public Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default) => Apply(s => s with { PromptVolume = O(level) });
        public Task<CommandResult> SetGainAsync(int level, CancellationToken cancellationToken = default) => Apply(s => s with { Gain = O(level) });
        public Task<CommandResult> SetBassEngineLevelAsync(int level, CancellationToken cancellationToken = default) =>
            Apply(s => s with { BassEngine = O((s.BassEngine?.Value ?? new BassEngineState(-5, 5, 0)) with { Current = level }) });
        public Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default) => Apply(s =>
        {
            var keys = (s.KeyFunctions?.Value ?? []).ToList();
            // A both-earbuds record stays one record, as the earbuds report it.
            int index = keys.FindIndex(k => k.Button == assignment.Button && k.Action == assignment.Action && (k.Side == assignment.Side || k.Side == EarSide.Both));
            if (index >= 0) keys[index] = keys[index] with { Function = assignment.Function };
            else keys.Add(assignment);
            return s with { KeyFunctions = O<IReadOnlyList<KeyFunction>>(keys) };
        });
        public Task<CommandResult> SetNoiseCycleAsync(EarSide side, uint modeMask, CancellationToken cancellationToken = default) => Apply(s =>
        {
            var cycle = s.NoiseCycle?.Value ?? new NoiseCycleState(modeMask, modeMask);
            bool shared = (s.Profile as OppoDeviceProfile)?.Definition.NoiseCycleShared == true;
            return s with
            {
                NoiseCycle = O(shared || side == EarSide.Both ? new NoiseCycleState(modeMask, modeMask)
                    : side == EarSide.Right ? cycle with { RightMask = modeMask } : cycle with { LeftMask = modeMask }),
            };
        });
        public Task<CommandResult> SetHeadMotionTypeAsync(int type, CancellationToken cancellationToken = default) => Apply(s => s with { HeadMotionType = O(type) });
        public Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default) => Apply(s => s with { IsFindingEarbuds = ring });
        public Task<CommandResult> RunFitTestAsync(CancellationToken cancellationToken = default) =>
            Apply(s => s with { FitTest = new FitTestResult(FitQuality.Good, FitQuality.Average), IsFitTestRunning = false });
        public Task<CommandResult> MultiConnectAsync(MultiConnectOperation operation, byte[] address, CancellationToken cancellationToken = default) => Apply(s =>
        {
            var devices = (s.SourceDevices?.Value ?? []).Where(d => operation != MultiConnectOperation.Remove || !d.Address.SequenceEqual(address))
                .Select(d => d.Address.SequenceEqual(address) ? d with { State = operation == MultiConnectOperation.Connect ? 2 : 0 } : d).ToArray();
            return s with { SourceDevices = O<IReadOnlyList<SourceDevice>>(devices) };
        });
        public Task<CommandResult> SetPreferredDeviceAsync(byte[]? address, CancellationToken cancellationToken = default) =>
            Apply(s => s with { PreferredDevice = O(new PreferredDeviceState(address)) });
        public Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) => Apply(s => s with
        {
            CustomEqualizers = O<IReadOnlyList<CustomEqualizer>>((s.CustomEqualizers?.Value ?? []).Select(p => p with { IsSelected = p.Id == id }).ToArray()),
        });
        public Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken cancellationToken = default) => Apply(s => s with
        {
            CustomEqualizers = O<IReadOnlyList<CustomEqualizer>>((s.CustomEqualizers?.Value ?? []).Select(p => p.Id == preset.Id ? preset : p).ToArray()),
        });
        public Task<CommandResult> AddCustomEqualizerAsync(CancellationToken cancellationToken = default) => Apply(s =>
        {
            var presets = s.CustomEqualizers?.Value ?? [];
            if ((s.Profile as OppoDeviceProfile)?.Definition.CustomEqualizer is not { } spec) return s;
            int id = presets.Select(p => p.Id).DefaultIfEmpty(0).Max() + 1;
            return s with { CustomEqualizers = O<IReadOnlyList<CustomEqualizer>>([.. presets, DemoCustomEqualizer(spec, id, false)]) };
        });
        public Task<CommandResult> DeleteCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) => Apply(s => s with
        {
            CustomEqualizers = O<IReadOnlyList<CustomEqualizer>>((s.CustomEqualizers?.Value ?? []).Where(p => p.Id != id).ToArray()),
        });
    }
}
