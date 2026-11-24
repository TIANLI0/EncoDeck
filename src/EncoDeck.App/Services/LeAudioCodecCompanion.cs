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
/// It polls only while there is something to watch: a choice other than Windows' own is applied, an applied choice waits
/// for its link, a restore waits for its rebuild, or LE Audio earbuds are connected; otherwise it sleeps until one of
/// those can begin (<see cref="Expect"/>, an LE Audio connection change).
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
    private readonly ConditionalPoll _poll;
    private DateTime _linkWritten;
    // What the last successful read said: a choice other than Windows' own is applied (poll thread only). Assumed
    // until the first read succeeds, so an unreadable state keeps the poll running as it always did.
    private bool _appliedNonDefault = true;
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
        _poll = new ConditionalPoll("LE Audio link poll", PollInterval, IsWatching, Poll, logger);
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
        // LE Audio earbuds connecting is when a lost choice is put back (Restore) and a new link can appear (Check).
        _leAudio.Changed += OnLeAudioChanged;
        _poll.Start();
    }

    private void OnLeAudioChanged(object? sender, EventArgs e) => _poll.Wake();

    /// <summary>After a successful apply: report the first new link that matches.</summary>
    public void Expect(LeAudioCodecPreset preset, LeAudioLatencyMode mode)
    {
        lock (_gate) _expected = (preset, mode, DateTimeOffset.Now);
        // Watch for that link even while nothing else keeps the poll running.
        _poll.Wake();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Whether to keep ticking. <see cref="Restore"/> can only act while a choice other than Windows' own is applied or a
    /// rebuild waits, and both keep the poll running, so the restore runs exactly when it did on an unconditional timer.
    /// With Windows' own choice and nothing pending, a new link record (<see cref="Check"/>) is picked up on the next
    /// wake instead (it is only written while LE Audio streams).
    /// </summary>
    private bool IsWatching() => Pending || _leAudio.LeAudioConnected || _rebuildAfterRestore || _appliedNonDefault;

    /// <summary>One tick: the link record, then the restore; a failure in one never skips the other or ends the poll.</summary>
    private void Poll()
    {
        try { Check(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the LE Audio link record");
        }
        catch (Exception ex)
        {
            // Includes a Changed / Switched subscriber that threw.
            _logger.LogWarning(ex, "LE Audio link check failed");
        }
        Restore();
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
        _appliedNonDefault = !applied.IsWindowsDefault;
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
        _leAudio.Changed -= OnLeAudioChanged;
        _poll.Dispose();
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
    /// <see cref="LeAudioHookRestoreTask.RestoreArgument"/> (passed by a restore task an earlier version
    /// registered). A choice other than Windows' own registers the restore task, and Windows' own removes it.
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
        // Only a task registered by an earlier version still gets here with "restore"; a successful run replaces it too.
        if (restore) Log($"restored: result {result}");
        if (result == LeAudioCodecHook.Result.Ok)
        {
            if (preset.IsWindowsDefault && mode == LeAudioLatencyMode.Auto && !noCallMode)
                LeAudioHookRestoreTask.Unregister(Log);
            else
                // The task runs the copy the service just loaded, from the folder only administrators can change; this
                // executable may sit in a folder the user's programs can write to and is never run by the task.
                LeAudioHookRestoreTask.Register(LeAudioCodecHook.AppliedCopy(LeAudioCodecHook.BundledDll), Log);
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
