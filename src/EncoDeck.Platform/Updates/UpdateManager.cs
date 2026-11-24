// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using EncoDeck.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Platform.Updates;

public enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    /// <summary>A newer release exists; nothing downloaded yet.</summary>
    Available,
    Downloading,
    /// <summary>The verified installer is on disk and can be started.</summary>
    Ready,
    /// <summary>The installer was started; the app is about to exit.</summary>
    Installing,
    Failed,
}

/// <summary>Why a check or download failed, in categories the UI can explain without exception text.</summary>
public enum UpdateFailure
{
    None,
    Offline,
    RateLimited,
    NotFound,
    /// <summary>Size or checksum mismatch.</summary>
    Verification,
    /// <summary>The download folder could not be written, or the installer could not be started.</summary>
    Storage,
    Other,
}

public sealed record UpdateStatus
{
    public UpdateStage Stage { get; init; }

    public UpdateRelease? Release { get; init; }

    public long Received { get; init; }

    public long Total { get; init; }

    public UpdateFailure Failure { get; init; }

    public string? InstallerPath { get; init; }

    /// <summary>The SHA-256 <see cref="InstallerPath"/> was verified against; checked again before it is started.</summary>
    public string? InstallerSha256 { get; init; }

    public DateTimeOffset? LastChecked { get; init; }

    public bool IsBusy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Installing;
}

/// <summary>
/// Checks GitHub Releases shortly after start-up and when the main window opens (when enabled, at most once
/// per <c>checkInterval</c>) and otherwise once a day, downloads a newer installer in the background and starts
/// it only when the user asks. All work runs off the UI thread; one check or download at a time.
/// </summary>
public sealed class UpdateManager : IAsyncDisposable
{
    /// <summary>Inno Setup switches for an in-place update; /RELAUNCH=1 makes the installer start EncoDeck again (see installer/EncoDeck.iss).</summary>
    public const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1";

    /// <summary>Passed by the installer when it relaunches EncoDeck after an update.</summary>
    public const string RelaunchArgument = "--updated";

    private readonly GitHubUpdateClient _client;
    private readonly ISettingsStore _settings;
    private readonly string _stateFile;
    private readonly ILogger _logger;
    private readonly TimeSpan _firstCheckDelay;
    private readonly TimeSpan _checkInterval;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Lock _gate = new();
    private readonly HashSet<SemanticVersion> _announced = [];
    private UpdateStatus _status;
    private Task? _loop;
    private bool _disposed;

    /// <param name="stateFile">Holds the time of the last successful check, so restarts do not hammer the API.</param>
    /// <param name="checkInterval">Minimum time between automatic checks.</param>
    /// <param name="signatures">Code-signature checks before the installer starts (Authenticode by default).</param>
    /// <param name="currentExecutable">The running app, whose signer the installer's must match; the process's own file by default.</param>
    public UpdateManager(GitHubUpdateClient client, ISettingsStore settings, string stateFile, ILogger<UpdateManager> logger,
        TimeSpan? firstCheckDelay = null, TimeSpan? checkInterval = null, ISignatureVerifier? signatures = null, string? currentExecutable = null)
    {
        _client = client;
        _settings = settings;
        _stateFile = stateFile;
        _logger = logger;
        _firstCheckDelay = firstCheckDelay ?? TimeSpan.FromSeconds(30);
        _checkInterval = checkInterval ?? TimeSpan.FromHours(1);
        _signatures = signatures ?? AuthenticodeVerifier.Instance;
        string? self = currentExecutable ?? Environment.ProcessPath;
        // Checked once, when an installer is first about to start; the running file does not change meanwhile.
        _selfSignature = new Lazy<SignatureInfo>(() => self is null ? new SignatureInfo(SignatureState.Unsigned) : _signatures.Verify(self));
        _status = new UpdateStatus { LastChecked = ReadLastCheck() };
    }

    private readonly ISignatureVerifier _signatures;
    private readonly Lazy<SignatureInfo> _selfSignature;

    /// <summary>Starts the installer; tests replace it so nothing is ever run.</summary>
    internal Action<System.Diagnostics.ProcessStartInfo> StartProcess { get; set; } = static info => System.Diagnostics.Process.Start(info)?.Dispose();

    public SemanticVersion CurrentVersion => _client.Current;

    public UpdateStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Raised on a background thread; handlers should read <see cref="Status"/> on their own thread.</summary>
    public event EventHandler<UpdateStatus>? StatusChanged;

    /// <summary>An automatic check downloaded a newer version. Raised once per version per session.</summary>
    public event EventHandler<UpdateRelease>? UpdateReady;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _disposed) return;
            _loop = Task.Run(() => RunAsync(_shutdown.Token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Installers of the version now running (or older) are no longer needed.
            _client.CleanUpInstalled();
            await Task.Delay(_firstCheckDelay, ct).ConfigureAwait(false);
            await CheckIfDueAsync().ConfigureAwait(false);
            // A tray-only session never opens the main window: keep checking once a day in the background.
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(BackgroundWake, ct).ConfigureAwait(false);
                if (_settings.Current.AutoCheckUpdates != false && IsDue(DateTimeOffset.UtcNow, BackgroundInterval))
                    await CheckCoreAsync(automatic: true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Update loop stopped"); }
    }

    private static readonly TimeSpan BackgroundWake = TimeSpan.FromHours(1);
    private static readonly TimeSpan BackgroundInterval = TimeSpan.FromHours(24);

    /// <summary>Automatic check (may download), when enabled and the last one is older than the check interval; e.g. when the main window opens.</summary>
    public Task CheckIfDueAsync() =>
        _settings.Current.AutoCheckUpdates != false && IsDue(DateTimeOffset.UtcNow) ? CheckCoreAsync(automatic: true, _shutdown.Token) : Task.CompletedTask;

    internal bool IsDue(DateTimeOffset now) => IsDue(now, _checkInterval);

    private bool IsDue(DateTimeOffset now, TimeSpan interval) => Status.LastChecked is not { } last || now - last >= interval || last > now.AddDays(1);

    /// <summary>User-initiated check; never downloads by itself.</summary>
    public Task CheckNowAsync() => CheckCoreAsync(automatic: false, _shutdown.Token);

    /// <summary>Downloads the available release (user-initiated). Returns the verified installer, or null.</summary>
    public async Task<string?> DownloadAsync()
    {
        if (Status is not { Release: { } release } || _disposed) return null;
        if (!await _busy.WaitAsync(0).ConfigureAwait(false)) return null;
        try { return await DownloadCoreAsync(release, automatic: false, _shutdown.Token).ConfigureAwait(false); }
        finally { _busy.Release(); }
    }

    /// <summary>Runs one automatic-style check now (tests; diagnostics).</summary>
    internal Task RunAutomaticCheckAsync() => CheckCoreAsync(automatic: true, _shutdown.Token);

    private async Task CheckCoreAsync(bool automatic, CancellationToken ct)
    {
        if (_disposed || !await _busy.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) return;
        try
        {
            var previous = Status;
            Publish(s => s with { Stage = UpdateStage.Checking, Failure = UpdateFailure.None });
            var result = await _client.CheckAsync(_settings.Current.IncludePrereleaseUpdates, ct).ConfigureAwait(false);
            if (result.Outcome is not (UpdateCheckOutcome.Offline or UpdateCheckOutcome.Failed))
            {
                // Rate-limited counts as a check too: retrying sooner would only extend the limit.
                var now = DateTimeOffset.UtcNow;
                WriteLastCheck(now);
                Publish(s => s with { LastChecked = now });
            }
            _logger.LogInformation("Update check ({Mode}): {Outcome} {Version}", automatic ? "automatic" : "manual", result.Outcome, result.Release?.Version);
            switch (result.Outcome)
            {
                case UpdateCheckOutcome.UpToDate:
                    Publish(s => s with { Stage = UpdateStage.UpToDate, Release = null, InstallerPath = null });
                    break;
                case UpdateCheckOutcome.Available:
                    var release = result.Release!;
                    if (previous is { Stage: UpdateStage.Ready, InstallerPath: { } path } && Equals(previous.Release?.Version, release.Version) && File.Exists(path))
                        Publish(s => s with { Stage = UpdateStage.Ready, Release = release });
                    else if (automatic)
                        await DownloadCoreAsync(release, automatic: true, ct).ConfigureAwait(false);
                    else
                        Publish(s => s with { Stage = UpdateStage.Available, Release = release, InstallerPath = null });
                    break;
                default:
                    Publish(s => s with
                    {
                        Stage = UpdateStage.Failed,
                        Failure = result.Outcome switch
                        {
                            UpdateCheckOutcome.Offline => UpdateFailure.Offline,
                            UpdateCheckOutcome.RateLimited => UpdateFailure.RateLimited,
                            UpdateCheckOutcome.NotFound => UpdateFailure.NotFound,
                            _ => UpdateFailure.Other,
                        },
                    });
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _busy.Release(); }
    }

    private async Task<string?> DownloadCoreAsync(UpdateRelease release, bool automatic, CancellationToken ct)
    {
        Publish(s => s with { Stage = UpdateStage.Downloading, Release = release, Received = 0, Total = release.Installer.Size, Failure = UpdateFailure.None, InstallerPath = null, InstallerSha256 = null });
        try
        {
            var (path, sha256) = await _client.DownloadVerifiedAsync(release, new Callback(p => Publish(s => s with { Received = p.Received, Total = p.Total })), ct).ConfigureAwait(false);
            Publish(s => s with { Stage = UpdateStage.Ready, Release = release, Received = release.Installer.Size, InstallerPath = path, InstallerSha256 = sha256 });
            _logger.LogInformation("Update {Version} downloaded and verified", release.Version);
            bool announce;
            lock (_gate) announce = automatic && _announced.Add(release.Version);
            if (announce) UpdateReady?.Invoke(this, release);
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning("Update download failed ({Reason}: {Message})", ex.GetType().Name, ex.Message);
            Publish(s => s with { Stage = UpdateStage.Failed, Failure = Classify(ex), Received = 0 });
            return null;
        }
    }

    /// <summary>
    /// Starts the verified installer silently. The caller then shuts the app down; the installer closes
    /// anything left, installs over the current version and starts EncoDeck again.
    /// </summary>
    /// <remarks>
    /// The file sits in a user-writable folder, possibly for days, so right before it starts it is hashed again against
    /// the SHA-256 it was downloaded with, and its Authenticode signer must match the running app's (when the running
    /// app is validly signed; development builds and old unsigned installs skip that comparison). The file is held
    /// open without write or delete sharing from the hash until the installer has started, so the file checked is
    /// the file run. A refusal is a <see cref="UpdateFailure.Verification"/> failure.
    /// </remarks>
    public bool TryStartInstaller()
    {
        var status = Status;
        if (status is not { Stage: UpdateStage.Ready, InstallerPath: { } path } || !File.Exists(path)) return false;
        string? refusal;
        try
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                refusal = status.InstallerSha256 is not { } expected
                    ? "no SHA-256 is known for it"
                    : Convert.ToHexStringLower(SHA256.HashData(file)) != expected
                        ? "it changed after it was downloaded and verified"
                        : CheckPublisher(path);
                if (refusal is null)
                {
                    string log = Path.Combine(_client.DownloadDirectory, "install.log");
                    StartProcess(new System.Diagnostics.ProcessStartInfo(path)
                    {
                        Arguments = $"{InstallerArguments} /LOG=\"{log}\"",
                        WorkingDirectory = _client.DownloadDirectory,
                        // Shell execution so a UAC prompt can appear if Windows decides the installer needs one.
                        UseShellExecute = true,
                    });
                }
            }
            if (refusal is null)
            {
                Publish(s => s with { Stage = UpdateStage.Installing });
                _logger.LogInformation("Started the installer for {Version}", status.Release?.Version);
                return true;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not start the installer ({Reason})", ex.GetType().Name);
            Publish(s => s with { Stage = UpdateStage.Failed, Failure = UpdateFailure.Storage });
            return false;
        }
        _logger.LogWarning("Refused to start the installer for {Version}: {Reason}", status.Release?.Version, refusal);
        // A file that is not what was published is not kept around (and the failure text says it was deleted). The
        // release stays offered: downloading again fetches a fresh copy, which is checked the same way.
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogDebug("Could not delete the refused installer ({Reason})", ex.GetType().Name); }
        Publish(s => s with { Stage = UpdateStage.Failed, Failure = UpdateFailure.Verification, InstallerPath = null, InstallerSha256 = null });
        return false;
    }

    /// <summary>Null when the installer may start; otherwise why not.</summary>
    private string? CheckPublisher(string installer)
    {
        var self = _selfSignature.Value;
        if (self.State != SignatureState.Valid || string.IsNullOrEmpty(self.SignerName))
        {
            // No publisher to compare with: the SHA-256 published with the release is all there is.
            _logger.LogInformation("EncoDeck itself is not validly signed ({State}, 0x{Status:X8}); not comparing the installer's publisher", self.State, self.Status);
            return null;
        }
        var theirs = _signatures.Verify(installer);
        if (theirs.State != SignatureState.Valid)
            return $"its signature is not valid ({theirs.State}, 0x{theirs.Status:X8})";
        if (!string.Equals(theirs.SignerName, self.SignerName, StringComparison.Ordinal))
            return $"it is signed by \"{theirs.SignerName}\", not by \"{self.SignerName}\"";
        return null;
    }

    internal static UpdateFailure Classify(Exception exception) => exception switch
    {
        UpdateVerificationException => UpdateFailure.Verification,
        HttpRequestException { StatusCode: null } or TimeoutException or OperationCanceledException => UpdateFailure.Offline,
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => UpdateFailure.NotFound,
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => UpdateFailure.RateLimited,
        IOException or UnauthorizedAccessException => UpdateFailure.Storage,
        _ => UpdateFailure.Other,
    };

    private void Publish(Func<UpdateStatus, UpdateStatus> change)
    {
        UpdateStatus next;
        lock (_gate)
        {
            if (_disposed) return;
            next = _status = change(_status);
        }
        try { StatusChanged?.Invoke(this, next); }
        catch (Exception ex) { _logger.LogWarning(ex, "Update status handler failed"); }
    }

    private DateTimeOffset? ReadLastCheck()
    {
        try
        {
            return File.Exists(_stateFile) && DateTimeOffset.TryParse(File.ReadAllText(_stateFile).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
                ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void WriteLastCheck(DateTimeOffset value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, value.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogDebug("Could not save the update check time ({Reason})", ex.GetType().Name); }
    }

    private sealed class Callback(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            loop = _loop;
        }
        await _shutdown.CancelAsync();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (Exception) { /* Logged by the loop. */ }
        }
        // A user-started check or download observes the same token; wait for it to let go of the files.
        if (await _busy.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)) _busy.Release();
        _client.Dispose();
        _shutdown.Dispose();
    }
}
