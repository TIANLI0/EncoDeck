// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using EncoDeck.Platform.A2dpBridge;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Lhdc;
using EncoDeck.Platform.Updates;

namespace EncoDeck.Tests.Platform;

public sealed class ComponentInstallerTests : IDisposable
{
    private const string Commit = "b3da30e72f5a43c634383f81b95b8ab0dd1f8f88";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EncoDeck-Components-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuiltInManifestPinsEveryComponent()
    {
        var lhdc = ComponentCatalog.Lhdc;
        Assert.Equal("https://github.com/Wayne-u/LHDC-Win", lhdc.Source.Repository);
        Assert.StartsWith("lhdc-v", lhdc.Tag);
        Assert.NotNull(lhdc.PackageFor(System.Runtime.InteropServices.Architecture.X64));
        var bridge = ComponentCatalog.A2dpBridge;
        Assert.StartsWith("a2dpwb-v", bridge.Tag);
        Assert.True(bridge.UpstreamDownloads);
        foreach (var package in bridge.Packages)
        {
            Assert.True(package.IsPublished);
            // Downloaded from the upstream release only, held to the same pin.
            Assert.NotEmpty(package.Mirrors);
            Assert.All(bridge.Sources(package), m => Assert.StartsWith("https://github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge/releases/download/", m.AbsoluteUri));
        }
        Assert.Equal(new Uri($"https://github.com/TIANLI0/EncoDeck/releases/download/{lhdc.Tag}/{lhdc.Packages[0].File}"), lhdc.Sources(lhdc.Packages[0])[0]);
        // Component tags never parse as app versions, so the app's update check cannot pick them.
        Assert.All(ComponentCatalog.Components, c => Assert.False(SemanticVersion.TryParse(c.Tag, out _)));
    }

    [Theory]
    [InlineData("""{"schema":2,"components":[]}""")]
    [InlineData("""{"schema":1,"components":[{"id":"x","version":"1.0.0-r1","tag":"x-v1.0.0-r1","source":{"repository":"r","commit":"abc","release":"1"},"packages":[]}]}""")]
    [InlineData("""{"schema":1,"components":[{"id":"x","version":"1.0.0-r1","tag":"x-v1.0.0-r1","source":{"repository":"r","commit":"b3da30e72f5a43c634383f81b95b8ab0dd1f8f88","release":"1"},"packages":[{"architecture":"x64","file":"../a.zip","size":1,"sha256":""}]}]}""")]
    [InlineData("""{"schema":1,"components":[{"id":"x","version":"1.0.0-r1","tag":"x-v1.0.0-r1","source":{"repository":"r","commit":"b3da30e72f5a43c634383f81b95b8ab0dd1f8f88","release":"1"},"packages":[{"architecture":"x64","file":"a.zip","size":1,"sha256":"","mirrors":["http://example.com/a.zip"]}]}]}""")]
    // A version without the build revision, and a tag that is not <id>-v<version>.
    [InlineData("""{"schema":1,"components":[{"id":"x","version":"1.0.0","tag":"x-v1.0.0","source":{"repository":"r","commit":"b3da30e72f5a43c634383f81b95b8ab0dd1f8f88","release":"1"},"packages":[]}]}""")]
    [InlineData("""{"schema":1,"components":[{"id":"x","version":"1.0.0-r1","tag":"y-v1.0.0-r1","source":{"repository":"r","commit":"b3da30e72f5a43c634383f81b95b8ab0dd1f8f88","release":"1"},"packages":[]}]}""")]
    public void RejectsMalformedManifests(string json) => Assert.Throws<InvalidDataException>(() => ComponentCatalog.Parse(json));

    [Fact]
    public async Task InstallsVerifiedPackageOnceAndReusesItWithoutNetwork()
    {
        byte[] zip = LhdcPackage("0.8.8-r1");
        var http = new FakeHttp(zip);
        var installer = Lhdc(http, zip, "0.8.8-r1");
        Assert.Null(installer.Installed);
        await installer.InstallAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal("0.8.8-r1", installer.Installed?.Version);
        Assert.True(installer.IsCurrent);
        int requests = http.Requests;
        await installer.InstallAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(requests, http.Requests);
        // The download is removed after unpacking: no second copy on disk.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "downloads")));
    }

    [Fact]
    public async Task WrongBytesFromTheReleaseFallBackToTheMirror()
    {
        byte[] zip = LhdcPackage("0.8.8-r1");
        var http = new FakeHttp(zip) { Corrupt = uri => uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/TIANLI0/", StringComparison.Ordinal) };
        var installer = Lhdc(http, zip, "0.8.8-r1", mirror: new Uri("https://mirror.example/lhdc.zip"));
        await installer.InstallAsync(ct: TestContext.Current.CancellationToken);
        Assert.NotNull(installer.Installed);
        Assert.Contains(http.Hosts, h => h == "mirror.example");
    }

    [Fact]
    public async Task MissingReleaseAssetMovesOnWithoutRetrying()
    {
        byte[] zip = LhdcPackage("0.8.8-r1");
        var http = new FakeHttp(zip) { Status = uri => uri.Host == "github.com" ? HttpStatusCode.NotFound : HttpStatusCode.OK };
        var installer = Lhdc(http, zip, "0.8.8-r1", mirror: new Uri("https://mirror.example/lhdc.zip"));
        await installer.InstallAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, http.Hosts.Count(h => h == "github.com"));
    }

    [Fact]
    public async Task BytesOfABrokenSourceNeverReachTheNextOne()
    {
        byte[] zip = LhdcPackage("0.8.8-r1");
        // The release sends wrong bytes and drops three times; the mirror must start from zero, not resume on them.
        var http = new FakeHttp(zip) { Broken = uri => uri.Host == "github.com" };
        var installer = Lhdc(http, zip, "0.8.8-r1", mirror: new Uri("https://mirror.example/lhdc.zip"));
        await installer.InstallAsync(ct: TestContext.Current.CancellationToken);
        Assert.NotNull(installer.Installed);
        Assert.Contains(http.Hosts, h => h == "mirror.example");
    }

    [Fact]
    public async Task CorruptEverywhereLeavesNothingActive()
    {
        byte[] zip = LhdcPackage("0.8.8-r1");
        var http = new FakeHttp(zip) { Corrupt = _ => true };
        var installer = Lhdc(http, zip, "0.8.8-r1");
        await Assert.ThrowsAsync<UpdateVerificationException>(() => installer.InstallAsync(ct: TestContext.Current.CancellationToken));
        Assert.Null(installer.Installed);
        Assert.Empty(Directory.GetFiles(_root, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DroppedConnectionResumesWithARangeRequest()
    {
        byte[] zip = LhdcPackage("0.8.8-r1");
        var http = new FakeHttp(zip) { CutAfter = zip.Length / 2 };
        var installer = Lhdc(http, zip, "0.8.8-r1");
        await installer.InstallAsync(ct: TestContext.Current.CancellationToken);
        Assert.NotNull(installer.Installed);
        Assert.Contains(http.Ranges, r => r == zip.Length / 2);
    }

    [Fact]
    public async Task PackageFromAnotherCommitIsRejected()
    {
        byte[] zip = LhdcPackage("0.8.8-r1", commit: new string('c', 40));
        var installer = Lhdc(new FakeHttp(zip), zip, "0.8.8-r1");
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(ct: TestContext.Current.CancellationToken));
        Assert.Null(installer.Installed);
        Assert.Empty(Directory.GetDirectories(_root, ".install-*"));
    }

    [Fact]
    public async Task IncompletePackageIsRejectedAndCanBeRetried()
    {
        byte[] zip = LhdcPackage("0.8.8-r1", incomplete: true);
        var installer = Lhdc(new FakeHttp(zip), zip, "0.8.8-r1");
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(ct: TestContext.Current.CancellationToken));
        Assert.Null(installer.Installed);
    }

    [Fact]
    public async Task AnUpdateReplacesTheInstalledVersionAndLegacyLayoutsGo()
    {
        // 1.10.0's layout (Components\LHDC\<app version>\<digest>) and the deployment results.
        Directory.CreateDirectory(Path.Combine(_root, "1.10.0", new string('a', 64)));
        Directory.CreateDirectory(Path.Combine(_root, LhdcComponent.ResultsFolder));
        byte[] first = LhdcPackage("0.8.8-r1");
        await Lhdc(new FakeHttp(first), first, "0.8.8-r1").InstallAsync(ct: TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(Path.Combine(_root, "1.10.0")));
        Assert.True(Directory.Exists(Path.Combine(_root, LhdcComponent.ResultsFolder)));

        // An app update that keeps the old pin still uses the installed component.
        var older = Lhdc(new FakeHttp([]), LhdcPackage("0.8.9-r1"), "0.8.9-r1");
        Assert.Equal("0.8.8-r1", older.Installed?.Version);
        Assert.False(older.IsCurrent);

        byte[] second = LhdcPackage("0.8.9-r1");
        byte[] third = LhdcPackage("0.9.0-r1");
        await Lhdc(new FakeHttp(second), second, "0.8.9-r1").InstallAsync(ct: TestContext.Current.CancellationToken);
        await Lhdc(new FakeHttp(third), third, "0.9.0-r1").InstallAsync(ct: TestContext.Current.CancellationToken);
        // No older version is kept once an update is in place.
        var versions = Directory.GetDirectories(_root).Select(Path.GetFileName).Where(n => n!.Contains("-r1-")).ToArray();
        Assert.Equal(["0.9.0-r1-"], versions.Select(v => v![..9]));
        Assert.True(Directory.Exists(Path.Combine(_root, LhdcComponent.ResultsFolder)));
    }

    [Fact]
    public async Task AnUpdateFoundOnlineInstallsWithItsOwnDigestAndOutranksAnOlderPin()
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] pinned = LhdcPackage("0.8.8-r1");
        byte[] newer = LhdcPackage("0.8.8-r2");
        var installer = Lhdc(new FakeHttp(pinned), pinned, "0.8.8-r1");
        await installer.InstallAsync(ct: ct);
        var online = installer.Release with
        {
            Version = "0.8.8-r2", Tag = "lhdc-v0.8.8-r2",
            Packages = [Package(newer, "EncoDeck-LHDC-0.8.8-r2-win-x64.zip")],
        };
        Assert.Same(online, installer.Newest(online));
        Assert.Same(online, installer.UpdateTarget(online));
        var updater = new ComponentInstaller(new HttpClient(new FakeHttp(newer)), _root, installer.Release, installer.Package, LhdcComponent.Layout)
        {
            KeepFolders = [LhdcComponent.ResultsFolder],
        };
        await updater.InstallAsync(online, null, ct);
        Assert.Equal("0.8.8-r2", updater.Installed?.Version);
        // Installed beyond the pin: nothing to update, and the older pin is never offered as a downgrade.
        Assert.True(updater.IsCurrent);
        Assert.Null(updater.UpdateTarget(null));
        Assert.Null(updater.UpdateTarget(online));
        Assert.Single(Directory.GetDirectories(_root), d => Path.GetFileName(d).StartsWith("0.8.8-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UninstallRemovesEverythingButNotWhileInUse()
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] zip = LhdcPackage("0.8.8-r1");
        var installer = Lhdc(new FakeHttp(zip), zip, "0.8.8-r1");
        var installed = await installer.InstallAsync(ct: ct);
        using (File.Open(Path.Combine(installed.Directory, "backend", "lhdc-host.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(installer.Uninstall());
            Assert.NotNull(installer.Installed);
        }
        Assert.True(installer.Uninstall());
        Assert.False(Directory.Exists(_root));
        Assert.Null(installer.Installed);
    }

    [Theory]
    [InlineData("0.8.8-r2", "0.8.8-r1", true)]
    [InlineData("0.8.8-r10", "0.8.8-r9", true)]
    [InlineData("0.8.10-r1", "0.8.9-r4", true)]
    [InlineData("0.8.8-r1", "0.8.8-r1", false)]
    [InlineData("0.8.8-r1", "0.9.0-r1", false)]
    [InlineData("0.8.8", null, false)]
    [InlineData("1.0.0-r1", null, true)]
    // Upstream pre-releases: before their release, after the version below, ordered as SemVer.
    [InlineData("2.0.2-beta.10-r1", "2.0.1-r1", true)]
    [InlineData("2.0.2-beta.10-r1", "2.0.2-beta.9-r3", true)]
    [InlineData("2.0.2-beta.10-r2", "2.0.2-beta.10-r1", true)]
    [InlineData("2.0.2-r1", "2.0.2-beta.10-r4", true)]
    [InlineData("2.0.2-beta.10-r1", "2.0.2-r1", false)]
    [InlineData("2.0.2-rc.1-r1", "2.0.2-beta.10-r1", true)]
    [InlineData("v2.0.2-r1", null, false)]
    [InlineData("2.0-r1", null, false)]
    [InlineData("2.0.2+abc-r1", null, false)]
    public void ComparesComponentVersionsWithNumericRevisions(string candidate, string? current, bool newer) =>
        Assert.Equal(newer, ComponentVersion.IsNewer(candidate, current));

    [Fact]
    public void AComponentVersionReadsBackAsWritten()
    {
        Assert.True(ComponentVersion.TryParse("2.0.2-beta.10-r1", out var version));
        Assert.True(version.IsPrerelease);
        Assert.Equal("2.0.2-beta.10-r1", version.ToString());
        Assert.True(ComponentVersion.TryParse("0.8.8-r1", out version));
        Assert.Equal("0.8.8-r1", version.ToString());
    }

    [Fact]
    public void AFolderInUseIsNotHalfDeleted()
    {
        string folder = Path.Combine(_root, "busy");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "a2dpwb-core.exe");
        File.WriteAllText(file, "x");
        File.WriteAllText(Path.Combine(folder, "a2dpwb.exe"), "x");
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(ComponentInstaller.TryDelete(folder));
            Assert.True(File.Exists(Path.Combine(folder, "a2dpwb.exe")));
        }
        Assert.True(ComponentInstaller.TryDelete(folder));
        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("folder/../../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("file:stream")]
    public void RejectsArchiveTraversal(string entry)
    {
        Directory.CreateDirectory(_root);
        string zip = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry(entry);
        Assert.Throws<InvalidDataException>(() => ComponentInstaller.ExtractArchive(zip, Path.Combine(_root, "stage"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_root, "escape.exe")));
    }

    [Fact]
    public async Task CancellationDoesNotActivatePackage()
    {
        byte[] zip = LhdcPackage("0.8.8-r1");
        var installer = Lhdc(new FakeHttp(zip), zip, "0.8.8-r1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(ct: cancellation.Token));
        Assert.Null(installer.Installed);
    }

    [Fact]
    public async Task BridgePackageInstallsFromItsRootFolder()
    {
        string bridgeCommit = "3a094af733c830a2f4f5bd7418e6c7ba1db94749";
        byte[] zip = BridgePackage("A2DPWB-2.0.1", bridgeCommit);
        var release = new ComponentRelease
        {
            Id = ComponentCatalog.A2dpBridgeId, Version = "2.0.1-r1", Tag = "a2dpwb-v2.0.1-r1",
            Source = new("https://github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge", bridgeCommit, "v2.0.1"),
            Packages = [Package(zip, "A2DPWB-2.0.1-win64-portable.zip") with { Root = "A2DPWB-2.0.1" }],
        };
        var installer = new ComponentInstaller(new HttpClient(new FakeHttp(zip)), _root, release, release.Packages[0], A2dpBridgeComponent.Layout);
        var installed = await installer.InstallAsync(ct: TestContext.Current.CancellationToken);
        Assert.True(A2dpBridgeComponent.IsBridgeFolder(installed.Directory));
        Assert.EndsWith("A2DPWB-2.0.1", installed.Directory);

        // Another build than the pinned one (its SOURCE.txt names another commit) is refused.
        byte[] other = BridgePackage("A2DPWB-2.0.1", new string('d', 40));
        var rogue = new ComponentInstaller(new HttpClient(new FakeHttp(other)), _root + "-rogue", release with { Packages = [Package(other, "A2DPWB-2.0.1-win64-portable.zip") with { Root = "A2DPWB-2.0.1" }] },
            Package(other, "A2DPWB-2.0.1-win64-portable.zip") with { Root = "A2DPWB-2.0.1" }, A2dpBridgeComponent.Layout);
        await Assert.ThrowsAsync<InvalidDataException>(() => rogue.InstallAsync(ct: TestContext.Current.CancellationToken));
        if (Directory.Exists(_root + "-rogue")) Directory.Delete(_root + "-rogue", recursive: true);
    }

    [Theory]
    [InlineData("\"C:\\Users\\me\\AppData\\Local\\A2DP Windows Bridge\\a2dpwb-core.exe\" --background", "C:\\Users\\me\\AppData\\Local\\A2DP Windows Bridge\\a2dpwb-core.exe")]
    [InlineData("C:\\A2DPWB\\a2dpwb-core.exe --background", "C:\\A2DPWB\\a2dpwb-core.exe")]
    [InlineData("\"", null)]
    public void ReadsTheProgramOfARunValue(string command, string? program) => Assert.Equal(program, A2dpBridgeComponent.ProgramOf(command));

    private ComponentInstaller Lhdc(FakeHttp http, byte[] zip, string version, Uri? mirror = null)
    {
        var release = new ComponentRelease
        {
            Id = ComponentCatalog.LhdcId, Version = version, Tag = "lhdc-v" + version,
            Source = new(LhdcComponent.Repository, Commit, "0.8.8"),
            Packages = [Package(zip, $"EncoDeck-LHDC-{version}-win-x64.zip") with { Mirrors = mirror is null ? [] : [mirror] }],
        };
        return new ComponentInstaller(new HttpClient(http), _root, release, release.Packages[0], LhdcComponent.Layout)
        {
            KeepFolders = [LhdcComponent.ResultsFolder],
            Backoff = _ => TimeSpan.FromMilliseconds(10),
        };
    }

    private static ComponentPackage Package(byte[] zip, string file) => new()
    {
        Architecture = "x64", File = file, Size = zip.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(zip)),
    };

    private static byte[] LhdcPackage(string version, bool incomplete = false, string commit = Commit)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            string[] files = ["backend/lhdc-host.exe", "backend/lhdc-service.exe", "backend/lhdc-device.exe", "backend/lhdc-audio-device.exe", "checksums.json", "scripts/Set-AudioMode.ps1", "runtime/powershell/pwsh.exe",
                "drivers/lhdc-test.cer", "drivers/audio/lhdc-audio.sys", "drivers/transport/lhdc-transport.sys"];
            // Distinct bytes per version, so each package has its own digest.
            foreach (string file in files) { using var writer = new StreamWriter(archive.CreateEntry(file).Open()); writer.Write("fixture " + version); }
            using (var runtime = new StreamWriter(archive.CreateEntry("runtime.json").Open()))
                runtime.Write(JsonSerializer.Serialize(new { Schema = 2, Component = "lhdc", ComponentVersion = version, SourceRepository = LhdcComponent.Repository, Architecture = "x64", SourceCommit = commit }));
            if (!incomplete)
            {
                using var writer = new StreamWriter(archive.CreateEntry("release.json").Open());
                writer.Write(JsonSerializer.Serialize(new { SourceCommit = commit, SourceTreeDirty = false }));
            }
        }
        return memory.ToArray();
    }

    private static byte[] BridgePackage(string root, string commit)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string file in new[] { "a2dpwb-core.exe", "a2dpwb.exe", "a2dpwb-driver.exe", "LICENSE", "THIRD_PARTY_LICENSES.md" })
            {
                using var writer = new StreamWriter(archive.CreateEntry($"{root}/{file}").Open());
                writer.Write("fixture");
            }
            using var source = new StreamWriter(archive.CreateEntry($"{root}/SOURCE.txt").Open());
            source.Write($"A2DP Windows Bridge (A2DPWB) 2.0.1 (x64)\nRepository: https://github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge\nCommit:     {commit}\n");
        }
        return memory.ToArray();
    }

    /// <summary>Serves the package; can corrupt it, answer an error, or drop the connection half way (once).</summary>
    private sealed class FakeHttp(byte[] body) : HttpMessageHandler
    {
        private bool _cut;

        public int Requests { get; private set; }

        public List<string> Hosts { get; } = [];

        public List<long> Ranges { get; } = [];

        public Func<Uri, bool> Corrupt { get; init; } = _ => false;

        public Func<Uri, HttpStatusCode> Status { get; init; } = _ => HttpStatusCode.OK;

        public int CutAfter { get; init; } = -1;

        /// <summary>Sources that always send corrupt bytes and drop half way (a broken mirror).</summary>
        public Func<Uri, bool> Broken { get; init; } = _ => false;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            var uri = request.RequestUri!;
            Hosts.Add(uri.Host);
            var status = Status(uri);
            if (status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(status));
            byte[] bytes = Corrupt(uri) || Broken(uri) ? [.. body.Select(b => (byte)~b)] : body;
            long from = request.Headers.Range?.Ranges.First().From ?? 0;
            if (from > 0) Ranges.Add(from);
            Stream content = new MemoryStream(bytes, (int)from, bytes.Length - (int)from);
            if (Broken(uri))
            {
                if (from > 0) Ranges.Add(-from);
                var broken = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CutStream(new MemoryStream(bytes, 0, bytes.Length / 2))) };
                return Task.FromResult(broken);
            }
            if (CutAfter > 0 && !_cut)
            {
                _cut = true;
                content = new CutStream(new MemoryStream(bytes, 0, CutAfter));
            }
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(content) };
            if (from > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.Length - 1, bytes.Length);
            else if (CutAfter <= 0 || _cut && content is not CutStream) response.Content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(response);
        }
    }

    /// <summary>Delivers its bytes, then fails like a dropped connection.</summary>
    private sealed class CutStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            return n > 0 ? n : throw new IOException("connection reset");
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
