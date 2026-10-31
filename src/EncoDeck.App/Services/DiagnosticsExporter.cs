// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Text.Json;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.State;
using EncoDeck.Platform.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// Builds a zip a user can attach to a bug report: redacted logs, settings and a state summary.
/// Bluetooth addresses are redacted (log lines are redacted when written and again here).
/// </summary>
internal static class DiagnosticsExporter
{
    public static async Task<string> ExportAsync(AppHost host)
    {
        Directory.CreateDirectory(host.Paths.DiagnosticsDirectory);
        string path = Path.Combine(host.Paths.DiagnosticsDirectory, $"EncoDeck-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        var state = host.Store.Current;
        var summary = new
        {
            app = typeof(DiagnosticsExporter).Assembly.GetName().Version?.ToString(),
            os = Environment.OSVersion.VersionString,
            arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            createdUtc = DateTimeOffset.UtcNow,
            device = state.Device is { } d ? new { name = d.Name, address = d.Address.ToRedactedString() } : null,
            model = state.Profile?.ModelId,
            connection = state.Connection.ToString(),
            leAudio = new { state.IsLeAudioBonded, links = state.LeAudioLinks, audioStreamingVerified = false },
            firmware = state.Firmware?.Value.Extra,
            codec = new
            {
                windowsLogged = state.WindowsCodecLogged,
                windows = state.WindowsCodec,
                displayKind = CodecDisplay.Resolve(state).Kind.ToString(),
                displayCodec = CodecDisplay.Resolve(state).Codec,
            },
            battery = new
            {
                left = state.Battery.Left is { } l ? new { l.Value.Level, l.Value.IsCharging, source = l.Source.ToString(), l.Timestamp } : null,
                right = state.Battery.Right is { } r ? new { r.Value.Level, r.Value.IsCharging, source = r.Source.ToString(), r.Timestamp } : null,
                @case = state.Battery.Case is { } c ? new { c.Value.Level, c.Value.IsCharging, source = c.Source.ToString(), c.Timestamp } : null,
            },
            noise = state.NoiseControl is { } n ? new { mode = n.Value.Mode.ToString(), n.Value.LevelId, source = n.Source.ToString() } : null,
            equalizer = state.Equalizer?.Value.RawPresetValue,
            lastError = state.LastError?.Message,
            capabilities = state.Profile?.Capabilities.All.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Support.ToString()),
            settings = host.Settings.Current,
            bridge = new
            {
                link = host.Bridge.Status.Link.ToString(),
                server = host.Bridge.Status.Server,
                rfcomm = host.Bridge.Status.Rfcomm,
                origin = host.Bridge.Installation?.Origin.ToString(),
                stream = state.Bridge,
                controlViaBridge = state.LinkViaBridge,
                component = host.Bridge.Managed?.Version,
            },
        };
        await WriteEntryAsync(zip, "summary.json", JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        // A2DP Windows Bridge's own diagnostics (its addresses masked by the core), when it runs.
        if (await host.Bridge.DiagnosticsAsync() is { } bridge) await WriteEntryAsync(zip, "a2dpwb.txt", LogRedactor.Redact(bridge));

        foreach (var log in new DirectoryInfo(host.Paths.LogDirectory).GetFiles("encodeck-*.log").OrderByDescending(f => f.Name).Take(3))
        {
            string text;
            using (var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                text = await reader.ReadToEndAsync();
            }
            await WriteEntryAsync(zip, "logs/" + log.Name, LogRedactor.Redact(text));
        }
        // The LE Audio switch writes its steps (advert, per-earbud pairing results) to a trace of its own.
        if (await ReadTailAsync(EncoDeck.Bluetooth.LeAudio.LeAudioPairing.DiagnosticLogPath, LeAudioTraceTail) is { } trace)
            await WriteEntryAsync(zip, "logs/enco-leaudio.log", LogRedactor.Redact(trace));
        return path;
    }

    private const int LeAudioTraceTail = 256 * 1024;

    /// <summary>The last <paramref name="maxBytes"/> of a file, or null when it does not exist or cannot be read.</summary>
    private static async Task<string?> ReadTailAsync(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync(content);
    }

    public static void RevealInExplorer(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
