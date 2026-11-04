// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Configuration;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Platform.Cache;
using EncoDeck.Platform.Input;
using EncoDeck.Platform.Logging;
using EncoDeck.Platform.Settings;
using EncoDeck.Platform.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Platform;

public class PopupPlacementTests
{
    // 1920x1080 monitor, 48 px taskbar at the bottom.
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);
    private static readonly PixelRect WorkBottom = new(0, 0, 1920, 1032);

    [Fact]
    public void InferEdge_FromWorkArea()
    {
        Assert.Equal(TaskbarEdge.Bottom, PopupPlacement.InferEdge(Monitor, WorkBottom));
        Assert.Equal(TaskbarEdge.Top, PopupPlacement.InferEdge(Monitor, new PixelRect(0, 48, 1920, 1032)));
        Assert.Equal(TaskbarEdge.Left, PopupPlacement.InferEdge(Monitor, new PixelRect(60, 0, 1860, 1080)));
        Assert.Equal(TaskbarEdge.Right, PopupPlacement.InferEdge(Monitor, new PixelRect(0, 0, 1860, 1080)));
        Assert.Null(PopupPlacement.InferEdge(Monitor, Monitor)); // auto-hide
    }

    [Fact]
    public void BottomTaskbar_AnchoredAtTrayIcon_StaysInsideWorkArea()
    {
        var rect = PopupPlacement.PlaceNearTaskbar(WorkBottom, TaskbarEdge.Bottom, new PixelPoint(1880, 1056), 540, 450, 18);
        Assert.True(WorkBottom.Contains(rect));
        Assert.Equal(1032 - 18 - 450, rect.Y);
        Assert.Equal(1920 - 18 - 540, rect.X); // clamped at the right edge
    }

    [Theory]
    [InlineData(TaskbarEdge.Top)]
    [InlineData(TaskbarEdge.Left)]
    [InlineData(TaskbarEdge.Right)]
    [InlineData(TaskbarEdge.Bottom)]
    public void AllEdges_StayInside(TaskbarEdge edge)
    {
        var work = new PixelRect(100, 50, 1700, 1000);
        foreach (var anchor in new PixelPoint?[] { null, new(0, 0), new(5000, 5000), new(900, 500) })
        {
            var rect = PopupPlacement.PlaceNearTaskbar(work, edge, anchor, 400, 300, 12);
            Assert.True(work.Contains(rect), $"{edge} {anchor}: {rect}");
        }
    }

    [Fact]
    public void SecondaryMonitorWithNegativeCoordinates()
    {
        var work = new PixelRect(-2560, -200, 2560, 1400);
        var rect = PopupPlacement.PlaceNearTaskbar(work, TaskbarEdge.Bottom, new PixelPoint(-100, 1190), 570, 204, 18);
        Assert.True(work.Contains(rect));
    }

    [Fact]
    public void OversizedPopup_IsShrunkToFit()
    {
        var work = new PixelRect(0, 0, 300, 200);
        var rect = PopupPlacement.PlaceNearTaskbar(work, TaskbarEdge.Bottom, null, 1000, 1000, 10);
        Assert.True(work.Contains(rect));
    }

    // Quick Settings region measured on a 2560x1600 display at 150 %: 576 px wide, cards from y 916 down to the taskbar.
    private static readonly PixelRect WorkArea150 = new(0, 0, 2560, 1528);
    private static readonly PixelRect QuickSettings150 = PixelRect.FromLtrb(1984, 916, 2560, 1528);

    [Fact]
    public void AboveQuickSettings_AlignsWithCardsAndKeepsTheShellGap()
    {
        var rect = PopupPlacement.PlaceAboveQuickSettings(QuickSettings150, WorkArea150, 300, 18);
        Assert.Equal(new PixelRect(2002, 616, 540, 300), rect);
    }

    [Theory]
    [InlineData(320)]
    [InlineData(384)]
    [InlineData(424)]
    [InlineData(496)]
    public void AboveQuickSettings_WidthFollowsTheCards(int regionWidth)
    {
        // The panel is the cards' column, not a fixed width: a wider Quick Settings makes a wider
        // panel, whatever the display, scaling factor or Windows build.
        var cards = new PixelRect(2560 - regionWidth, 916, regionWidth, 612);
        var rect = PopupPlacement.PlaceAboveQuickSettings(cards, WorkArea150, 300, 12);
        Assert.NotNull(rect);
        Assert.Equal(regionWidth - 24, rect!.Value.Width);
        Assert.Equal(cards.X + 12, rect.Value.X);
    }

    [Fact]
    public void AboveQuickSettings_GoesBesideTheCardsWhenTheTopIsTooLow()
    {
        // Tall cards (media card, sub-page) on the right: to their left, bottoms aligned.
        var rect = PopupPlacement.PlaceAboveQuickSettings(PixelRect.FromLtrb(1984, 200, 2560, 1528), WorkArea150, 300, 18);
        Assert.Equal(new PixelRect(1444, 1210, 540, 300), rect);
        // Taskbar on the left: to their right.
        var left = PopupPlacement.PlaceAboveQuickSettings(PixelRect.FromLtrb(0, 200, 576, 1528), WorkArea150, 300, 18);
        Assert.Equal(new PixelRect(576, 1210, 540, 300), left);
    }

    [Fact]
    public void AboveQuickSettings_NullWhenItDoesNotFit()
    {
        Assert.Null(PopupPlacement.PlaceAboveQuickSettings(PixelRect.FromLtrb(1984, 916, 2010, 1528), WorkArea150, 300, 18));
        // Taller than the screen.
        Assert.Null(PopupPlacement.PlaceAboveQuickSettings(PixelRect.FromLtrb(1984, 200, 2560, 1528), WorkArea150, 1600, 18));
        // No room above and none beside.
        Assert.Null(PopupPlacement.PlaceAboveQuickSettings(PixelRect.FromLtrb(0, 200, 2560, 1528), WorkArea150, 300, 18));
    }
}

public class GlobalHotkeysTests
{
    // Nobody's combination; registered only for the duration of the test.
    private static readonly Hotkey Unused = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x87);

    [Fact]
    public void Registers_RaisesPressed_AndReportsTakenCombinations()
    {
        using var hotkeys = new GlobalHotkeys(NullLogger<GlobalHotkeys>.Instance);
        var pressed = new List<int>();
        hotkeys.Pressed += (_, id) => pressed.Add(id);

        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(7, Unused));
        SendMessage(hotkeys.WindowHandle, 0x0312, 7, 0); // WM_HOTKEY, as Windows delivers it
        Assert.Equal([7], pressed);

        // The same combination for a second id is taken (by ourselves, here).
        Assert.Equal(HotkeyRegistration.InUse, hotkeys.Register(8, Unused));
        Assert.Equal(HotkeyRegistration.Invalid, hotkeys.Register(9, new Hotkey(HotkeyModifiers.None, 0x41)));

        hotkeys.UnregisterAll();
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(8, Unused));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}

public class LogRedactorTests
{
    [Theory]
    [InlineData("addr E4:98:E0:3A:2A:A3 seen", "addr **:**:**:**:2A:A3 seen")]
    [InlineData("Bluetooth#Bluetooth8c:f8:c5:11:22:33-e4:98:e0:3a:2a:a3", "Bluetooth#Bluetooth**:**:**:**:22:33-**:**:**:**:2a:a3")]
    [InlineData(@"BTHENUM\DEV_E498E03A2AA3\7&24AD8B5C&0&BLUETOOTHDEVICE_E498E03A2AA3", @"BTHENUM\DEV_********2AA3\7&24AD8B5C&0&BLUETOOTHDEVICE_********2AA3")]
    [InlineData("no address here 12:34", "no address here 12:34")]
    public void RedactsAddresses(string input, string expected)
    {
        Assert.Equal(expected, LogRedactor.Redact(input));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void PersistsAndReloads()
    {
        string dir = Directory.CreateTempSubdirectory("encodeck-test").FullName;
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);
            AppSettings? changed = null;
            store.Changed += (_, s) => changed = s;
            store.Update(s => s with { Theme = AppTheme.Dark, PopupDurationSeconds = 6, Language = "en-US" });
            Assert.NotNull(changed);

            var reloaded = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);
            Assert.Equal(AppTheme.Dark, reloaded.Current.Theme);
            Assert.Equal(6, reloaded.Current.PopupDurationSeconds);
            Assert.Equal("en-US", reloaded.Current.Language);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults_AndKeepsBackup()
    {
        string dir = Directory.CreateTempSubdirectory("encodeck-test").FullName;
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ not json");
            var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);
            Assert.Equal(new AppSettings(), store.Current);
            Assert.True(File.Exists(path + ".bak"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Hotkeys_PersistAndOlderFilesHaveNone()
    {
        string dir = Directory.CreateTempSubdirectory("encodeck-test").FullName;
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{ "schemaVersion": 1, "keepRunningInTray": true }""");
            var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);
            Assert.Null(store.Current.Hotkeys);

            store.Update(s => s with { Hotkeys = new HotkeyBindings { ToggleNoise = "Ctrl+Alt+N", CycleNoise = "F13" } });
            var reloaded = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance).Current.Hotkeys;
            Assert.Equal("Ctrl+Alt+N", reloaded?.ToggleNoise);
            Assert.Equal("F13", reloaded?.CycleNoise);
            Assert.Null(reloaded?.Transparency);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DisconnectedDevices_PersistAndOlderFilesHaveNone()
    {
        string dir = Directory.CreateTempSubdirectory("encodeck-test").FullName;
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{ "schemaVersion": 1, "keepRunningInTray": true }""");
            var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);
            Assert.Null(store.Current.DisconnectedDeviceIds);

            store.Update(s => s with { DisconnectedDeviceIds = ["Bluetooth#Bluetooth00:11:22:33:44:55-66:77:88:99:aa:bb"] });
            var reloaded = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance).Current.DisconnectedDeviceIds;
            Assert.Equal(["Bluetooth#Bluetooth00:11:22:33:44:55-66:77:88:99:aa:bb"], reloaded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void QuickSettingsPanel_OnForOlderFiles_OffStaysOff()
    {
        string dir = Directory.CreateTempSubdirectory("encodeck-test").FullName;
        try
        {
            // Saved by a version without the setting.
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{ "schemaVersion": 1, "keepRunningInTray": true }""");
            var store = new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance);
            Assert.True(store.Current.ShowAboveQuickSettings);
            Assert.True(store.Current.HideQuickSettingsPanelWhenBluetoothOff);

            store.Update(s => s with { ShowAboveQuickSettings = false });
            Assert.False(new JsonSettingsStore(path, NullLogger<JsonSettingsStore>.Instance).Current.ShowAboveQuickSettings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class DeviceStateCacheTests
{
    private static readonly DeviceIdentity X4 = new("dev-x4", "OPPO Enco X4", new BluetoothAddress(0x112233445566));

    [Fact]
    public void ConfirmedReadings_AreRestoredAsCacheOnNextStart()
    {
        string dir = Directory.CreateTempSubdirectory("encodeck-test").FullName;
        try
        {
            string path = Path.Combine(dir, "cache.json");
            var at = DateTimeOffset.Parse("2026-10-20T10:00:00Z");

            var store1 = new DeviceStateStore();
            var cache1 = new DeviceStateCache(path, store1, NullLogger<DeviceStateCache>.Instance);
            cache1.Start();
            store1.Reset(DeviceState.Empty with { Device = X4 });
            store1.TryUpdate(X4.DeviceId, s => s with
            {
                Battery = s.Battery with
                {
                    Case = Observed.From(new BatteryReading(70, false), at, StateSource.QueryResponse),
                    Left = Observed.From(new BatteryReading(55, false), at, StateSource.Optimistic), // never cached
                },
            });
            cache1.Dispose();

            Assert.True(File.Exists(path));
            Assert.DoesNotContain("11:22:33", File.ReadAllText(path));
            Assert.DoesNotContain("dev-x4", File.ReadAllText(path));

            var store2 = new DeviceStateStore();
            var cache2 = new DeviceStateCache(path, store2, NullLogger<DeviceStateCache>.Instance);
            cache2.Start();
            store2.Reset(DeviceState.Empty with { Device = X4 });

            var restored = store2.Current.Battery;
            Assert.Equal(70, restored.Case!.Value.Level);
            Assert.Equal(StateSource.Cache, restored.Case.Source);
            Assert.Equal(at, restored.Case.Timestamp);
            Assert.Null(restored.Left);
            cache2.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SentEqCurve_IsKeptAcrossRestartsButReadCurvesAreNot()
    {
        string dir = Directory.CreateTempSubdirectory("encodeck-test").FullName;
        try
        {
            string path = Path.Combine(dir, "cache.json");
            var at = DateTimeOffset.Parse("2026-11-02T10:00:00Z");
            CustomEqualizer Curve(int gain) => new(1, "", -12, 12, true, [new EqualizerBand(31, gain), new EqualizerBand(63, -gain)]);

            var store1 = new DeviceStateStore();
            var cache1 = new DeviceStateCache(path, store1, NullLogger<DeviceStateCache>.Instance);
            cache1.Start();
            store1.Reset(DeviceState.Empty with { Device = X4 });
            store1.TryUpdate(X4.DeviceId, s => s with
            {
                Battery = s.Battery with { Case = Observed.From(new BatteryReading(70, false), at, StateSource.QueryResponse) },
                CustomEqualizers = Observed.From<IReadOnlyList<CustomEqualizer>>([Curve(5)], at, StateSource.Sent),
            });
            // A curve the device reports is read again next time, never restored.
            store1.TryUpdate(X4.DeviceId, s => s with { CustomEqualizers = Observed.From<IReadOnlyList<CustomEqualizer>>([Curve(2)], at.AddMinutes(1), StateSource.QueryResponse) });
            cache1.Dispose();

            var store2 = new DeviceStateStore();
            var cache2 = new DeviceStateCache(path, store2, NullLogger<DeviceStateCache>.Instance);
            cache2.Start();
            store2.Reset(DeviceState.Empty with { Device = X4 });
            var restored = store2.Current.CustomEqualizers!;
            Assert.Equal(StateSource.Cache, restored.Source);
            Assert.Equal(at, restored.Timestamp);
            var curve = restored.Value[0];
            Assert.Equal((1, "", -12, 12, true), (curve.Id, curve.Name, curve.MinGain, curve.MaxGain, curve.IsSelected));
            Assert.Equal([5, -5], restored.Value[0].Bands.Select(b => b.Gain));
            Assert.Equal([31, 63], restored.Value[0].Bands.Select(b => b.FrequencyHz));
            cache2.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reconnecting_TheSameDevice_RestoresLastReadings()
    {
        string dir = Directory.CreateTempSubdirectory("encodeck-test").FullName;
        try
        {
            var at = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
            var store = new DeviceStateStore();
            var cache = new DeviceStateCache(Path.Combine(dir, "cache.json"), store, NullLogger<DeviceStateCache>.Instance);
            cache.Start();
            store.Reset(DeviceState.Empty with { Device = X4 });
            store.TryUpdate(X4.DeviceId, s => s with
            {
                Battery = new BatteryStatus(
                    Observed.From(new BatteryReading(80, false), at, StateSource.QueryResponse),
                    Observed.From(new BatteryReading(90, false), at, StateSource.QueryResponse),
                    Observed.From(new BatteryReading(60, false), at, StateSource.QueryResponse)),
                ProductId = 0x068C10,
            });

            // The device manager resets the state when the same earbuds reconnect.
            store.Reset(DeviceState.Empty with { Device = X4 });
            Assert.Equal(90, store.Current.Battery.Right!.Value.Level);
            Assert.Equal(60, store.Current.Battery.Case!.Value.Level);
            Assert.Equal(0x068C10u, store.Current.ProductId);
            cache.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ModelFacts_AreRememberedAcrossRestarts()
    {
        string dir = Directory.CreateTempSubdirectory("encodeck-test").FullName;
        try
        {
            string path = Path.Combine(dir, "cache.json");
            var store1 = new DeviceStateStore();
            var cache1 = new DeviceStateCache(path, store1, NullLogger<DeviceStateCache>.Instance);
            cache1.Start();
            store1.Reset(DeviceState.Empty with { Device = X4 });
            store1.TryUpdate(X4.DeviceId, s => s with { ProductId = 0x06EC10, ColorId = 1, ControlCapabilities = [0xFF, 0x77, 0x5A], BudFirmware = 116 });
            cache1.Dispose();

            var store2 = new DeviceStateStore();
            var cache2 = new DeviceStateCache(path, store2, NullLogger<DeviceStateCache>.Instance);
            cache2.Start();
            store2.Reset(DeviceState.Empty with { Device = X4 });
            var restored = store2.Current;
            Assert.Equal(0x06EC10u, restored.ProductId);
            Assert.Equal(1, restored.ColorId);
            Assert.Equal([0xFF, 0x77, 0x5A], restored.ControlCapabilities);
            Assert.Equal(116, restored.BudFirmware);
            cache2.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MultiConnectionList_IsRestoredWithoutAddresses()
    {
        string dir = Directory.CreateTempSubdirectory("encodeck-test").FullName;
        try
        {
            string path = Path.Combine(dir, "cache.json");
            var at = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
            var store1 = new DeviceStateStore();
            var cache1 = new DeviceStateCache(path, store1, NullLogger<DeviceStateCache>.Instance);
            cache1.Start();
            store1.Reset(DeviceState.Empty with { Device = X4 });
            store1.TryUpdate(X4.DeviceId, s => s with
            {
                SourceDevices = Observed.From<IReadOnlyList<SourceDevice>>(
                [
                    new([0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6], "Desk PC", 2, true, true, 2),
                    new([0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F], "Phone", 1, false, false, 1),
                ], at, StateSource.QueryResponse),
            });
            cache1.Dispose();
            Assert.DoesNotContain("A1", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

            var store2 = new DeviceStateStore();
            var cache2 = new DeviceStateCache(path, store2, NullLogger<DeviceStateCache>.Instance);
            cache2.Start();
            store2.Reset(DeviceState.Empty with { Device = X4 });
            var restored = store2.Current.SourceDevices!;
            Assert.Equal(StateSource.Cache, restored.Source);
            Assert.Equal(["Desk PC", "Phone"], restored.Value.Select(d => d.Name));
            Assert.All(restored.Value, d => Assert.Empty(d.Address));
            Assert.True(restored.Value[0].IsThisDevice && restored.Value[0].IsConnected);
            // Connecting is momentary: remembered as not connected.
            Assert.Equal(0, restored.Value[1].State);

            // The earbuds' own answer replaces it.
            store2.TryUpdate(X4.DeviceId, s => s with
            {
                SourceDevices = Observed.From<IReadOnlyList<SourceDevice>>([new([0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6], "Desk PC", 2, true, true, 2)], at, StateSource.QueryResponse),
            });
            Assert.Single(store2.Current.SourceDevices!.Value);
            cache2.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
