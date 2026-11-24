// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Logging;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Tests.Platform;

public sealed class JsonFileLoggerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("encodeck-log-test").FullName;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A clock in UTC that moves only when told to; delays still use real time.</summary>
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Set(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static readonly DateTimeOffset Noon = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private string[] Files() => [.. Directory.GetFiles(_root, "encodeck-*.log").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    /// <summary>Read as the diagnostics export does, while the logger still has the file open.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50, Ct);
        Assert.True(condition());
    }

    [Fact]
    public void FileNamesOfADaySortBetweenTheDays()
    {
        var day = new DateOnly(2026, 11, 24);
        Assert.Equal("encodeck-20261124.log", JsonFileLoggerProvider.FileName(day, 1));
        Assert.Equal("encodeck-20261124.part02.log", JsonFileLoggerProvider.FileName(day, 2));
        string[] newestFirst =
        [
            JsonFileLoggerProvider.FileName(day.AddDays(1), 1),
            JsonFileLoggerProvider.FileName(day, 10),
            JsonFileLoggerProvider.FileName(day, 3),
            JsonFileLoggerProvider.FileName(day, 2),
            JsonFileLoggerProvider.FileName(day, 1),
            JsonFileLoggerProvider.FileName(day.AddDays(-1), 2),
        ];
        // The diagnostics export lists encodeck-*.log newest first by name with the default (culture) comparer.
        foreach (var comparer in new IComparer<string>[] { StringComparer.Ordinal, Comparer<string>.Default, StringComparer.InvariantCulture })
            Assert.Equal(newestFirst, newestFirst.Reverse().OrderByDescending(n => n, comparer));
    }

    [Fact]
    public void ADayThatOutgrowsTheCapContinuesInNumberedParts()
    {
        var clock = new Clock(Noon);
        var provider = new JsonFileLoggerProvider(_root, LogLevel.Information, clock, maxFileBytes: 2000, retryDelay: TimeSpan.FromMilliseconds(50));
        var logger = provider.CreateLogger("test");
        for (int i = 0; i < 60; i++) logger.LogInformation("line {Index} {Padding}", i, new string('x', 60));
        provider.Dispose();

        string[] files = Files();
        Assert.True(files.Length >= 3, string.Join(", ", files));
        Assert.Equal("encodeck-20261124.log", files[0]);
        Assert.All(files.Skip(1), f => Assert.Matches(@"^encodeck-20261124\.part\d{2}\.log$", f));
        Assert.All(files, f => Assert.InRange(new FileInfo(Path.Combine(_root, f)).Length, 1, 2000));
        // Nothing is lost, in order across the parts.
        var lines = files.OrderBy(f => f == "encodeck-20261124.log" ? "" : f, StringComparer.Ordinal)
            .SelectMany(f => File.ReadAllLines(Path.Combine(_root, f))).ToList();
        Assert.Equal(60, lines.Count);
        for (int i = 0; i < 60; i++) Assert.Contains($"\"Index\":\"{i}\"", lines[i]);
        Assert.Equal(Path.Combine(_root, files[^1]), provider.CurrentFilePath);

        // A restart the same day goes on in the newest part (or the next one), never back in the first file.
        long first = new FileInfo(Path.Combine(_root, files[0])).Length;
        var again = new JsonFileLoggerProvider(_root, LogLevel.Information, clock, maxFileBytes: 2000, retryDelay: TimeSpan.FromMilliseconds(50));
        again.CreateLogger("test").LogInformation("after restart");
        again.Dispose();
        Assert.Equal(first, new FileInfo(Path.Combine(_root, files[0])).Length);
        Assert.Contains("after restart", File.ReadAllText(again.CurrentFilePath));
        Assert.NotEqual(Path.Combine(_root, files[0]), again.CurrentFilePath);
    }

    [Fact]
    public async Task AFailedWriteIsSurvived_TheFileReopened_AndTheLossNoted()
    {
        var clock = new Clock(Noon);
        // A folder where the day's file should be: opening it fails with UnauthorizedAccessException.
        string blocked = Path.Combine(_root, "encodeck-20261124.log");
        Directory.CreateDirectory(blocked);
        var provider = new JsonFileLoggerProvider(_root, LogLevel.Information, clock, maxFileBytes: 1 << 20, retryDelay: TimeSpan.FromMilliseconds(300));
        var logger = provider.CreateLogger("test");
        logger.LogInformation("dropped");
        await WaitForAsync(() => provider.WriteFailures > 0);

        Directory.Delete(blocked);
        logger.LogInformation("written");
        await WaitForAsync(() => File.Exists(blocked) && ReadShared(blocked).Contains("written"));
        provider.Dispose();

        string text = File.ReadAllText(blocked);
        Assert.DoesNotContain("\"msg\":\"dropped\"", text);
        Assert.Contains("lines were lost", text);
        Assert.Contains("UnauthorizedAccessException", text);
    }

    [Fact]
    public async Task OldDaysArePrunedAtRolloverToo()
    {
        var today = new DateOnly(2026, 11, 24);
        // Seven days of old logs (the oldest with a second part), and a file that is not one of ours.
        for (int d = 4; d <= 10; d++) File.WriteAllText(Path.Combine(_root, JsonFileLoggerProvider.FileName(today.AddDays(-d), 1)), "{}\n");
        File.WriteAllText(Path.Combine(_root, JsonFileLoggerProvider.FileName(today.AddDays(-10), 2)), "{}\n");
        File.WriteAllText(Path.Combine(_root, "encodeck-notes.log"), "keep");

        var clock = new Clock(Noon.AddDays(-1));
        var provider = new JsonFileLoggerProvider(_root, LogLevel.Information, clock);
        Assert.Equal(9, Files().Length); // Seven days at start-up: nothing to prune yet.
        var logger = provider.CreateLogger("test");
        logger.LogInformation("yesterday");
        string yesterday = Path.Combine(_root, JsonFileLoggerProvider.FileName(today.AddDays(-1), 1));
        await WaitForAsync(() => File.Exists(yesterday));

        // The session runs past midnight: the new day's file opens and the oldest days go.
        clock.Set(Noon);
        logger.LogInformation("today");
        provider.Dispose();

        string[] files = Files();
        Assert.Contains(JsonFileLoggerProvider.FileName(today, 1), files);
        Assert.Contains(JsonFileLoggerProvider.FileName(today.AddDays(-1), 1), files);
        Assert.DoesNotContain(JsonFileLoggerProvider.FileName(today.AddDays(-9), 1), files);
        Assert.DoesNotContain(JsonFileLoggerProvider.FileName(today.AddDays(-10), 1), files);
        Assert.DoesNotContain(JsonFileLoggerProvider.FileName(today.AddDays(-10), 2), files);
        Assert.Contains(JsonFileLoggerProvider.FileName(today.AddDays(-8), 1), files);
        Assert.Contains("encodeck-notes.log", files);
        Assert.Equal(8, files.Length);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
