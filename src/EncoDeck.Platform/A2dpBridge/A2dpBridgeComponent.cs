// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using EncoDeck.Platform.Components;
using Microsoft.Win32;

namespace EncoDeck.Platform.A2dpBridge;

public enum BridgeOrigin
{
    /// <summary>The unmodified upstream build EncoDeck downloaded from its component release.</summary>
    Managed,

    /// <summary>A2DPWB's own installer (it updates itself).</summary>
    Installed,

    /// <summary>A copy found only because its core is running (e.g. a portable folder).</summary>
    Running,
}

/// <summary>A folder with a2dpwb-core.exe, a2dpwb.exe and a2dpwb-driver.exe.</summary>
public sealed record BridgeInstallation(string Directory, BridgeOrigin Origin, string? Version)
{
    public string CorePath => Path.Combine(Directory, A2dpBridgeComponent.CoreExe);

    public string WindowPath => Path.Combine(Directory, A2dpBridgeComponent.WindowExe);
}

/// <summary>
/// A2DP Windows Bridge (github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge): A2DP from a dedicated USB Bluetooth adapter
/// under WinUSB, with its own user-mode Bluetooth stack. EncoDeck downloads the official portable build from the
/// project's own releases (the newest 2.x, betas included) and starts, watches and controls the core; it never builds
/// or patches it.
/// </summary>
public static class A2dpBridgeComponent
{
    /// <summary>The A2DPWB pipe protocol this app speaks (protocol 1 of the ipc crate).</summary>
    public const int Interface = A2dpBridgeConnection.ProtocolVersion;

    public const string CoreExe = "a2dpwb-core.exe";
    public const string WindowExe = "a2dpwb.exe";
    public const string DriverExe = "a2dpwb-driver.exe";
    private const string ProductName = "A2DP Windows Bridge";

    public static ComponentLayout Layout { get; } = new()
    {
        RequiredFiles = [CoreExe, WindowExe, DriverExe, "SOURCE.txt", "LICENSE", "THIRD_PARTY_LICENSES.md"],
        Validate = Validate,
    };

    public static ComponentInstaller CreateInstaller(HttpClient http, string root, ComponentRelease? release = null)
    {
        release ??= ComponentCatalog.A2dpBridge;
        return new ComponentInstaller(http, root, release, release.CurrentPackage, Layout);
    }

    /// <summary>SOURCE.txt of the official build names the repository and, at install time, must name the pinned commit.</summary>
    internal static void Validate(string directory, string version, string? pinnedCommit)
    {
        string source = File.ReadAllText(Path.Combine(directory, "SOURCE.txt"));
        if (!source.Contains("github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Not an A2DP Windows Bridge build.");
        if (pinnedCommit is not null && !source.Contains(pinnedCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The A2DPWB package is not the pinned upstream build.");
    }

    /// <summary>
    /// A2DPWB's own per-user installation (NSIS, %LOCALAPPDATA%\A2DP Windows Bridge by default), if any. Folders under
    /// <paramref name="managedRoot"/> (EncoDeck's copies, which A2DPWB's "start with Windows" may name) do not count.
    /// </summary>
    public static BridgeInstallation? FindInstalled(string? managedRoot = null)
    {
        string? managed = managedRoot is null ? null : Path.GetFullPath(managedRoot).TrimEnd('\\') + "\\";
        foreach (string? candidate in InstallCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string directory = candidate.Trim().Trim('"');
            if (!IsBridgeFolder(directory)) continue;
            string full = Path.GetFullPath(directory);
            if (managed is not null && (full.TrimEnd('\\') + "\\").StartsWith(managed, StringComparison.OrdinalIgnoreCase)) continue;
            return new BridgeInstallation(full, BridgeOrigin.Installed, FileVersion(Path.Combine(full, CoreExe)));
        }
        return null;
    }

    private static IEnumerable<string?> InstallCandidates()
    {
        string? Read(string key, string? name)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(key);
                return k?.GetValue(name) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }
        yield return Read($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProductName}", "InstallLocation");
        yield return Read($@"Software\Seiya Funaoka\{ProductName}", null);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);
        // "Start with Windows": "<dir>\a2dpwb-core.exe" --background
        if (Read(@"Software\Microsoft\Windows\CurrentVersion\Run", "A2DPWB") is { } run && ProgramOf(run) is { } program)
            yield return Path.GetDirectoryName(program);
    }

    /// <summary>The program of a command line: the quoted first token, or everything up to ".exe".</summary>
    internal static string? ProgramOf(string commandLine)
    {
        string text = commandLine.Trim();
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : null;
    }

    /// <summary>The folder of the running core, if one runs and its image can be read.</summary>
    public static string? RunningCoreDirectory()
    {
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(CoreExe)))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    if (process.MainModule?.FileName is { } path) return Path.GetDirectoryName(path);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return null;
    }

    public static bool IsBridgeFolder(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, CoreExe)) && File.Exists(Path.Combine(directory, WindowExe));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static string? FileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            string? version = info.ProductVersion ?? info.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or ArgumentException) { return null; }
    }

    /// <summary>Starts the resident core without its window (<c>--background</c>): tray, IPC and the stack, as "start with Windows" does.</summary>
    public static void StartCore(BridgeInstallation installation, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(installation.CorePath, "--background")
        {
            // Started like from the Start menu: nothing of EncoDeck's (console, handles) is inherited by the resident core.
            // A test instance needs its environment, which only a direct start can pass.
            UseShellExecute = environment is not { Count: > 0 },
            WorkingDirectory = installation.Directory,
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new IOException("Cannot start the A2DPWB core.");
    }

    /// <summary>Where A2DPWB keeps the previous driver of each adapter it assigned WinUSB to (its settings folder's <c>driver</c>).</summary>
    public static string DriverRecordsDirectory(string? settingsDirectory = null) =>
        Path.Combine(string.IsNullOrEmpty(settingsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "A2DPWB") : settingsDirectory, "driver");

    /// <summary>Adapters A2DPWB took from Windows and has not given back (one record each).</summary>
    public static int DriverRecordCount(string recordsDirectory)
    {
        try { return Directory.Exists(recordsDirectory) ? Directory.EnumerateFiles(recordsDirectory, "*.json").Count() : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Gives every recorded adapter back to Windows with one administrator prompt, as A2DPWB's uninstaller does:
    /// <c>a2dpwb-driver.exe restore --records &lt;dir&gt;</c>. Returns the helper's exit code (0: all restored);
    /// a declined prompt throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<int> RestoreRecordedDriversAsync(BridgeInstallation installation, string recordsDirectory, CancellationToken ct = default)
    {
        string result = Path.Combine(Path.GetTempPath(), $"encodeck-a2dpwb-restore-{Guid.NewGuid():N}.json");
        if (recordsDirectory.Contains('"') || result.Contains('"')) throw new ArgumentException("Unexpected quote in a path.", nameof(recordsDirectory));
        // Elevation needs ShellExecute, which takes one argument string; folder paths never contain quotes.
        var start = new ProcessStartInfo(Path.Combine(installation.Directory, DriverExe), $"restore --records \"{recordsDirectory.TrimEnd('\\')}\" --out \"{result}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = installation.Directory,
        };
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Cannot start a2dpwb-driver.exe.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("The administrator prompt was declined.", ex); }
        finally
        {
            try { File.Delete(result); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Removes A2DPWB's "start with Windows" entry when it names a copy under <paramref name="root"/> (one about to be deleted).</summary>
    public static void RemoveStartupEntryUnder(string root)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key?.GetValue("A2DPWB") is not string command || ProgramOf(command) is not { } program) return;
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(program).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) key.DeleteValue("A2DPWB", throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
    }

    /// <summary>A2DPWB's own window (first-run setup, adapter, firmware, pairing and diagnostics).</summary>
    public static void OpenWindow(BridgeInstallation installation)
    {
        using var process = Process.Start(new ProcessStartInfo(installation.WindowPath)
        {
            UseShellExecute = false,
            WorkingDirectory = installation.Directory,
        }) ?? throw new IOException("Cannot open A2DPWB.");
    }
}
