// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using EncoDeck.Platform.Components;
using Microsoft.Win32;

namespace EncoDeck.Platform.A2dpBridge;

public enum BridgeOrigin
{
    /// <summary>The unmodified upstream build EncoDeck downloaded from the project's own release.</summary>
    Managed,

    /// <summary>BlueCaster's own installer (it updates itself), or that of A2DP Windows Bridge 2.0.x.</summary>
    Installed,

    /// <summary>A copy found only because its core is running (e.g. a portable folder).</summary>
    Running,
}

/// <summary>The file names of one generation of the program: BlueCaster (3.0 and later) or A2DP Windows Bridge (2.0.x).</summary>
/// <param name="SourceInfo">The text that names the repository and commit the build comes from.</param>
public sealed record BridgeNames(string Core, string Window, string Driver, string SourceInfo, string License, string ThirdParty)
{
    public IReadOnlyList<string> Files => [Core, Window, Driver, SourceInfo, License, ThirdParty];
}

/// <summary>A folder with the core, the window and the driver tool (bluecaster-*.exe, or a2dpwb-*.exe up to 2.0.x).</summary>
public sealed record BridgeInstallation(string Directory, BridgeOrigin Origin, string? Version)
{
    public BridgeNames Names => A2dpBridgeComponent.NamesIn(Directory) ?? A2dpBridgeComponent.Current;

    public string CorePath => Path.Combine(Directory, Names.Core);

    public string WindowPath => Path.Combine(Directory, Names.Window);

    public string DriverPath => Path.Combine(Directory, Names.Driver);
}

/// <summary>
/// BlueCaster (github.com/SeiyaFunaokaJP/BlueCaster, called A2DP Windows Bridge up to 2.0.x): A2DP from a dedicated
/// USB Bluetooth adapter under WinUSB, with its own user-mode Bluetooth stack. EncoDeck downloads the official portable
/// build from the project's own releases (the newest one, betas included) and starts, watches and controls the core;
/// it never builds or patches it. Copies of 2.0.x (installed, running, or managed until their update) keep working.
/// </summary>
public static class A2dpBridgeComponent
{
    /// <summary>The pipe protocol this app speaks (protocol 1 of the ipc crate; the rename kept it).</summary>
    public const int Interface = A2dpBridgeConnection.ProtocolVersion;

    /// <summary>BlueCaster 3.0 and later.</summary>
    public static BridgeNames Current { get; } = new("bluecaster-core.exe", "bluecaster.exe", "bluecaster-driver.exe",
        "licenses/README.txt", "licenses/bluecaster/LICENSE", "licenses/third-party/THIRD_PARTY_LICENSES.md");

    /// <summary>A2DP Windows Bridge 2.0.x.</summary>
    public static BridgeNames Legacy { get; } = new("a2dpwb-core.exe", "a2dpwb.exe", "a2dpwb-driver.exe",
        "SOURCE.txt", "LICENSE", "THIRD_PARTY_LICENSES.md");

    private static readonly BridgeNames[] Generations = [Current, Legacy];

    /// <summary>Repositories of official builds: the current name, then the name up to 2.0.x (GitHub redirects it).</summary>
    private static readonly string[] Repositories = ["github.com/SeiyaFunaokaJP/BlueCaster", "github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge"];

    /// <summary>BlueCaster's Inno Setup (3.0 and later): its AppId, and its default folder under %LOCALAPPDATA%.</summary>
    private const string ProductName = "BlueCaster";
    private const string ProductUninstallKey = "{C7A109ED-F618-4214-8186-E4472C3BDD10}_is1";

    /// <summary>A2DP Windows Bridge 2.0.x's installs, until BlueCaster's setup replaces them.</summary>
    private const string LegacyProductName = "A2DP Windows Bridge";

    /// <summary>The HKCU Run values of "start with Windows": BlueCaster's, then A2DPWB (2.0.x; a 3.x core moves it).</summary>
    private static readonly string[] RunValues = ["BlueCaster", "A2DPWB"];

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static ComponentLayout Layout { get; } = new()
    {
        HasFiles = directory => NamesIn(directory) is { } names && names.Files.All(f => File.Exists(Path.Combine(directory, f))),
        Validate = Validate,
    };

    public static ComponentInstaller CreateInstaller(HttpClient http, string root, ComponentRelease? release = null)
    {
        release ??= ComponentCatalog.A2dpBridge;
        return new ComponentInstaller(http, root, release, release.CurrentPackage, Layout);
    }

    /// <summary>The generation whose core is in <paramref name="directory"/>, if any.</summary>
    public static BridgeNames? NamesIn(string directory)
    {
        try { return Generations.FirstOrDefault(n => File.Exists(Path.Combine(directory, n.Core))); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The official build's source note (licenses/README.txt; SOURCE.txt up to 2.0.x) names the repository and, at
    /// install time, must name the pinned commit.
    /// </summary>
    internal static void Validate(string directory, string version, string? pinnedCommit)
    {
        var names = NamesIn(directory) ?? throw new InvalidDataException("Not a BlueCaster build.");
        string source = File.ReadAllText(Path.Combine(directory, names.SourceInfo));
        if (!Repositories.Any(r => source.Contains(r, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Not a BlueCaster build.");
        if (pinnedCommit is not null && !source.Contains(pinnedCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The BlueCaster package is not the pinned upstream build.");
    }

    /// <summary>
    /// BlueCaster's own per-user installation (%LOCALAPPDATA%\BlueCaster by default), else one of A2DP Windows Bridge
    /// 2.0.x not updated yet. Folders under <paramref name="managedRoot"/> (EncoDeck's copies, which "start with
    /// Windows" may name) do not count.
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
            return new BridgeInstallation(full, BridgeOrigin.Installed, FileVersion(Path.Combine(full, NamesIn(full)!.Core)));
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
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Read($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProductUninstallKey}", "InstallLocation");
        yield return Path.Combine(local, ProductName);
        // "Start with Windows": "<dir>\bluecaster-core.exe" --background
        foreach (string value in RunValues)
            if (Read(RunKey, value) is { } run && ProgramOf(run) is { } program)
                yield return Path.GetDirectoryName(program);
        yield return Read($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{LegacyProductName}", "InstallLocation");
        yield return Read($@"Software\Seiya Funaoka\{LegacyProductName}", null);
        yield return Path.Combine(local, LegacyProductName);
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

    /// <summary>The folder of the running core (BlueCaster's, or a 2.0.x one), if one runs and its image can be read.</summary>
    public static string? RunningCoreDirectory()
    {
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        foreach (var names in Generations)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(names.Core)))
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
        }
        return null;
    }

    public static bool IsBridgeFolder(string directory)
    {
        try
        {
            return NamesIn(directory) is { } names && File.Exists(Path.Combine(directory, names.Window));
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
        using var process = Process.Start(start) ?? throw new IOException("Cannot start the BlueCaster core.");
    }

    /// <summary>
    /// The environment of a side-by-side test core: its own pipe and mutex (<c>BLUECASTER_INSTANCE</c>) and settings
    /// folder (<c>BLUECASTER_CONFIG_DIR</c>). The 2.0.x names are set too, which BlueCaster still reads and 2.0.x only reads.
    /// </summary>
    public static Dictionary<string, string> TestEnvironment(string? instance, string? configDirectory)
    {
        var environment = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(instance)) environment["BLUECASTER_INSTANCE"] = environment["A2DPWB_INSTANCE"] = instance;
        if (!string.IsNullOrEmpty(configDirectory)) environment["BLUECASTER_CONFIG_DIR"] = environment["A2DPWB_CONFIG_DIR"] = configDirectory;
        return environment;
    }

    /// <summary>
    /// Where the core keeps the previous driver of each adapter it assigned WinUSB to: <c>adapters\driver</c> of its
    /// settings folder, <c>driver</c> up to 2.0.x, and %APPDATA%\A2DPWB\driver until BlueCaster has taken that folder
    /// over. The first of them with records, as BlueCaster's uninstaller picks it.
    /// </summary>
    public static string DriverRecordsDirectory(string? settingsDirectory = null)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string settings = string.IsNullOrEmpty(settingsDirectory) ? Path.Combine(appData, ProductName) : settingsDirectory;
        string[] candidates = [Path.Combine(settings, "adapters", "driver"), Path.Combine(settings, "driver"), Path.Combine(appData, "A2DPWB", "driver")];
        return candidates.FirstOrDefault(d => DriverRecordCount(d) > 0) ?? candidates[0];
    }

    /// <summary>Adapters the core took from Windows and has not given back (one record each).</summary>
    public static int DriverRecordCount(string recordsDirectory)
    {
        try { return Directory.Exists(recordsDirectory) ? Directory.EnumerateFiles(recordsDirectory, "*.json").Count() : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Gives every recorded adapter back to Windows with one administrator prompt, as BlueCaster's uninstaller does:
    /// <c>bluecaster-driver.exe restore --records &lt;dir&gt;</c>. Returns the helper's exit code (0: all restored);
    /// a declined prompt throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<int> RestoreRecordedDriversAsync(BridgeInstallation installation, string recordsDirectory, CancellationToken ct = default)
    {
        string result = Path.Combine(Path.GetTempPath(), $"encodeck-bluecaster-restore-{Guid.NewGuid():N}.json");
        if (recordsDirectory.Contains('"') || result.Contains('"')) throw new ArgumentException("Unexpected quote in a path.", nameof(recordsDirectory));
        // Elevation needs ShellExecute, which takes one argument string; folder paths never contain quotes.
        var start = new ProcessStartInfo(installation.DriverPath, $"restore --records \"{recordsDirectory.TrimEnd('\\')}\" --out \"{result}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = installation.Directory,
        };
        try
        {
            using var process = Process.Start(start) ?? throw new IOException($"Cannot start {installation.Names.Driver}.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("The administrator prompt was declined.", ex); }
        finally
        {
            try { File.Delete(result); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Removes the "start with Windows" entries that name a copy under <paramref name="root"/> (one about to be deleted).</summary>
    public static void RemoveStartupEntryUnder(string root)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string value in RunValues)
            {
                if (key.GetValue(value) is not string command || ProgramOf(command) is not { } program) continue;
                if (Path.GetFullPath(program).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(value, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
    }

    /// <summary>BlueCaster's own window (first-run setup, adapter, firmware, pairing and diagnostics).</summary>
    public static void OpenWindow(BridgeInstallation installation)
    {
        using var process = Process.Start(new ProcessStartInfo(installation.WindowPath)
        {
            UseShellExecute = false,
            WorkingDirectory = installation.Directory,
        }) ?? throw new IOException("Cannot open BlueCaster.");
    }
}
