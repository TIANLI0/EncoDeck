// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace EncoDeck.Platform.Lhdc;

public sealed record LhdcDevice(string Instance, string Name, string Address)
{
    public string Label => $"{Name} · {Address}";
}

public sealed record LhdcProfile(int SampleRate = 48000, int Bits = 24, int Kbps = 400, bool Adaptive = false)
{
    public void Validate()
    {
        if (SampleRate is not (44100 or 48000 or 96000 or 192000) || Bits is not (16 or 24)
            || Kbps is not (64 or 160 or 192 or 240 or 256 or 320 or 400 or 480 or 500 or 900 or 1000))
            throw new ArgumentOutOfRangeException(nameof(SampleRate), "Invalid LHDC profile.");
    }
}

/// <summary>EncoDeck's UI talks directly to the source-built CLI and deployment script.</summary>
public sealed class LhdcBackend(string packageRoot, string resultsDirectory)
{
    public async Task<JsonObject> QueryAsync(string? instance, string command, params string[] arguments)
    {
        // Only expose read-only commands to the unelevated process.
        if (command is not ("targets" or "inspect" or "audio-status" or "profile-options" or "profile-check"))
            throw new ArgumentException("Unsupported query.", nameof(command));
        var start = new ProcessStartInfo(Path.Combine(packageRoot, "backend", "lhdc-host.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(instance) && command != "targets")
        {
            start.ArgumentList.Add("--device");
            start.ArgumentList.Add(instance);
        }
        start.ArgumentList.Add(command);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start LHDC backend.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("LHDC backend query timed out.");
        }
        string text = await output.ConfigureAwait(false), errors = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException(errors.Trim().Length > 0 ? errors.Trim() : text.Trim());
        string line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? throw new InvalidDataException("No LHDC backend result.");
        return JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("Invalid LHDC backend result.");
    }

    internal static string ModeCommand(string root, string instance, bool lhdc, LhdcProfile profile, string resultFile)
    {
        profile.Validate();
        if (!instance.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) || instance.Any(char.IsControl))
            throw new ArgumentException("Select a paired Bluetooth Audio Sink.", nameof(instance));
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        return $"& {Quote(Path.Combine(root, "scripts", "Set-AudioMode.ps1"))} -PackageRoot {Quote(root)} "
            + $"-DeviceInstance {Quote(instance)} -Mode {(lhdc ? "LHDC" : "Windows")} "
            + $"-SampleRate {profile.SampleRate.ToString(CultureInfo.InvariantCulture)} -Bits {profile.Bits} -Kbps {profile.Kbps} "
            + (profile.Adaptive ? "-AdaptiveBitrate " : "") + $"-ResultFile {Quote(resultFile)}";
    }

    public async Task<JsonObject> ApplyAsync(string instance, bool lhdc, LhdcProfile profile)
    {
        Directory.CreateDirectory(resultsDirectory);
        string resultFile = Path.Combine(resultsDirectory, $"mode-{Guid.NewGuid():N}.json");
        string command = ModeCommand(packageRoot, instance, lhdc, profile, resultFile);
        // The next line asks for administrator rights for this runtime's PowerShell and script: not for an altered one.
        await Task.Run(() => LhdcComponent.VerifyFiles(packageRoot)).ConfigureAwait(false);
        var start = new ProcessStartInfo(Path.Combine(packageRoot, "runtime", "powershell", "pwsh.exe"))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
        };
        using var process = Process.Start(start) ?? throw new IOException("Cannot start LHDC deployment.");
        // Never kill a deployment: the upstream script must be allowed to finish rollback.
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (!File.Exists(resultFile)) throw new IOException($"LHDC deployment returned no result (exit {process.ExitCode}).");
        var result = JsonNode.Parse(await File.ReadAllTextAsync(resultFile).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("Invalid LHDC deployment result.");
        if (process.ExitCode != 0 || result["Completed"]?.GetValue<bool>() != true)
            throw new IOException($"{result["Error"]} {result["RecoveryError"]} RebootRequired={result["RebootRequired"]}. {result["EvidenceDirectory"]}");
        return result;
    }
}
