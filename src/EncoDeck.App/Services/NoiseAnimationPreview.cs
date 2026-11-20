// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;
using EncoDeck.App.Controls;
using EncoDeck.App.Views;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.EarFun;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EncoDeck.App.Services;

/// <summary>
/// <c>--noise-animation &lt;dir&gt;</c>: records the home page's noise card <b>off-screen</b> (never on a
/// monitor, never activated) while clicks on its modes and rows are answered by simulated EarFun Air
/// Pro 4+ earbuds about 0.7 s later, one of them refused. Writes raw BGRA frames plus
/// <c>frames.json</c> for assembling an animation; never starts the Bluetooth runtime.
/// </summary>
internal static class NoiseAnimationPreview
{
    public const string Argument = "--noise-animation";
    private static readonly TimeSpan Answer = TimeSpan.FromMilliseconds(700);

    public static async Task RunAsync(App app, string outDir, DispatcherQueue dispatcher)
    {
        Directory.CreateDirectory(outDir);
        var host = AppHost.Create(Path.Combine(outDir, "logs"), Path.Combine(outDir, "state"));
        app.SetHostForDiagnostics(host);
        var profile = (EarFunProfile)BuiltInProfiles.Create().Single(p => p.ModelId == "earfun-tw700");
        var device = new DeviceIdentity("preview", profile.DisplayName, new BluetoothAddress(0));
        var sample = BrandDemo.CreateState(DemoDiscovery.AfterSession(profile, profile.DisplayName), device, DateTimeOffset.UtcNow);
        var choices = EarFunSettings.Option(profile.Model, EarFunSetting.Sub, 0)!.Choices;
        byte Sub(string key) => (byte)choices.Single(c => c.LabelKey == "EarFun_" + key).Value;
        string Id(string key) => Sub(key).ToString(System.Globalization.CultureInfo.InvariantCulture);
        DeviceState With(NoiseControlMode mode, EarFunSetting setting, byte value)
        {
            var now = DateTimeOffset.UtcNow;
            var option = EarFunSettings.Option(profile.Model, setting, value)!;
            var current = host.Store.Current;
            return current with
            {
                NoiseControl = Observed.From(new NoiseControlState(mode), now, StateSource.QueryResponse),
                Options = current.Options.SetItem(option.Id, Observed.From(option, now, StateSource.QueryResponse)),
            };
        }
        host.Store.Reset(sample);
        byte ambient = profile.Model.Gaia ? (byte)1 : (byte)2;
        // Both sub-mode settings known, as after a session's first reads.
        host.Store.Reset(With(NoiseControlMode.Transparency, EarFunSetting.Ambient, ambient));
        host.Store.Reset(With(NoiseControlMode.NoiseCancellation, EarFunSetting.Sub, Sub("Level4")));
        app.CreateViewModelsForDiagnostics(dispatcher);
        await Task.Delay(300);

        var main = new MainWindow();
        double scale = WindowInterop.GetScale(main);
        var appWindow = WindowInterop.GetAppWindow(main);
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(-24000, -24000, (int)(1160 * scale), (int)(760 * scale)));
        appWindow.Show(activateWindow: false);
        main.Navigate("Home");
        await Task.Delay(1200);

        var card = UiSnapshot.Descendants(main.Content).OfType<NoiseModeCircles>().First();
        var at = card.TransformToVisual(null).TransformPoint(default);
        const double margin = 14;
        int cropX = (int)((at.X - margin) * scale), cropY = (int)((at.Y - margin - 44) * scale);
        int cropW = (int)((card.ActualWidth + 2 * margin) * scale), cropH = (int)((card.ActualHeight + 2 * margin + 44 + 90) * scale); // and the top of the card below

        var frames = new List<object>();
        var clock = Stopwatch.StartNew();
        bool recording = true;
        async Task RecordAsync()
        {
            while (recording)
            {
                if (WindowInterop.CaptureWindow(main) is { } shot)
                {
                    int w = Math.Min(cropW, shot.Width - cropX), h = Math.Min(cropH, shot.Height - cropY);
                    var crop = new byte[w * h * 4];
                    for (int y = 0; y < h; y++) Buffer.BlockCopy(shot.Bgra, ((cropY + y) * shot.Width + cropX) * 4, crop, y * w * 4, w * 4);
                    string name = $"frame{frames.Count:0000}.bgra";
                    await File.WriteAllBytesAsync(Path.Combine(outDir, name), crop);
                    frames.Add(new { file = name, width = w, height = h, ms = clock.Elapsed.TotalMilliseconds });
                }
                await Task.Delay(15);
            }
        }
        // Simulated earbuds: answer after a moment with the new state, or keep the old one (a refusal).
        Func<string, Task> Earbuds(Func<DeviceState>? confirmed) => async _ =>
        {
            await Task.Delay(Answer);
            if (confirmed is not null) host.Store.Reset(confirmed());
        };

        var recorder = RecordAsync();
        await Task.Delay(700);
        await card.ManualLevels.ChooseAsync(Id("Level2"), Earbuds(() => With(NoiseControlMode.NoiseCancellation, EarFunSetting.Sub, Sub("Level2"))));
        await Task.Delay(800);
        await card.SubModes.ChooseAsync(Id("Adaptive"), Earbuds(() => With(NoiseControlMode.NoiseCancellation, EarFunSetting.Sub, Sub("Adaptive"))));
        await Task.Delay(800);
        await card.SubModes.ChooseAsync(EarFunSettings.ManualSubMode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Earbuds(() => With(NoiseControlMode.NoiseCancellation, EarFunSetting.Sub, Sub("Level2"))));
        await Task.Delay(800);
        await card.SubModes.ChooseAsync(Id("Wind"), Earbuds(null)); // refused: slides back
        await Task.Delay(800);
        // Modes with and without rows: the rows follow the click, and the card resizes smoothly.
        DeviceState Off() => host.Store.Current with { NoiseControl = Observed.From(new NoiseControlState(NoiseControlMode.Off), DateTimeOffset.UtcNow, StateSource.QueryResponse) };
        await card.PreviewChooseAsync(NoiseControlMode.Off, () => Earbuds(Off)(""));
        await Task.Delay(800);
        await card.PreviewChooseAsync(NoiseControlMode.Transparency, () => Earbuds(() => With(NoiseControlMode.Transparency, EarFunSetting.Ambient, ambient))(""));
        await Task.Delay(800);
        await card.PreviewChooseAsync(NoiseControlMode.NoiseCancellation, () => Earbuds(() => With(NoiseControlMode.NoiseCancellation, EarFunSetting.Sub, Sub("Level2")))(""));
        await Task.Delay(800);
        await card.PreviewChooseAsync(NoiseControlMode.Off, () => Earbuds(null)("")); // refused: the rows come back
        await Task.Delay(1000);
        recording = false;
        await recorder;
        main.Close();
        await File.WriteAllTextAsync(Path.Combine(outDir, "frames.json"), JsonSerializer.Serialize(frames));
    }
}
