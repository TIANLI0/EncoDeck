// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Diagnostics;
using EncoDeck.Platform.LeAudioCodec;
using EncoDeck.Platform.LeAudioSync;
using EncoDeck.Platform.Packaging;
using EncoDeck.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// Labs LE Audio bitrate: starts this executable elevated with <see cref="ApplyArgument"/> to hook Windows' Bluetooth
/// audio service (<see cref="LeAudioCodecHook"/>), serves that elevated child, and watches the link the hook records so
/// the user learns when an applied choice actually took effect. The service loses the hook when it restarts (reboot,
/// Bluetooth turned off); while LE Audio earbuds are connected the choice is then put back through the scheduled task
/// the apply registered (<see cref="LeAudioHookRestoreTask"/>), without another prompt. Not offered in the Store package.
/// </summary>
public sealed class LeAudioCodecCompanion : IDisposable
{
    /// <summary>
    /// <c>--le-hook-apply &lt;preset id&gt; &lt;mode&gt;</c>: elevated child; applies and exits with a
    /// <see cref="LeAudioCodecHook.Result"/> code.
    /// </summary>
    public const string ApplyArgument = "--le-hook-apply";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>A restore that did not take is tried again after this, a few times per service process.</summary>
    private static readonly TimeSpan RestoreRetry = TimeSpan.FromSeconds(30);
    private const int RestoreAttempts = 3;

    private readonly LeAudioSyncCompanion _leAudio;
    private readonly ISettingsStore _settings;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private DateTime _linkWritten;
    private (LeAudioCodecPreset Preset, LeAudioLatencyMode Mode, DateTimeOffset Since)? _expected;
    private int _restorePid;
    private int _restoreAttempts;
    private DateTimeOffset _lastRestore;
    private bool _rebuildAfterRestore;

    public LeAudioCodecCompanion(LeAudioSyncCompanion leAudio, ISettingsStore settings, ILogger logger)
    {
        _leAudio = leAudio;
        _settings = settings;
        _logger = logger;
    }

    public static bool Available => !PackageIdentity.IsPackaged;

    /// <summary>The transport of the latest LE Audio stream the hook saw; null before the first.</summary>
    public LeAudioLinkConfig? Link { get; private set; }

    /// <summary>An applied choice waits for Windows to set up a new LE Audio stream.</summary>
    public bool Pending
    {
        get { lock (_gate) return _expected is not null; }
    }

    /// <summary>Raised on a background thread when <see cref="Link"/> or <see cref="Pending"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on a background thread when a new link matches what was just applied.</summary>
    public event EventHandler<LeAudioLinkConfig>? Switched;

    public void Start()
    {
        if (!Available) return;
        _ = PollAsync(_cts.Token);
    }

    /// <summary>After a successful apply: report the first new link that matches.</summary>
    public void Expect(LeAudioCodecPreset preset, LeAudioLatencyMode mode)
    {
        lock (_gate) _expected = (preset, mode, DateTimeOffset.Now);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                try { Check(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Could not read the LE Audio link record");
                }
                Restore();
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Check()
    {
        DateTime written = File.Exists(LeAudioCodecHook.LinkPath) ? File.GetLastWriteTimeUtc(LeAudioCodecHook.LinkPath) : default;
        if (written == _linkWritten) return;
        _linkWritten = written;
        if (LeAudioCodecHook.ReadLink() is not { } link) return;
        Link = link;
        bool switched;
        lock (_gate)
        {
            switched = _expected is { } expected && link.At >= expected.Since && link.Matches(expected.Preset, expected.Mode);
            if (switched) _expected = null;
        }
        _logger.LogInformation("LE Audio link: {FrameUs} us, max SDU {MaxSdu}, latency {LatencyMs} ms, {Retransmissions} retransmissions",
            link.FrameUs, link.MaxSdu, link.LatencyMs, link.Retransmissions);
        Changed?.Invoke(this, EventArgs.Empty);
        if (switched) Switched?.Invoke(this, link);
    }

    /// <summary>
    /// Puts an applied choice back once the service that had it is gone, while LE Audio (LC3) earbuds are connected, and
    /// then rebuilds the link the calibration holds, which was set up before the hook returned.
    /// </summary>
    private void Restore()
    {
        LeAudioCodecApplied applied = LeAudioCodecHook.ReadApplied();
        if (_rebuildAfterRestore && applied.Active)
        {
            _rebuildAfterRestore = false;
            var guard = _leAudio.Guard;
            if (_settings.Current.LeAudioSyncGuard && guard.Status.State is LeAudioSyncState.NeedsCheck or LeAudioSyncState.Aligned or LeAudioSyncState.Checking)
                guard.Rebuild();
        }
        if (applied.IsWindowsDefault || applied.Active || !_leAudio.LeAudioConnected) return;
        int pid = LeAudioCodecHook.ServicePid;
        if (pid == 0) return;
        if (pid != _restorePid)
        {
            _restorePid = pid;
            _restoreAttempts = 0;
        }
        if (_restoreAttempts >= RestoreAttempts || DateTimeOffset.UtcNow - _lastRestore < RestoreRetry) return;
        _restoreAttempts++;
        _lastRestore = DateTimeOffset.UtcNow;
        bool started = LeAudioHookRestoreTask.Run();
        _logger.LogInformation("LE Audio settings lost with the Bluetooth audio service; restore {Result}", started ? "started" : "unavailable (apply again)");
        _rebuildAfterRestore |= started;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>Command-line name of a transport mode, and back.</summary>
    public static string ModeArgument(LeAudioLatencyMode mode) => mode switch
    {
        LeAudioLatencyMode.LowLatency => "low",
        LeAudioLatencyMode.HighReliability => "high",
        _ => "auto",
    };

    public static LeAudioLatencyMode ParseMode(string? value) => value?.ToLowerInvariant() switch
    {
        "low" => LeAudioLatencyMode.LowLatency,
        "high" => LeAudioLatencyMode.HighReliability,
        _ => LeAudioLatencyMode.Auto,
    };

    /// <summary>Command-line flag of the elevated child: calls must not take over the link.</summary>
    private const string NoCallArgument = "nocall";

    /// <summary>
    /// Runs in the elevated child, before any window or single-instance check. <paramref name="arguments"/> follow
    /// <see cref="ApplyArgument"/>: the preset id, the mode, then optionally <c>nocall</c> and
    /// <see cref="LeAudioHookRestoreTask.RestoreArgument"/> (started by the restore task, which stays as it is). Otherwise
    /// a choice other than Windows' own registers the restore task with the same arguments, and Windows' own removes it.
    /// </summary>
    public static int RunElevatedApply(IReadOnlyList<string> arguments)
    {
        if (LeAudioCodecPreset.Find(arguments.ElementAtOrDefault(0) ?? "") is not { } preset) return LeAudioCodecHook.Result.Failed;
        string logPath = Path.Combine(LeAudioCodecHook.HookDirectory, "apply.log");
        void Log(string line)
        {
            try { File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var options = arguments.Skip(2).ToList();
        bool noCallMode = options.Contains(NoCallArgument, StringComparer.OrdinalIgnoreCase);
        bool restore = options.Contains(LeAudioHookRestoreTask.RestoreArgument, StringComparer.OrdinalIgnoreCase);
        var mode = ParseMode(arguments.ElementAtOrDefault(1));
        // Off the UI thread: the apply awaits a download and must not wait on the dispatcher it blocks.
        int result = Task.Run(() => LeAudioCodecHook.ApplyElevatedAsync(preset, mode, noCallMode, LeAudioCodecHook.BundledDll, Log))
            .GetAwaiter().GetResult();
        if (restore)
        {
            Log($"restored: result {result}");
        }
        else if (result == LeAudioCodecHook.Result.Ok && Environment.ProcessPath is { } exe)
        {
            if (preset.IsWindowsDefault && mode == LeAudioLatencyMode.Auto && !noCallMode)
                LeAudioHookRestoreTask.Unregister(Log);
            else
                LeAudioHookRestoreTask.Register(exe, string.Join(' ', [ApplyArgument, .. arguments, LeAudioHookRestoreTask.RestoreArgument]), Log);
        }
        return result;
    }

    /// <summary>
    /// Shows the administrator prompt and applies <paramref name="preset"/>, <paramref name="mode"/> and
    /// <paramref name="noCallMode"/>; returns the child's result code. A declined prompt throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<int> ApplyAsync(LeAudioCodecPreset preset, LeAudioLatencyMode mode, bool noCallMode)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        string arguments = $"{ApplyArgument} {preset.Id} {ModeArgument(mode)}" + (noCallMode ? $" {NoCallArgument}" : "");
        var start = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Cannot start the elevated helper.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The administrator prompt was declined.", ex);
        }
    }
}
