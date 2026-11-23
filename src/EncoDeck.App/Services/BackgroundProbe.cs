// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using EncoDeck.App.Views;
using Microsoft.UI.Dispatching;

namespace EncoDeck.App.Services;

/// <summary>
/// <c>--background-probe &lt;dir&gt;</c>: measures what EncoDeck costs while it only sits in the tray.
/// It opens every page of the main window and the tray panel <b>off-screen</b> (never activated),
/// closes them as a user would, and records memory, threads and idle CPU at each step into
/// <c>probe.json</c>. Like the other diagnostics it never starts the Bluetooth runtime.
/// </summary>
internal static class BackgroundProbe
{
    public const string Argument = "--background-probe";

    private static readonly string[] Pages = ["Home", "Sound", "Controls", "Connections", "Devices", "Labs", "Settings", "About"];

    public static async Task RunAsync(App app, string outDir, DispatcherQueue dispatcher)
    {
        Directory.CreateDirectory(outDir);
        var steps = new List<object>();
        void Record(string step)
        {
            var m = ProcessMemory.Measure();
            steps.Add(new
            {
                step,
                privateWorkingSetMB = Mb(m.PrivateWorkingSet),
                workingSetMB = Mb(m.WorkingSet),
                privateBytesMB = Mb(m.PrivateBytes),
                managedHeapMB = Mb(m.ManagedHeap),
                gcCommittedMB = Mb(m.GcCommitted),
                m.Threads,
                m.Handles,
            });
        }

        var host = AppHost.Create(Path.Combine(outDir, "logs"));
        app.SetHostForDiagnostics(host);
        host.Store.Reset(UiSnapshot.SampleState(DateTimeOffset.UtcNow));
        app.CreateViewModelsForDiagnostics(dispatcher);
        await Task.Delay(1000);
        Record("started");
        // What the app does after a launch into the tray.
        app.Windows.TrimWhenIdle();
        await Task.Delay(5000);
        Record("started, then idle in the tray");

        var main = new MainWindow();
        double scale = WindowInterop.GetScale(main);
        var appWindow = WindowInterop.GetAppWindow(main);
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(-24000, -24000, (int)(1160 * scale), (int)(760 * scale)));
        appWindow.Show(activateWindow: false);
        foreach (string page in Pages)
        {
            main.Navigate(page);
            await Task.Delay(700);
        }
        main.Navigate("Home");
        await Task.Delay(700);
        Record("main window open");
        main.Close();

        var panel = new TrayPanelWindow();
        panel.ShowForDiagnostics(new Platform.Shell.PixelPoint(-24000, -22000), scale);
        await Task.Delay(800);
        panel.HidePanel();
        // These windows are not the window manager's own, so tell it what their closing would.
        app.Windows.TrimWhenIdle();
        await Task.Delay(5000);
        Record("windows closed, idle in the tray");

        // Idle CPU: nothing should run while only the tray icon is left.
        var before = ProcessMemory.Measure().Cpu;
        await Task.Delay(10000);
        double idleCpuMs = (ProcessMemory.Measure().Cpu - before).TotalMilliseconds;
        Record("15 s later");

        long reopenStart = Environment.TickCount64;
        panel.ShowForDiagnostics(new Platform.Shell.PixelPoint(-24000, -22000), scale);
        long reopenMs = Environment.TickCount64 - reopenStart;
        await Task.Delay(800);
        Record("tray panel reopened");
        panel.HidePanel();
        app.Windows.TrimWhenIdle();
        await Task.Delay(5000);
        Record("panel hidden again, idle");

        panel.Close();
        app.Windows.TrimWhenIdle();
        await Task.Delay(5000);
        Record("panel closed, idle");
        await File.WriteAllTextAsync(
            Path.Combine(outDir, "probe.json"),
            JsonSerializer.Serialize(new { steps, idleCpuMsPer10s = Math.Round(idleCpuMs), panelReopenMs = reopenMs }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);
}
