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
/// USB Bluetooth adapter under WinUSB, with its own user-mode Bluetooth stack. EncoDeck can run the official installer
/// from the project's own releases (the newest one, betas included), then only finds, starts and connects to the core;
/// BlueCaster updates and uninstalls itself, and manages adapters and earbuds in its own window. EncoDeck never builds
/// or patches it. Portable copies EncoDeck downloaded before, and copies of 2.0.x, keep working.
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

    /// <summary>
    /// Where BlueCaster's official releases are. Only the place is known to this app: which release is the newest, and
    /// its installer's file, size and digest, are read from there when the user installs it.
    /// </summary>
    public const string Repository = "https://github.com/SeiyaFunaokaJP/BlueCaster";

    /// <summary>BlueCaster's Inno Setup (3.0 and later): its AppId, and its default folder under %LOCALAPPDATA%.</summary>
    private const string ProductName = "BlueCaster";
    private const string ProductUninstallKey = "{C7A109ED-F618-4214-8186-E4472C3BDD10}_is1";

    /// <summary>A2DP Windows Bridge 2.0.x's installs, until BlueCaster's setup replaces them.</summary>
    private const string LegacyProductName = "A2DP Windows Bridge";

    /// <summary>The HKCU Run values of "start with Windows": BlueCaster's, then A2DPWB (2.0.x; a 3.x core moves it).</summary>
    private static readonly string[] RunValues = ["BlueCaster", "A2DPWB"];

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// A portable copy that EncoDeck up to 1.15 downloaded into <paramref name="root"/> (named by its <c>current.json</c>).
    /// It is still found and started, never updated or removed; its version is read from the core itself.
    /// </summary>
    public static BridgeInstallation? FindDownloaded(string root)
    {
        try
        {
            string pointer = Path.Combine(root, "current.json");
            if (!File.Exists(pointer) || System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(pointer)) is not System.Text.Json.Nodes.JsonObject node) return null;
            string? folder = node["folder"]?.GetValue<string>(), packageRoot = node["root"]?.GetValue<string>() ?? "";
            if (!IsFolderName(folder) || (packageRoot.Length > 0 && !IsFolderName(packageRoot))) return null;
            string directory = Path.Combine(root, folder!, packageRoot);
            return IsBridgeFolder(directory) ? new BridgeInstallation(directory, BridgeOrigin.Managed, FileVersion(Path.Combine(directory, NamesIn(directory)!.Core))) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static bool IsFolderName(string? name) => name is { Length: > 0 and <= 160 } && name == Path.GetFileName(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");

    /// <summary>The generation whose core is in <paramref name="directory"/>, if any.</summary>
    public static BridgeNames? NamesIn(string directory)
    {
        try { return Generations.FirstOrDefault(n => File.Exists(Path.Combine(directory, n.Core))); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
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

    /// <summary>The architecture tag of BlueCaster's installer for this PC ("win64", "arm64").</summary>
    public static string SetupArchitecture =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "win64";

    /// <summary>Downloads BlueCaster's own installer to <paramref name="path"/>, held to the size and digest GitHub reports.</summary>
    public static Task DownloadSetupAsync(HttpClient http, UpstreamSetup setup, string path,
        IProgress<EncoDeck.Platform.Updates.UpdateDownloadProgress>? progress, CancellationToken ct) =>
        ComponentInstaller.DownloadAsync(http, [setup.Url], path, setup.Size, setup.Sha256, progress, ct);

    /// <summary>
    /// Runs BlueCaster's own installer unattended (a per-user installation, with its progress window) and returns its
    /// exit code. The installed BlueCaster then updates and uninstalls itself; EncoDeck only finds and starts it.
    /// </summary>
    public static async Task<int> RunSetupAsync(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        }) ?? throw new IOException("Cannot start the BlueCaster installer.");
        // Never cut an installation short: it is waited for even when the card closes.
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
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
