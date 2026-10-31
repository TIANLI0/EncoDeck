// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EncoDeck.Platform.LeAudioCodec;

public enum LeAudioCodecSupport
{
    Supported,
    /// <summary>The hook is x64 code; the Bluetooth audio service of an Arm PC cannot load it.</summary>
    NotX64,
    /// <summary>EncoDeck.LeHook.dll is not next to the app (a build without MinGW-w64).</summary>
    ComponentMissing,
    /// <summary>This Windows' LE Audio code lacks the functions the hook patches, or they changed shape.</summary>
    UnsupportedWindows,
    /// <summary>Microsoft's public symbols for this Windows' LE Audio code could not be downloaded (try again later).</summary>
    SymbolsUnavailable,
}

/// <summary>
/// Which LE Audio transport Windows uses. Windows itself picks low latency while a program plays game audio and high
/// reliability otherwise; the hook can pin one of them.
/// </summary>
public enum LeAudioLatencyMode
{
    Auto = 0,
    /// <summary>5 retransmissions, at most 15 ms on the air (20 ms with 10 ms frames).</summary>
    LowLatency = 1,
    /// <summary>13 retransmissions, at most 75 ms on the air (100 ms with 10 ms frames).</summary>
    HighReliability = 2,
}

/// <summary>What was last applied, and whether the hook is still in the running Bluetooth audio service.</summary>
public sealed record LeAudioCodecApplied(LeAudioCodecPreset Preset, LeAudioLatencyMode Mode, bool Active)
{
    public bool IsWindowsDefault => Preset.IsWindowsDefault && Mode == LeAudioLatencyMode.Auto;
}

/// <summary>The transport of the latest LE Audio stream Windows set up, as the hook recorded it.</summary>
public sealed record LeAudioLinkConfig(DateTimeOffset At, int FrameUs, int MaxSdu, int LatencyMs, int Retransmissions)
{
    /// <summary>Bitrate of one earbud, in kbit/s.</summary>
    public double Kbps => FrameUs == 0 ? 0 : MaxSdu * 8 * 1000.0 / FrameUs;

    /// <summary>Low latency or high reliability, from the transport limit (15/20 ms against 75/100 ms).</summary>
    public LeAudioLatencyMode Mode => LatencyMs <= 20 ? LeAudioLatencyMode.LowLatency : LeAudioLatencyMode.HighReliability;

    /// <summary>Whether this link is what <paramref name="preset"/> and <paramref name="mode"/> ask for.</summary>
    public bool Matches(LeAudioCodecPreset preset, LeAudioLatencyMode mode) =>
        (preset.IsWindowsDefault || (preset.FrameUs == FrameUs && preset.Octets == MaxSdu))
        && (mode == LeAudioLatencyMode.Auto || mode == Mode);
}

/// <summary>
/// Experimental: makes Windows ask LE Audio earbuds for a chosen LC3 preset. EncoDeck.LeHook.dll is loaded into the
/// svchost of BthAvctpSvc, where Windows' LE Audio client (Microsoft.Bluetooth.Audio.dll) picks the codec
/// configuration and its QoS; it rewrites both from HKLM\SOFTWARE\EncoDeck\LeHook for every new stream. Which functions
/// to patch is worked out for the installed Windows from its public symbols (<see cref="LeAudioHookTargets"/>), so no
/// Windows version is built in. Applying needs an elevated process (<see cref="ApplyElevatedAsync"/>); the hook lasts
/// until the service restarts (sign-out, reboot, Bluetooth turned off), which <see cref="ReadApplied"/> detects from the
/// service's process id.
/// </summary>
public static class LeAudioCodecHook
{
    public const string HookDllName = "EncoDeck.LeHook.dll";
    internal const string ServiceName = "BthAvctpSvc";
    internal const string ConfigKey = @"SOFTWARE\EncoDeck\LeHook";
    private const string TargetModule = "Microsoft.Bluetooth.Audio.dll";

    private static string TargetPath => Path.Combine(Environment.SystemDirectory, TargetModule);

    /// <summary>%ProgramData%\EncoDeck\LeHook: the copies loaded into the service, its log and the applied state.</summary>
    public static string HookDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EncoDeck", "LeHook");

    public static string LogPath => Path.Combine(HookDirectory, "lehook.log");

    /// <summary>Public symbols downloaded for the support check (per user; the elevated apply keeps its own copy).</summary>
    public static string UserSymbolCache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EncoDeck", "Symbols");

    private static string ElevatedSymbolCache => Path.Combine(HookDirectory, "Symbols");

    /// <summary>Written by the hook for every new stream (<see cref="ReadLink"/>).</summary>
    public static string LinkPath => Path.Combine(HookDirectory, "link.txt");

    private static string StatePath => Path.Combine(HookDirectory, "state.txt");

    /// <summary>The hook DLL shipped next to the app.</summary>
    public static string BundledDll => Path.Combine(AppContext.BaseDirectory, HookDllName);

    /// <summary>
    /// No elevation. Finds the patched functions in this Windows' LE Audio code; the first time for a Windows build
    /// that downloads its public symbols (about 12 MB, cached per user).
    /// </summary>
    public static async Task<LeAudioCodecSupport> CheckSupportAsync(CancellationToken ct = default)
    {
        bool x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64 && Environment.Is64BitOperatingSystem;
        if (!x64 || !File.Exists(BundledDll)) return Evaluate(x64, File.Exists(BundledDll), LeAudioHookResolveError.None);
        var (_, error) = await LeAudioHookTargets.ResolveAsync(TargetPath, UserSymbolCache, ct).ConfigureAwait(false);
        return Evaluate(x64, componentPresent: true, error);
    }

    internal static LeAudioCodecSupport Evaluate(bool x64, bool componentPresent, LeAudioHookResolveError resolve)
    {
        if (!x64) return LeAudioCodecSupport.NotX64;
        if (!componentPresent) return LeAudioCodecSupport.ComponentMissing;
        return resolve switch
        {
            LeAudioHookResolveError.None => LeAudioCodecSupport.Supported,
            LeAudioHookResolveError.SymbolsUnavailable => LeAudioCodecSupport.SymbolsUnavailable,
            _ => LeAudioCodecSupport.UnsupportedWindows,
        };
    }

    /// <summary>The last applied preset and whether the service that got it is still running. No elevation.</summary>
    public static LeAudioCodecApplied ReadApplied() => Interpret(ReadState(), ServiceProcess.GetProcessId(ServiceName));

    internal static LeAudioCodecApplied Interpret(AppliedState? state, int servicePid)
    {
        if (state is null) return new LeAudioCodecApplied(LeAudioCodecPreset.WindowsDefault, LeAudioLatencyMode.Auto, true);
        var preset = LeAudioCodecPreset.Find(state.FrameUs, state.Octets) ?? LeAudioCodecPreset.WindowsDefault;
        var mode = Enum.IsDefined(state.Mode) ? state.Mode : LeAudioLatencyMode.Auto;
        if (preset.IsWindowsDefault && mode == LeAudioLatencyMode.Auto)
            return new LeAudioCodecApplied(preset, mode, true);
        return new LeAudioCodecApplied(preset, mode, servicePid != 0 && servicePid == state.ServicePid);
    }

    internal sealed record AppliedState(int ServicePid, int FrameUs, int Octets, LeAudioLatencyMode Mode = LeAudioLatencyMode.Auto);

    private static Dictionary<string, string> ParseValues(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

    private static long GetNumber(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0;

    internal static AppliedState? ParseState(string text)
    {
        var values = ParseValues(text);
        int Get(string key) => (int)GetNumber(values, key);
        return values.Count == 0 ? null : new AppliedState(Get("pid"), Get("frameUs"), Get("octets"), (LeAudioLatencyMode)Get("mode"));
    }

    internal static string FormatState(AppliedState state) =>
        string.Create(CultureInfo.InvariantCulture, $"pid={state.ServicePid}\nframeUs={state.FrameUs}\noctets={state.Octets}\nmode={(int)state.Mode}\n");

    /// <summary>link.txt as the hook writes it (time is a FILETIME); null when incomplete.</summary>
    internal static LeAudioLinkConfig? ParseLink(string text)
    {
        var values = ParseValues(text);
        long time = GetNumber(values, "time");
        int frameUs = (int)GetNumber(values, "frameUs"), maxSdu = (int)GetNumber(values, "maxSdu");
        if (time <= 0 || frameUs <= 0 || maxSdu <= 0) return null;
        return new LeAudioLinkConfig(DateTimeOffset.FromFileTime(time), frameUs, maxSdu,
            (int)GetNumber(values, "latencyMs"), (int)GetNumber(values, "retransmissions"));
    }

    /// <summary>The transport of the latest LE Audio stream, as the hook recorded it; null before the first. No elevation.</summary>
    public static LeAudioLinkConfig? ReadLink()
    {
        try
        {
            using var stream = new FileStream(LinkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return ParseLink(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static AppliedState? ReadState()
    {
        try { return File.Exists(StatePath) ? ParseState(File.ReadAllText(StatePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Exit codes of <see cref="ApplyElevatedAsync"/>.</summary>
    public static class Result
    {
        public const int Ok = 0;
        public const int TargetNotLoaded = 2;
        public const int UnsupportedWindows = 3;
        public const int HookFailed = 4;
        public const int SymbolsUnavailable = 5;
        public const int ServiceNotRunning = 6;
        public const int ComponentMissing = 7;
        public const int NotElevated = 8;
        public const int Failed = 9;
    }

    /// <summary>
    /// In an elevated process: stores the preset and transport for the hook, then turns the hook on in the running
    /// service and records what was applied. With Windows' own choice the hook stays on but only records each new
    /// link, so that one can be confirmed too. Returns a <see cref="Result"/> code. Takes effect the next time Windows
    /// builds an LE Audio stream.
    /// </summary>
    public static async Task<int> ApplyElevatedAsync(LeAudioCodecPreset preset, LeAudioLatencyMode mode, string hookDll, Action<string>? log = null)
    {
        log ??= _ => { };
        if (!Environment.IsPrivilegedProcess) return Result.NotElevated;
        try
        {
            // Worked out again here, from a symbol cache only administrators can change: the hook patches what HKLM names.
            Directory.CreateDirectory(HookDirectory);
            var (targets, error) = await LeAudioHookTargets.ResolveAsync(TargetPath, ElevatedSymbolCache).ConfigureAwait(false);
            if (targets is null)
            {
                log($"resolve: {error}");
                return error == LeAudioHookResolveError.SymbolsUnavailable ? Result.SymbolsUnavailable : Result.UnsupportedWindows;
            }
            using (var key = Registry.LocalMachine.CreateSubKey(ConfigKey))
            {
                key.SetValue("FrameDurationUs", preset.FrameUs, RegistryValueKind.DWord);
                key.SetValue("OctetsPerFrame", preset.Octets, RegistryValueKind.DWord);
                key.SetValue("LatencyMode", (int)mode, RegistryValueKind.DWord);
                for (int i = 0; i < targets.Count; i++)
                {
                    key.SetValue($"Hook{i}Rva", targets[i].Rva, RegistryValueKind.DWord);
                    key.SetValue($"Hook{i}Prologue", targets[i].Prologue, RegistryValueKind.Binary);
                    log($"target {i}: 0x{targets[i].Rva:X} {Convert.ToHexString(targets[i].Prologue)} {targets[i].Name}");
                }
            }
            int pid = ServiceProcess.GetProcessId(ServiceName);
            int result;
            if (pid == 0)
            {
                result = Result.ServiceNotRunning;
            }
            else if (!File.Exists(hookDll))
            {
                result = Result.ComponentMissing;
            }
            else
            {
                result = LeAudioHookInjector.Enable(pid, hookDll, HookDirectory, log);
            }
            if (result == Result.Ok)
                File.WriteAllText(StatePath, FormatState(new AppliedState(pid, preset.FrameUs, preset.Octets, mode)));
            return result;
        }
        catch (Exception ex)
        {
            log($"failed: {ex.Message}");
            return Result.Failed;
        }
    }
}
