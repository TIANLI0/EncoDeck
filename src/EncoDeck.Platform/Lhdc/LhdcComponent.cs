// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Text.Json;
using EncoDeck.Platform.Components;
using Microsoft.Win32;

namespace EncoDeck.Platform.Lhdc;

/// <summary>
/// The LHDC runtime EncoDeck compiles from LHDC-Win at a pinned commit and publishes as its own component release
/// (<c>lhdc-v…</c>), independent of app releases. Nothing is installed into Windows by downloading it.
/// </summary>
public static class LhdcComponent
{
    public const string Repository = "https://github.com/Wayne-u/LHDC-Win";

    /// <summary>The backend contract this app drives (lhdc-host queries and Set-AudioMode.ps1 parameters).</summary>
    public const int Interface = 1;

    /// <summary>Results of the deployment script live next to the versions and survive clean-ups.</summary>
    public const string ResultsFolder = "results";

    private static readonly string[] Required = ["runtime.json", "release.json", "checksums.json",
        "backend/lhdc-host.exe", "backend/lhdc-service.exe", "backend/lhdc-device.exe", "backend/lhdc-audio-device.exe",
        "scripts/Set-AudioMode.ps1", "runtime/powershell/pwsh.exe", "drivers/lhdc-test.cer",
        "drivers/audio/lhdc-audio.sys", "drivers/transport/lhdc-transport.sys"];

    public static ComponentLayout Layout { get; } = new() { RequiredFiles = Required, Validate = Validate };

    public static ComponentInstaller CreateInstaller(HttpClient http, string root, ComponentRelease? release = null)
    {
        release ??= ComponentCatalog.Lhdc;
        return new ComponentInstaller(http, root, release, release.PackageFor(Architecture.X64), Layout) { KeepFolders = [ResultsFolder] };
    }

    /// <summary>
    /// Whether the deployment left the LHDC service set to start: "Restore Windows audio" stops it and sets it to
    /// Disabled, so anything else means LHDC may still be in use. Read from the registry, no administrator rights.
    /// </summary>
    public static bool ServiceEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LHDC-Win");
            return key?.GetValue("Start") is int start && start != 4;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>
    /// The bitrate the LHDC service streams at now, in kbps; 0 while it plays nothing or is turned off. The service
    /// publishes it in its registry key, which needs no administrator rights to read.
    /// </summary>
    public static int ActiveBitrate()
    {
        if (!ServiceEnabled()) return 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\LHDC-Win");
            return key?.GetValue("ActiveBitrate") is int kbps && kbps > 0 ? kbps : 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return 0; }
    }

    /// <summary>A runtime folder built by build/build-lhdc.ps1 (downloaded, or bundled by an offline installer).</summary>
    public static bool IsRuntime(string directory)
    {
        try
        {
            if (!Layout.HasRequiredFiles(directory)) return false;
            Validate(directory, ReadVersion(directory), null);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or KeyNotFoundException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Checks the runtime in <paramref name="directory"/> against the SHA-256 list its build wrote (checksums.json): every
    /// listed file is there unchanged and nothing else was added (a DLL next to pwsh.exe would be loaded by it). The
    /// download is verified once, as an archive; this is run again before each deployment, which starts the runtime's
    /// PowerShell as administrator, so a runtime damaged or altered since is not. The list sits in the same folder, which
    /// the user's programs can write to like EncoDeck's own: it tells an altered runtime apart from the built one, not
    /// one altered together with its list. Throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static void VerifyFiles(string directory)
    {
        const string list = "checksums.json";
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var checksums = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, list)));
            foreach (var entry in checksums.RootElement.EnumerateArray())
            {
                string? path = entry.GetProperty("Path").GetString(), sha = entry.GetProperty("SHA256").GetString();
                if (string.IsNullOrEmpty(path) || sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit) || !expected.TryAdd(path.Replace('\\', '/'), sha))
                    throw new InvalidDataException("Invalid LHDC checksum list.");
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FileNotFoundException)
        {
            throw new InvalidDataException("Invalid LHDC checksum list.", ex);
        }
        if (expected.Count == 0) throw new InvalidDataException("Invalid LHDC checksum list.");

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None };
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
        {
            string relative = Path.GetRelativePath(directory, entry.FullName).Replace('\\', '/');
            // A link could point the deployment at files outside the runtime.
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw Changed(relative);
            if (entry is not FileInfo file || relative.Equals(list, StringComparison.OrdinalIgnoreCase)) continue;
            if (!expected.Remove(relative, out string? sha)) throw Changed(relative);
            using var stream = file.OpenRead();
            if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw Changed(relative);
        }
        if (expected.Count > 0) throw Changed(expected.Keys.First());

        static InvalidDataException Changed(string path) =>
            new($"The LHDC runtime was changed after it was installed ({path}). Remove it and install it again.");
    }

    internal static string ReadVersion(string directory)
    {
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "runtime.json")));
        return runtime.RootElement.GetProperty("ComponentVersion").GetString() ?? throw new InvalidDataException("No LHDC component version.");
    }

    internal static void Validate(string directory, string version, string? pinnedCommit)
    {
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "runtime.json")));
        var r = runtime.RootElement;
        if (r.GetProperty("Schema").GetInt32() != 2
            || r.GetProperty("Component").GetString() != ComponentCatalog.LhdcId
            || r.GetProperty("ComponentVersion").GetString() != version
            || r.GetProperty("SourceRepository").GetString() != Repository
            || r.GetProperty("Architecture").GetString() != "x64") throw new InvalidDataException("Invalid LHDC runtime identity.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "release.json")));
        string? source = manifest.RootElement.GetProperty("SourceCommit").GetString();
        if (source is null || source.Length != 40 || !source.All(Uri.IsHexDigit)
            || manifest.RootElement.GetProperty("SourceTreeDirty").GetBoolean()
            || !string.Equals(r.GetProperty("SourceCommit").GetString(), source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid LHDC source provenance.");
        if (pinnedCommit is not null && !string.Equals(source, pinnedCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The LHDC package was not built from the pinned LHDC-Win commit.");
    }
}
