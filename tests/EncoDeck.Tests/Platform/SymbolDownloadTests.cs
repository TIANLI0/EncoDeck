// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http.Headers;
using EncoDeck.Platform.LeAudioCodec;

namespace EncoDeck.Tests.Platform;

public sealed class SymbolDownloadTests : IDisposable
{
    private const string Url = "https://symbols.test/x.pdb/KEY/x.pdb";
    private readonly string _directory = Directory.CreateTempSubdirectory("encodeck-pdb-").FullName;
    private readonly byte[] _content = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

    private string PdbPath => Path.Combine(_directory, "x.pdb");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task SlowButSteadyDownloadIsNotCutOff()
    {
        // Takes far longer than the stall timeout, but data keeps coming.
        var server = new FakeServer(_content) { ChunkDelay = TimeSpan.FromMilliseconds(30), ChunkSize = 20_000 };
        using var http = new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan };
        Assert.True(await LeAudioHookTargets.DownloadAsync(http, Url, PdbPath, TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken));
        Assert.Equal(_content, await File.ReadAllBytesAsync(PdbPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StalledDownloadResumesWhereItStopped()
    {
        // Each connection sends 100 kB and then goes quiet.
        var server = new FakeServer(_content) { StallAfter = 100_000 };
        using var http = new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan };
        Assert.True(await LeAudioHookTargets.DownloadAsync(http, Url, PdbPath, TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken));
        Assert.Equal(_content, await File.ReadAllBytesAsync(PdbPath, TestContext.Current.CancellationToken));
        Assert.Equal([0L, 100_000L, 200_000L], server.RangeStarts);
        Assert.False(File.Exists(PdbPath + ".partial"));
    }

    [Fact]
    public async Task PartKeptByAnEarlierCallIsResumed()
    {
        await File.WriteAllBytesAsync(PdbPath + ".partial", _content[..120_000], TestContext.Current.CancellationToken);
        var server = new FakeServer(_content);
        using var http = new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan };
        Assert.True(await LeAudioHookTargets.DownloadAsync(http, Url, PdbPath, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(_content, await File.ReadAllBytesAsync(PdbPath, TestContext.Current.CancellationToken));
        Assert.Equal([120_000L], server.RangeStarts);
    }

    [Fact]
    public async Task ServerWithoutRangesStartsOver()
    {
        await File.WriteAllBytesAsync(PdbPath + ".partial", new byte[50_000], TestContext.Current.CancellationToken);
        var server = new FakeServer(_content) { IgnoreRange = true };
        using var http = new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan };
        Assert.True(await LeAudioHookTargets.DownloadAsync(http, Url, PdbPath, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(_content, await File.ReadAllBytesAsync(PdbPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingSymbolsFailWithoutRetrying()
    {
        var server = new FakeServer(_content) { Status = HttpStatusCode.NotFound };
        using var http = new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan };
        Assert.False(await LeAudioHookTargets.DownloadAsync(http, Url, PdbPath, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Single(server.RangeStarts);
        Assert.False(File.Exists(PdbPath));
    }

    [Fact]
    public async Task ServerThatNeverSendsGivesUpAndKeepsNothingBroken()
    {
        var server = new FakeServer(_content) { StallAfter = 0 };
        using var http = new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan };
        Assert.False(await LeAudioHookTargets.DownloadAsync(http, Url, PdbPath, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(PdbPath));
    }

    private sealed class FakeServer(byte[] content) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool IgnoreRange { get; init; }
        public int? StallAfter { get; init; }
        public int ChunkSize { get; init; } = 16_384;
        public TimeSpan ChunkDelay { get; init; }
        public List<long> RangeStarts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            long from = IgnoreRange ? 0 : request.Headers.Range?.Ranges.First().From ?? 0;
            lock (RangeStarts) RangeStarts.Add(from);
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
            var body = new SlowStream(content, (int)from, StallAfter, ChunkSize, ChunkDelay);
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(body),
            };
            response.Content.Headers.ContentLength = content.Length - from;
            if (from > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, content.Length - 1, content.Length);
            return Task.FromResult(response);
        }
    }

    /// <summary>Serves content from an offset in chunks; after <c>stallAfter</c> bytes it blocks until cancelled.</summary>
    private sealed class SlowStream(byte[] content, int position, int? stallAfter, int chunkSize, TimeSpan chunkDelay) : Stream
    {
        private int _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position >= content.Length) return 0;
            if (stallAfter is { } limit && _sent >= limit) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (chunkDelay > TimeSpan.Zero) await Task.Delay(chunkDelay, cancellationToken);
            int count = Math.Min(Math.Min(buffer.Length, chunkSize), content.Length - position);
            if (stallAfter is { } cap) count = Math.Min(count, cap - _sent);
            content.AsMemory(position, count).CopyTo(buffer);
            position += count;
            _sent += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
