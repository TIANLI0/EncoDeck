// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Platform.Logging;

/// <summary>
/// Structured JSON-lines file logger. Writes happen on a background task so logging never blocks
/// Bluetooth callbacks or the UI thread. All text is passed through <see cref="LogRedactor"/>.
/// Files roll daily (<c>encodeck-yyyyMMdd.log</c>); a day that outgrows <see cref="MaxFileBytes"/> continues in
/// <c>encodeck-yyyyMMdd.part02.log</c>, <c>.part03</c>… Only the files of the newest <see cref="RetainedFiles"/> days are kept.
/// </summary>
/// <remarks>
/// The session may last weeks, so a write error (disk full, file locked by a scanner, folder deleted) never ends file
/// logging: the failed batch is dropped, the file is reopened after a short delay, and a line in the log says how many
/// lines were lost. Memory stays bounded throughout: the queue drops its oldest lines when full.
/// <para>
/// Continuation names sort after the day's first file and before the next day's, both ordinally and with the
/// culture's comparer, so a newest-first listing of <c>encodeck-*.log</c> by name (the diagnostics export) starts
/// with the newest part.
/// </para>
/// </remarks>
public sealed partial class JsonFileLoggerProvider : ILoggerProvider
{
    /// <summary>Days of log files kept (a day may have several parts).</summary>
    public const int RetainedFiles = 7;

    /// <summary>Size at which a day's log continues in its next part.</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>Parts per day (the first file counts as part 1); beyond them the day's further lines are dropped.</summary>
    public const int MaxPartsPerDay = 10;

    private const int QueueCapacity = 4096;
    private const int MaxBatch = 512;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly TimeProvider _time;
    private readonly long _maxFileBytes;
    private readonly TimeSpan _retryDelay;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    // Cancels only the wait before reopening a failed file, so shutdown is not held up by it.
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _writer;

    // Writer-task state.
    private StreamWriter? _file;
    private DateOnly _fileDay;
    private int _filePart;
    private long _fileBytes;
    private DateOnly _fullDay;
    private volatile OpenFile? _current;
    private int _writeFailures;

    private sealed record OpenFile(DateOnly Day, string Path);

    public JsonFileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information, TimeProvider? time = null)
        : this(directory, minimumLevel, time, MaxFileBytes, DefaultRetryDelay)
    {
    }

    /// <summary>Tests: a small size cap and a short retry delay.</summary>
    internal JsonFileLoggerProvider(string directory, LogLevel minimumLevel, TimeProvider? time, long maxFileBytes, TimeSpan retryDelay)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        _time = time ?? TimeProvider.System;
        _maxFileBytes = maxFileBytes;
        _retryDelay = retryDelay;
        Directory.CreateDirectory(directory);
        PruneOldFiles();
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>The file today's lines go to (the part being written, once one is open).</summary>
    public string CurrentFilePath
    {
        get
        {
            var today = Today();
            return _current is { } open && open.Day == today ? open.Path : Path.Combine(_directory, FileName(today, 1));
        }
    }

    /// <summary>How often writing a batch failed (tests).</summary>
    internal int WriteFailures => Volatile.Read(ref _writeFailures);

    /// <summary><c>encodeck-20261124.log</c>, then <c>encodeck-20261124.part02.log</c>…</summary>
    internal static string FileName(DateOnly day, int part) =>
        part <= 1
            ? string.Create(CultureInfo.InvariantCulture, $"encodeck-{day:yyyyMMdd}.log")
            : string.Create(CultureInfo.InvariantCulture, $"encodeck-{day:yyyyMMdd}.part{part:00}.log");

    [GeneratedRegex(@"^encodeck-(?<day>[0-9]{8})(?:\.part(?<part>[0-9]{2}))?\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogFileName();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _stopping.Cancel();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Logging must never crash shutdown.
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        var batch = new List<string>(MaxBatch);
        long lost = 0;
        string? lostReason = null;
        var delay = _retryDelay;
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (batch.Count < MaxBatch && _queue.Reader.TryRead(out var line)) batch.Add(line);
                try
                {
                    if (lost > 0)
                    {
                        await WriteLineAsync(LostLine(lost, lostReason)).ConfigureAwait(false);
                        lost = 0;
                    }
                    foreach (var line in batch) await WriteLineAsync(line).ConfigureAwait(false);
                    batch.Clear();
                    if (_file is not null) await _file.FlushAsync().ConfigureAwait(false);
                    delay = _retryDelay;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Disk full / file locked / folder gone: drop this batch (some of it may have been written),
                    // reopen after a pause and go on. The queue keeps filling meanwhile, bounded.
                    Interlocked.Increment(ref _writeFailures);
                    lost += batch.Count;
                    lostReason = ex.GetType().Name;
                    batch.Clear();
                    await CloseFileAsync().ConfigureAwait(false);
                    if (_stopping.IsCancellationRequested) break;
                    try { await Task.Delay(delay, _time, _stopping.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
                }
            }
        }
        finally
        {
            await CloseFileAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Appends a line to the day's current part, opening, rolling over to the next day or part as needed.</summary>
    private async Task WriteLineAsync(string line)
    {
        var day = Today();
        if (day == _fullDay) return;
        if (_file is null || day != _fileDay)
        {
            bool newDay = _fileDay != default && day != _fileDay;
            await CloseFileAsync().ConfigureAwait(false);
            bool opened = OpenLatestPart(day);
            // A long session would otherwise keep every day since start-up.
            if (newDay) PruneOldFiles();
            if (!opened) return;
        }
        long bytes = Encoding.UTF8.GetByteCount(line) + _file!.NewLine.Length;
        if (_fileBytes > 0 && _fileBytes + bytes > _maxFileBytes)
        {
            await CloseFileAsync().ConfigureAwait(false);
            if (!OpenPart(day, _filePart + 1)) return;
        }
        await _file!.WriteLineAsync(line).ConfigureAwait(false);
        _fileBytes += bytes;
    }

    /// <summary>The day's highest existing part (after a restart, or after a write error), or part 1.</summary>
    private bool OpenLatestPart(DateOnly day)
    {
        int part = 1;
        string prefix = FileName(day, 1)[..^".log".Length];
        if (Directory.Exists(_directory))
        {
            foreach (string path in Directory.EnumerateFiles(_directory, prefix + "*.log"))
            {
                if (LogFileName().Match(Path.GetFileName(path)) is { Success: true } m && m.Groups["part"].Success
                    && int.TryParse(m.Groups["part"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int found))
                    part = Math.Max(part, found);
            }
        }
        while (OpenPart(day, part))
        {
            if (_fileBytes < _maxFileBytes) return true;
            CloseFileSync();
            part++;
        }
        return false;
    }

    /// <summary>Opens (appends to) a part; false when the day has used all its parts.</summary>
    private bool OpenPart(DateOnly day, int part)
    {
        if (part > MaxPartsPerDay)
        {
            _fullDay = day;
            return false;
        }
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, FileName(day, part));
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _file = new StreamWriter(stream, new UTF8Encoding(false));
        _fileDay = day;
        _filePart = part;
        _fileBytes = stream.Length;
        _current = new OpenFile(day, path);
        return true;
    }

    private async Task CloseFileAsync()
    {
        var file = _file;
        _file = null;
        if (file is null) return;
        try { await file.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void CloseFileSync()
    {
        var file = _file;
        _file = null;
        try { file?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string LostLine(long count, string? reason)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("t", _time.GetLocalNow().ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("lvl", "WRN");
            json.WriteString("cat", typeof(JsonFileLoggerProvider).FullName);
            json.WriteString("msg", string.Create(CultureInfo.InvariantCulture, $"The log file could not be written ({reason}); up to {count} lines were lost"));
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Deletes the files (all parts) of days older than the newest <see cref="RetainedFiles"/> days.</summary>
    private void PruneOldFiles()
    {
        try
        {
            var files = new List<(FileInfo File, string Day)>();
            foreach (var file in new DirectoryInfo(_directory).GetFiles("encodeck-*.log"))
            {
                if (LogFileName().Match(file.Name) is { Success: true } m) files.Add((file, m.Groups["day"].Value));
            }
            var keep = files.Select(f => f.Day).Distinct(StringComparer.Ordinal)
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .Take(RetainedFiles)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var (file, day) in files)
            {
                if (keep.Contains(day)) continue;
                try { file.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(JsonFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("t", provider._time.GetLocalNow().ToString("O"));
                json.WriteString("lvl", ShortLevel(logLevel));
                json.WriteString("cat", category);
                if (eventId.Id != 0) json.WriteNumber("evt", eventId.Id);
                json.WriteString("msg", LogRedactor.Redact(formatter(state, exception)));
                if (state is IReadOnlyList<KeyValuePair<string, object?>> props)
                {
                    foreach (var (key, value) in props)
                    {
                        if (key == "{OriginalFormat}") continue;
                        json.WriteString(key, LogRedactor.Redact(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
                    }
                }
                if (exception is not null) json.WriteString("ex", LogRedactor.Redact(exception.ToString()));
                json.WriteEndObject();
            }
            provider.Enqueue(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }

        private static string ShortLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
