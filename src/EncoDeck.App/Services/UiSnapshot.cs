// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Runtime.InteropServices.WindowsRuntime;
using EncoDeck.App.Views;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Protocol.Brands;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Protocol.Brands.Moondrop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace EncoDeck.App.Services;

/// <summary>
/// <c>--ui-snapshot &lt;dir&gt;</c>: renders every page of the main window <b>off-screen</b> (never on
/// a monitor, never activated) with a sample Enco X4 state and captures only that window, scrolling
/// through long pages. Like <see cref="PopupSelfTest"/> it runs before the single-instance check and
/// never starts the Bluetooth runtime.
/// </summary>
internal static class UiSnapshot
{
    public const string Argument = "--ui-snapshot";

    private static readonly string[] Pages = ["Home", "Sound", "Controls", "Devices", "Settings"];

    public static async Task<int> RunAsync(App app, string outDir, DispatcherQueue dispatcher, string? modelFile = null, string? artworkDirectory = null, string? profileId = null)
    {
        Directory.CreateDirectory(outDir);
        var host = AppHost.Create(Path.Combine(outDir, "logs"), Path.Combine(outDir, "state"));
        host.Settings.Update(s => s with { EnableExperimentalFeatures = false });
        app.SetHostForDiagnostics(host);
        var sample = SampleState(DateTimeOffset.UtcNow);
        if (profileId is not null)
        {
            var profile = BuiltInProfiles.Create().Single(p => p.ModelId == profileId);
            // Same synthetic session as the brand demo; not hardware-support evidence.
            var device = new DeviceIdentity("snapshot", profile.DisplayName, new BluetoothAddress(0));
            sample = BrandDemo.CreateState(DemoDiscovery.AfterSession(profile, profile.DisplayName), device, DateTimeOffset.UtcNow);
            if (sample.Profile is MoondropProfile moondrop)
            {
                var features = new HashSet<int> { 0, 8, 13, 14, 15, 16, 17, 18, 19, 20, 22, 27, 30 };
                profile = moondrop.WithFeatures(features).WithCodec(DeviceToggle.HiRes).WithCodec(DeviceToggle.Ldac).WithCodec(DeviceToggle.Lc3);
                sample = BrandDemo.CreateState(profile, device, DateTimeOffset.UtcNow) with { SourceDevices = null, PreferredDevice = null };
            }
            if (sample.Profile is MoondropProfile && sample.KeyFunctions is not null)
                sample = sample with
                {
                    KeyFunctions = Observed.From<IReadOnlyList<KeyFunction>>(
                        (from side in new[] { EarSide.Left, EarSide.Right }
                         from action in new[] { GestureAction.SingleSqueeze, GestureAction.DoubleSqueeze, GestureAction.TripleSqueeze, GestureAction.SqueezeAndHold, GestureAction.LongHold }
                         select new KeyFunction(side, 0, action, action switch
                         {
                             GestureAction.SingleSqueeze => 1,
                             GestureAction.DoubleSqueeze => side == EarSide.Left ? 2 : 3,
                             GestureAction.TripleSqueeze => 6,
                             GestureAction.SqueezeAndHold => 7,
                             _ => side == EarSide.Left ? 0 : 10, // Unknown native nibble must survive display unchanged.
                         })).ToArray(), DateTimeOffset.UtcNow, StateSource.QueryResponse),
                };
        }
        host.Store.Reset(sample);
        app.CreateViewModelsForDiagnostics(dispatcher);
        await Task.Delay(300);
        // Display only: a test turntable can be given with --model <file>.
        if (modelFile is not null && File.Exists(modelFile)) app.Device.DeviceModelPath = modelFile;
        ApplyArtworkDirectory(app, artworkDirectory);

        var main = new MainWindow();
        double scale = WindowInterop.GetScale(main);
        var appWindow = WindowInterop.GetAppWindow(main);
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(-24000, -24000, (int)(1160 * scale), (int)(760 * scale)));
        appWindow.Show(activateWindow: false);

        var errors = new List<string>();
        foreach (string page in Pages)
        {
            try
            {
                main.Navigate(page);
                await Task.Delay(900);
                foreach (var view in Descendants(main.Content).OfType<Controls.DeviceModelView>()) errors.Add($"model {page}: {view.DiagnosticState}");
                foreach (var sw in Descendants(main.Content).OfType<ToggleSwitch>())
                {
                    errors.Add($"layout {page}: ToggleSwitch ActualWidth={sw.ActualWidth:0.#} MinWidth={sw.MinWidth:0.#} Width={sw.Width:0.#} Margin={sw.Margin} Align={sw.HorizontalAlignment} Parent={VisualTreeHelper.GetParent(sw)?.GetType().Name}");
                }
                var scroller = FindScrollViewer(main.Content);
                int shot = 0;
                double offset = 0;
                while (true)
                {
                    await CaptureAsync(main, Path.Combine(outDir, $"{page}-{shot++}.png"));
                    if (scroller is null || offset >= scroller.ScrollableHeight || shot >= 6) break;
                    offset = Math.Min(scroller.ScrollableHeight, offset + scroller.ViewportHeight * 0.85);
                    scroller.ChangeView(null, offset, null, disableAnimation: true);
                    await Task.Delay(250);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{page}: {ex}");
            }
        }
        if (sample.Profile is MoondropProfile snapshotProfile)
        {
            var diagnostics = new List<string> { "Synthetic states; no Bluetooth runtime is started. Visibility is measured through the live visual tree, including collapsed ancestors." };
            async Task CaptureScenarioAsync(string name, DeviceState state, bool experimental)
            {
                host.Settings.Update(s => s with { EnableExperimentalFeatures = experimental });
                host.Store.Reset(state);
                await Task.Delay(350);
                foreach (string page in new[] { "Controls", "Sound" })
                {
                    main.Navigate(page);
                    await Task.Delay(500);
                    diagnostics.Add($"scenario={name} page={page} experimental={experimental} connection={state.Connection} gain={state.Gain?.Value} error={app.Device.HasError} bassCapability={app.Features.Has(DeviceCapability.BassEngine)} bassSwitch={app.Features.HasSwitch(DeviceToggle.BassEngine)} bassState={state.BassEngine is not null} spatialCapability={app.Features.Has(DeviceCapability.SpatialAudio)} headCapability={app.Features.Has(DeviceCapability.HeadTracking)} spatialState={state.Spatial is not null}");
                    foreach (var element in Descendants(main.Content).OfType<FrameworkElement>().Where(e => e.Name is "GainSection" or "GainBox" or "GestureCard"
                        || e.Name.StartsWith("Moondrop", StringComparison.Ordinal) || e.Name is "ToggleIndicatorLight" or "TogglePromptTone"
                            or "ToggleInEarDetection" or "ToggleChannelSwap" or "ToggleLdac" or "ToggleLc3" or "ToggleHiRes"
                            or "BassCard" or "BassSwitch" or "BassLevelRow" or "SpatialCard" or "SpatialHost"))
                    {
                        bool visible = true;
                        for (DependencyObject? ancestor = element; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                            if (ancestor is UIElement { Visibility: Visibility.Collapsed }) visible = false;
                        string value = element switch
                        {
                            ComboBox box => $"selected={box.SelectedIndex} text={(box.SelectedItem as ComboBoxItem)?.Content ?? box.SelectedItem}",
                            ToggleSwitch toggle => $"on={toggle.IsOn}",
                            _ => string.Empty,
                        };
                        diagnostics.Add($"{element.Name}: visible={visible} enabled={(element as Control)?.IsEnabled} {value}");
                        if (element is ComboBox { Name: var boxName } actionBox && boxName.StartsWith("Moondrop", StringComparison.Ordinal))
                            diagnostics.Add($"{boxName}: inferred4Enabled={((ComboBoxItem)actionBox.Items[4]).IsEnabled} inferred5Enabled={((ComboBoxItem)actionBox.Items[5]).IsEnabled} unknown10Enabled={((ComboBoxItem)actionBox.Items[10]).IsEnabled}");
                    }
                    var scroller = FindScrollViewer(main.Content);
                    int shot = 0;
                    double offset = 0;
                    while (true)
                    {
                        await CaptureAsync(main, Path.Combine(outDir, $"{name}-{page}-{shot++}.png"));
                        if (scroller is null || offset >= scroller.ScrollableHeight || shot >= 6) break;
                        offset = Math.Min(scroller.ScrollableHeight, offset + scroller.ViewportHeight * 0.85);
                        scroller.ChangeView(null, offset, null, disableAnimation: true);
                        await Task.Delay(100);
                    }
                }
            }
            await CaptureScenarioAsync("Moondrop-ExperimentalOff", sample, false);
            await CaptureScenarioAsync("Moondrop-ExperimentalOn", sample, true);
            var pending = new[] { "gain", "key-function", "toggle:IndicatorLight", "toggle:PromptTone", "prompt-tone",
                "toggle:InEarDetection", "toggle:BassEngine", "toggle:ChannelSwap", "spatial" }
                .Select(description => new PendingCommand(Guid.NewGuid(), description, DateTimeOffset.UtcNow)).ToImmutableList();
            await CaptureScenarioAsync("Moondrop-Pending", sample with { PendingCommands = pending }, true);
            await CaptureScenarioAsync("Moondrop-Disconnected", sample with { Connection = DeviceConnectionState.Disconnected }, true);
            await CaptureScenarioAsync("Moondrop-UnknownState", sample with
            {
                Gain = null, KeyFunctions = null, PromptVolume = null, BassEngine = null, Spatial = null,
                Toggles = ImmutableDictionary<DeviceToggle, Observed<bool>>.Empty,
            }, true);
            // Keep stale reported values: absent feature discovery must still hide the controls.
            await CaptureScenarioAsync("Moondrop-ExcludedFeatures", sample with { Profile = snapshotProfile.WithFeatures(new HashSet<int> { 0, 1, 5, 13 }) }, true);
            await CaptureScenarioAsync("Moondrop-Restored", sample, false);
            main.Navigate("Controls");
            await Task.Delay(500);
            if (Descendants(main.Content).OfType<ComboBox>().FirstOrDefault(box => box.Name == "GainBox") is { IsEnabled: true } gainBox)
            {
                // Exercise the actual selection handler against the unattached manager, not a fake successful command.
                gainBox.SelectedIndex = 2;
                await Task.Delay(350);
                diagnostics.Add($"selection-handler gain requested=2 rendered={gainBox.SelectedIndex} reported={host.Store.Current.Gain?.Value} error={app.Device.HasError} message={app.Device.ErrorMessage}");
                await CaptureAsync(main, Path.Combine(outDir, "Moondrop-RejectedGain-Controls-0.png"));
                app.Device.ErrorMessage = string.Empty;
                app.Device.HasError = false;
            }
            await File.WriteAllLinesAsync(Path.Combine(outDir, "moondrop-controls.txt"), diagnostics);
            host.Settings.Update(s => s with { EnableExperimentalFeatures = false });
            host.Store.Reset(sample);
        }
        if (sample.Profile is EncoDeck.Protocol.Brands.Sennheiser.SennheiserProfile { IsTw4: true })
        {
            var diagnostics = new List<string> { "Synthetic TW4 states, no Bluetooth runtime. Selection handlers use the unattached manager and must roll back rejected requests." };
            async Task CaptureTw4Async(string name, DeviceState state)
            {
                host.Store.Reset(state);
                main.Navigate("Controls");
                await Task.Delay(400);
                foreach (var box in Descendants(main.Content).OfType<ComboBox>().Where(b => b.Name.StartsWith("Sennheiser", StringComparison.Ordinal)))
                {
                    bool visible = true;
                    for (DependencyObject? ancestor = box; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                        if (ancestor is UIElement { Visibility: Visibility.Collapsed }) visible = false;
                    diagnostics.Add($"{name} {box.Name}: visible={visible} enabled={box.IsEnabled} selected={(box.SelectedItem as ComboBoxItem)?.Tag} text={(box.SelectedItem as ComboBoxItem)?.Content}");
                }
                await CaptureAsync(main, Path.Combine(outDir, $"{name}-Controls.png"));
            }
            await CaptureTw4Async("TW4-UnknownAction", sample with
            {
                KeyFunctions = Observed.From<IReadOnlyList<KeyFunction>>(
                    sample.KeyFunctions!.Value.Select(k => k.Side == EarSide.Left && k.Action == GestureAction.TripleSqueeze ? k with { Function = 222 } : k).ToArray(),
                    DateTimeOffset.UtcNow, StateSource.QueryResponse),
            });
            await CaptureTw4Async("TW4-Disconnected", sample with { Connection = DeviceConnectionState.Disconnected });
            await CaptureTw4Async("TW4-UnknownState", sample with { KeyFunctions = null });
            await CaptureTw4Async("TW4-Restored", sample);
            if (Descendants(main.Content).OfType<ComboBox>().FirstOrDefault(b => b.Name == "SennheiserLeftSingleSqueeze") is { IsEnabled: true } gesture)
            {
                gesture.SelectedIndex = 0;
                await Task.Delay(350);
                diagnostics.Add($"Rejected gesture requested=0 rendered={(gesture.SelectedItem as ComboBoxItem)?.Tag} reported={host.Store.Current.KeyFunctions?.Value.First(k => k.Side == EarSide.Left && k.Action == GestureAction.SingleSqueeze).Function} error={app.Device.HasError}");
                await CaptureAsync(main, Path.Combine(outDir, "TW4-RejectedGesture-Controls.png"));
            }
            await File.WriteAllLinesAsync(Path.Combine(outDir, "tw4-controls.txt"), diagnostics);
            host.Store.Reset(sample);
        }
        main.Close();

        try
        {
            // Wide window with the navigation pane collapsed (the layout users get from the
            // hamburger button), still off-screen and not activated.
            var wide = new MainWindow();
            WindowInterop.GetAppWindow(wide).MoveAndResize(new Windows.Graphics.RectInt32(-24000, -24000, (int)(1148 * scale), (int)(754 * scale)));
            WindowInterop.GetAppWindow(wide).Show(activateWindow: false);
            await Task.Delay(600);
            wide.SetPaneOpenForDiagnostics(false);
            foreach (string page in new[] { "Home", "Sound" })
            {
                wide.Navigate(page);
                await Task.Delay(900);
                await CaptureAsync(wide, Path.Combine(outDir, $"Collapsed-{page}-0.png"));
                for (DependencyObject? e = FindScrollViewer(wide.Content); e is not null; e = VisualTreeHelper.GetParent(e))
                {
                    if (e is FrameworkElement fe) errors.Add($"collapsed {page}: {fe.GetType().Name} {fe.Name} width={fe.ActualWidth:0.#} desired={fe.DesiredSize.Width:0.#} offset={fe.ActualOffset.X:0.#}");
                }
                if (FindScrollViewer(wide.Content) is { } sv) errors.Add($"collapsed {page}: viewport={sv.ViewportWidth:0.#} extent={sv.ExtentWidth:0.#} content={(sv.Content as FrameworkElement)?.ActualWidth:0.#}");
            }
            wide.Close();
        }
        catch (Exception ex)
        {
            errors.Add($"Collapsed: {ex}");
        }

        try
        {
            // Tray quick panel, also off-screen and not activated.
            var panel = new TrayPanelWindow();
            new ThemeService(host.Settings).Register(panel);
            panel.ShowForDiagnostics(new Platform.Shell.PixelPoint(-24000, -22000), scale);
            await Task.Delay(900);
            await CaptureAsync(panel, Path.Combine(outDir, "TrayPanel-0.png"));
            panel.Close();
        }
        catch (Exception ex)
        {
            errors.Add($"TrayPanel: {ex}");
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "errors.txt"), string.Join("\n\n", errors));
        return errors.Count == 0 ? 0 : 1;
    }

    internal static void ApplyArtworkDirectory(App app, string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return;
        if (File.Exists(Path.Combine(directory, "name.txt"))) app.Device.DeviceName = File.ReadAllText(Path.Combine(directory, "name.txt")).Trim();
        string? Find(string role) => Directory.EnumerateFiles(directory, role + ".*").FirstOrDefault(p => Path.GetExtension(p) is ".png" or ".webp" or ".jpg");
        app.Device.DeviceImagePath = Find("device");
        app.Device.LeftArtworkPath = Find("left");
        app.Device.RightArtworkPath = Find("right");
        app.Device.CaseArtworkPath = Find("case");
        app.Device.HasComponentArtwork = app.Device.DeviceImagePath is null &&
            (app.Device.LeftArtworkPath is not null || app.Device.RightArtworkPath is not null || app.Device.CaseArtworkPath is not null);
        app.Device.DeviceModelPath = null;
        app.Device.HasDeviceImage = app.Device.DeviceImagePath is not null;
    }

    /// <summary>Values like the ones read from a real X4 (display only, never shown to the user).</summary>
    internal static DeviceState SampleState(DateTimeOffset now)
    {
        Observed<T> O<T>(T value) => Observed.From(value, now, StateSource.QueryResponse);
        Observed<BatteryReading> Level(int level, bool charging = false) => O(new BatteryReading(level, charging));
        var toggles = new Dictionary<DeviceToggle, Observed<bool>>
        {
            [DeviceToggle.GamingMode] = O(false),
            [DeviceToggle.LowLatency] = O(true),
            [DeviceToggle.InEarDetection] = O(true),
            [DeviceToggle.DualDeviceConnection] = O(true),
            [DeviceToggle.SleepDetection] = O(true),
            [DeviceToggle.SwiftPair] = O(true),
            [DeviceToggle.VoiceCommands] = O(false),
            [DeviceToggle.HeadGestures] = O(true),
            [DeviceToggle.BassEngine] = O(true),
            [DeviceToggle.HiRes] = O(false),
        };
        var keys = new List<KeyFunction>();
        foreach (var side in new[] { EarSide.Left, EarSide.Right })
        {
            keys.Add(new KeyFunction(side, 1, GestureAction.SingleSqueeze, 1));
            keys.Add(new KeyFunction(side, 1, GestureAction.DoubleSqueeze, 6));
            keys.Add(new KeyFunction(side, 1, GestureAction.TripleSqueeze, 5));
            keys.Add(new KeyFunction(side, 1, GestureAction.SqueezeAndHold, 8));
            keys.Add(new KeyFunction(side, 1, GestureAction.Slide, 7));
            keys.Add(new KeyFunction(side, KeyFunction.CallButton, GestureAction.SingleSqueeze, 29));
            keys.Add(new KeyFunction(side, KeyFunction.CallButton, GestureAction.DoubleSqueeze, 28));
        }
        int[] bands = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        return DeviceState.Empty with
        {
            Device = new DeviceIdentity("snapshot", "OPPO Enco X4", new BluetoothAddress(0)),
            Profile = new OppoDeviceProfile(OppoModels.EncoX4),
            Connection = DeviceConnectionState.ControlReady,
            ProductId = 0x06EC10,
            ColorId = 1,
            Battery = new BatteryStatus(Level(70), Level(70), Level(100, charging: true)),
            Wear = O(new WearStatus(true, true, false, false, false)),
            NoiseControl = O(new NoiseControlState(NoiseControlMode.NoiseCancellation, "smart")),
            SmartLevelId = "medium",
            Equalizer = O(new EqualizerState("dynaudio-original", 0)),
            Firmware = O(new FirmwareInfo("116", "06EC10", new Dictionary<string, string> { ["left"] = "116", ["right"] = "116", ["case"] = "100" })),
            Toggles = toggles.ToImmutableDictionary(),
            Spatial = O(SpatialAudioMode.Off),
            GameSound = O(new GameSoundState(3, new[] { 3, 0 })),
            PromptVolume = O(6),
            BassEngine = O(new BassEngineState(-5, 5, 0)),
            KeyFunctions = O<IReadOnlyList<KeyFunction>>(keys),
            NoiseCycle = O(new NoiseCycleState(0x0006, 0x0006)),
            HeadMotionType = O(0),
            SourceDevices = O<IReadOnlyList<SourceDevice>>(new SourceDevice[]
            {
                new([0x11, 0x22, 0x33, 0x44, 0x55, 0x66], "Laptop", 2, true, true, 2),
                new([0x01, 0x02, 0x03, 0x04, 0x05, 0x06], "Phone with a long name", 2, false, false, 1),
                new([0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F], "Watch", 0, false, false, 4),
            }),
            PreferredDevice = O(new PreferredDeviceState(null)),
            CustomEqualizers = O<IReadOnlyList<CustomEqualizer>>(new CustomEqualizer[]
            {
                new(4, "自定义1", -6, 6, false, bands.Select(f => new EqualizerBand(f, 0)).ToArray()),
            }),
        };
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root is null) yield break;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            yield return node;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
    }

    internal static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        // The page's own ScrollViewer: the largest one inside the frame content.
        ScrollViewer? best = null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is ScrollViewer sv && sv.ScrollableHeight > (best?.ScrollableHeight ?? -1) && sv.ActualHeight > 200) best = sv;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return best;
    }

    internal static async Task CaptureAsync(Window window, string path)
    {
        if (WindowInterop.CaptureWindow(window) is not { } shot) return;
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, shot.Width, shot.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(shot.Bgra.AsBuffer());
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
    }
}
