// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Diagnostics;
using EncoDeck.Platform.LeAudioCodec;
using EncoDeck.Platform.Packaging;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// Labs LE Audio bitrate: starts this executable elevated with <see cref="ApplyArgument"/> to hook Windows' Bluetooth
/// audio service (<see cref="LeAudioCodecHook"/>), serves that elevated child, and watches the link the hook records so
/// the user learns when an applied choice actually took effect. Not offered in the Store package.
/// </summary>
public sealed class LeAudioCodecCompanion : IDisposable
{
    /// <summary>
    /// <c>--le-hook-apply &lt;preset id&gt; &lt;mode&gt;</c>: elevated child; applies and exits with a
    /// <see cref="LeAudioCodecHook.Result"/> code.
    /// </summary>
    public const string ApplyArgument = "--le-hook-apply";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private DateTime _linkWritten;
    private (LeAudioCodecPreset Preset, LeAudioLatencyMode Mode, DateTimeOffset Since)? _expected;

    public LeAudioCodecCompanion(ILogger logger) => _logger = logger;

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

    /// <summary>Runs in the elevated child, before any window or single-instance check.</summary>
    public static int RunElevatedApply(string? presetId, string? mode, string? noCall)
    {
        if (LeAudioCodecPreset.Find(presetId ?? "") is not { } preset) return LeAudioCodecHook.Result.Failed;
        string log = Path.Combine(LeAudioCodecHook.HookDirectory, "apply.log");
        bool noCallMode = string.Equals(noCall, NoCallArgument, StringComparison.OrdinalIgnoreCase);
        // Off the UI thread: the apply awaits a download and must not wait on the dispatcher it blocks.
        return Task.Run(() => LeAudioCodecHook.ApplyElevatedAsync(preset, ParseMode(mode), noCallMode, LeAudioCodecHook.BundledDll, line =>
        {
            try { File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        })).GetAwaiter().GetResult();
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
