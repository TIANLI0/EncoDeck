// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Text.Json.Nodes;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Updates;

namespace EncoDeck.Tests.Platform;

public sealed class ComponentUpdateTests
{
    private const string Commit = "3a094af733c830a2f4f5bd7418e6c7ba1db94749";
    private static readonly string Sha = new('a', 64);

    private static JsonObject Description(string version, int contract = 1, string? minApp = "1.11.0", long size = 1000, string? sha = null) => new()
    {
        ["schema"] = 1,
        ["id"] = "a2dpwb",
        ["version"] = version,
        ["tag"] = "a2dpwb-v" + version,
        ["interface"] = contract,
        ["minApp"] = minApp,
        ["source"] = new JsonObject { ["repository"] = "https://github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge", ["commit"] = Commit, ["release"] = "v" + version[..5] },
        ["packages"] = new JsonArray(new JsonObject
        {
            ["architecture"] = "x64", ["file"] = $"A2DPWB-{version[..5]}-win64-portable.zip", ["size"] = size, ["sha256"] = sha ?? Sha, ["root"] = $"A2DPWB-{version[..5]}",
        }),
    };

    /// <summary>A release as the GitHub API lists it; <paramref name="assetSha"/> is the digest GitHub reports for the package.</summary>
    private static JsonObject Release(string version, bool prerelease = false, string? assetSha = null, long assetSize = 1000, bool manifest = true)
    {
        var assets = new JsonArray(new JsonObject
        {
            ["name"] = $"A2DPWB-{version[..5]}-win64-portable.zip", ["size"] = assetSize, ["digest"] = "sha256:" + (assetSha ?? Sha),
            ["browser_download_url"] = $"https://github.com/TIANLI0/EncoDeck/releases/download/a2dpwb-v{version}/A2DPWB-{version[..5]}-win64-portable.zip",
        });
        if (manifest)
            assets.Add(new JsonObject
            {
                ["name"] = "component.json", ["size"] = 900, ["digest"] = "sha256:" + new string('b', 64),
                ["browser_download_url"] = $"https://github.com/TIANLI0/EncoDeck/releases/download/a2dpwb-v{version}/component.json",
            });
        return new JsonObject { ["tag_name"] = "a2dpwb-v" + version, ["draft"] = false, ["prerelease"] = prerelease, ["assets"] = assets };
    }

    private static ComponentUpdateClient Client(JsonArray releases, Dictionary<string, JsonObject> descriptions, string app = "1.11.0", HttpStatusCode list = HttpStatusCode.OK) =>
        new(new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com") return new HttpResponseMessage(list) { Content = new StringContent(releases.ToJsonString()) };
            string tag = uri.Segments[^2].TrimEnd('/');
            return descriptions.TryGetValue(tag, out var d)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(d.ToJsonString()) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        })), SemanticVersion.Parse(app));

    [Fact]
    public async Task OffersTheNewestConsistentReleaseAboveTheFloor()
    {
        var releases = new JsonArray(Release("2.0.2-r1"), Release("2.0.1-r2"), Release("2.0.1-r1"),
            new JsonObject { ["tag_name"] = "v1.11.0", ["draft"] = false, ["prerelease"] = false, ["assets"] = new JsonArray() },
            new JsonObject { ["tag_name"] = "lhdc-v0.9.0-r1", ["draft"] = false, ["prerelease"] = false, ["assets"] = new JsonArray() });
        var client = Client(releases, new()
        {
            ["a2dpwb-v2.0.2-r1"] = Description("2.0.2-r1"),
            ["a2dpwb-v2.0.1-r2"] = Description("2.0.1-r2"),
        });
        var result = await client.CheckAsync("a2dpwb", 1, "2.0.1-r1", includePrerelease: false, TestContext.Current.CancellationToken);
        Assert.Equal(ComponentUpdateOutcome.Available, result.Outcome);
        Assert.Equal("2.0.2-r1", result.Release?.Version);
        Assert.Equal(1000, result.Release?.Packages[0].Size);

        var none = await client.CheckAsync("a2dpwb", 1, "2.0.2-r1", includePrerelease: false, TestContext.Current.CancellationToken);
        Assert.Equal(ComponentUpdateOutcome.UpToDate, none.Outcome);
    }

    [Theory]
    [InlineData("interface")]   // another backend / pipe contract
    [InlineData("minApp")]      // needs a newer app
    [InlineData("digest")]      // description disagrees with GitHub's digest of the uploaded file
    [InlineData("size")]
    [InlineData("tag")]         // description of another version
    [InlineData("missing")]     // no component.json at all
    public async Task SkipsReleasesThatDoNotFitThisApp(string flaw)
    {
        var release = flaw switch
        {
            "digest" => Release("2.0.2-r1", assetSha: new string('c', 64)),
            "size" => Release("2.0.2-r1", assetSize: 999),
            "missing" => Release("2.0.2-r1", manifest: false),
            _ => Release("2.0.2-r1"),
        };
        var description = flaw switch
        {
            "interface" => Description("2.0.2-r1", contract: 2),
            "minApp" => Description("2.0.2-r1", minApp: "1.12.0"),
            "tag" => Description("2.0.3-r1"),
            _ => Description("2.0.2-r1"),
        };
        var client = Client(new JsonArray(release), new() { ["a2dpwb-v2.0.2-r1"] = description });
        var result = await client.CheckAsync("a2dpwb", 1, "2.0.1-r1", includePrerelease: false, TestContext.Current.CancellationToken);
        Assert.Equal(ComponentUpdateOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task PrereleasesOnlyWhenAsked()
    {
        var client = Client(new JsonArray(Release("2.0.2-r1", prerelease: true)), new() { ["a2dpwb-v2.0.2-r1"] = Description("2.0.2-r1") });
        Assert.Equal(ComponentUpdateOutcome.UpToDate, (await client.CheckAsync("a2dpwb", 1, "2.0.1-r1", false, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ComponentUpdateOutcome.Available, (await client.CheckAsync("a2dpwb", 1, "2.0.1-r1", true, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task ReportsTheRateLimit()
    {
        var client = Client([], [], list: HttpStatusCode.TooManyRequests);
        Assert.Equal(ComponentUpdateOutcome.RateLimited, (await client.CheckAsync("a2dpwb", 1, null, false, TestContext.Current.CancellationToken)).Outcome);
    }

    /// <summary>An official BlueCaster release as its own repository lists it (named A2DPWB up to 2.0.x).</summary>
    private static JsonObject Upstream(string tag, bool draft = false, bool prerelease = true, bool arm64 = true, bool digest = true, string product = "BlueCaster")
    {
        string version = tag[1..];
        var assets = new JsonArray();
        foreach (string arch in arm64 ? new[] { "win64", "arm64" } : ["win64"])
        {
            foreach (string kind in new[] { "portable.zip", "symbols.zip", "setup.exe" })
            {
                string file = $"{product}-{version}-{arch}-{kind}";
                assets.Add(new JsonObject
                {
                    ["name"] = file, ["size"] = 2000, ["digest"] = digest ? "sha256:" + new string('C', 64) : null,
                    ["browser_download_url"] = $"https://github.com/SeiyaFunaokaJP/BlueCaster/releases/download/{tag}/{file}",
                });
            }
        }
        return new JsonObject { ["tag_name"] = tag, ["draft"] = draft, ["prerelease"] = prerelease, ["assets"] = assets };
    }

    /// <summary>The upstream API under the pin's repository name, current or old (GitHub redirects the old one).</summary>
    private static ComponentUpdateClient UpstreamClient(JsonArray releases) =>
        new(new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/SeiyaFunaokaJP/BlueCaster/releases" or "/repos/SeiyaFunaokaJP/A2DP-Windows-Bridge/releases"
                => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releases.ToJsonString()) },
            var path when path.StartsWith("/repos/SeiyaFunaokaJP/BlueCaster/commits/", StringComparison.Ordinal)
                || path.StartsWith("/repos/SeiyaFunaokaJP/A2DP-Windows-Bridge/commits/", StringComparison.Ordinal)
                => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Commit) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        })), SemanticVersion.Parse("1.11.0"));

    [Fact]
    public async Task OffersTheNewestOfficialBuildOfAnyMajorVersion()
    {
        var pinned = ComponentCatalog.A2dpBridge;
        var releases = new JsonArray(Upstream("v4.0.0-beta.1.alpha.1"), Upstream("v4.0.0-beta.2", draft: true), Upstream("v4.0.0-beta.1.x", arm64: false),
            Upstream("v4.0.0-beta.1"), Upstream("v3.0.0-beta.9"), Upstream("v2.0.2-beta.18", product: "A2DPWB"), Upstream("v1.9.0", prerelease: false));
        var result = await UpstreamClient(releases).CheckUpstreamAsync(pinned, pinned.Version, TestContext.Current.CancellationToken);

        // The alpha is offered upstream only on request, the draft is not published, and beta.1.x lacks the arm64
        // package; a new major version is taken (a protocol it changed is refused at the handshake).
        Assert.Equal(ComponentUpdateOutcome.Available, result.Outcome);
        var offer = result.Release!;
        Assert.Equal("4.0.0-beta.1-r0", offer.Version);
        Assert.Equal("a2dpwb-v4.0.0-beta.1-r0", offer.Tag);
        Assert.Equal("v4.0.0-beta.1", offer.Source.Release);
        Assert.Equal(Commit, offer.Source.Commit);
        Assert.True(offer.UpstreamDownloads);
        var x64 = offer.PackageFor(System.Runtime.InteropServices.Architecture.X64)!;
        Assert.Equal(("BlueCaster-4.0.0-beta.1-win64-portable.zip", "BlueCaster-4.0.0-beta.1", 2000L, new string('c', 64)), (x64.File, x64.Root, x64.Size, x64.Sha256));
        Assert.Equal([new Uri("https://github.com/SeiyaFunaokaJP/BlueCaster/releases/download/v4.0.0-beta.1/BlueCaster-4.0.0-beta.1-win64-portable.zip")], offer.Sources(x64));

        var none = await UpstreamClient(releases).CheckUpstreamAsync(pinned, "4.0.0-beta.1-r0", TestContext.Current.CancellationToken);
        Assert.Equal(ComponentUpdateOutcome.UpToDate, none.Outcome);
    }

    [Fact]
    public async Task APinFromBeforeTheRenameFindsTheRenamedBuilds()
    {
        // A pin as apps up to 1.15 carry it: A2DPWB 2.0.x under the project's old name.
        var legacy = ComponentCatalog.A2dpBridge with
        {
            Version = "2.0.2-beta.11-r1",
            Tag = "a2dpwb-v2.0.2-beta.11-r1",
            Source = new("https://github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge", Commit, "v2.0.2-beta.11"),
            Packages = [.. ComponentCatalog.A2dpBridge.Packages.Select(p => p with
            {
                File = $"A2DPWB-2.0.2-beta.11-{(p.Architecture == "x64" ? "win64" : "arm64")}-portable.zip",
                Root = "A2DPWB-2.0.2-beta.11",
            })],
        };
        var releases = new JsonArray(Upstream("v3.0.0-beta.3"), Upstream("v2.0.2-beta.18", product: "A2DPWB"));
        var offer = (await UpstreamClient(releases).CheckUpstreamAsync(legacy, legacy.Version, TestContext.Current.CancellationToken)).Release!;

        Assert.Equal("3.0.0-beta.3-r0", offer.Version);
        var arm64 = offer.PackageFor(System.Runtime.InteropServices.Architecture.Arm64)!;
        Assert.Equal(("BlueCaster-3.0.0-beta.3-arm64-portable.zip", "BlueCaster-3.0.0-beta.3"), (arm64.File, arm64.Root));
    }

    [Fact]
    public async Task OfficialBuildsWithoutADigestAreNotOffered()
    {
        var pinned = ComponentCatalog.A2dpBridge;
        var result = await UpstreamClient(new JsonArray(Upstream("v3.0.0", digest: false))).CheckUpstreamAsync(pinned, pinned.Version, TestContext.Current.CancellationToken);
        Assert.Equal(ComponentUpdateOutcome.UpToDate, result.Outcome);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
