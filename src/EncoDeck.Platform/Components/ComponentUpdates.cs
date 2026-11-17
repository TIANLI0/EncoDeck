// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using EncoDeck.Platform.Updates;

namespace EncoDeck.Platform.Components;

public enum ComponentUpdateOutcome
{
    UpToDate,
    Available,
    Offline,
    /// <summary>GitHub's anonymous API limit was reached.</summary>
    RateLimited,
    Failed,
}

public sealed record ComponentUpdateResult(ComponentUpdateOutcome Outcome, ComponentRelease? Release = null);

/// <summary>An upstream project's own installer, as GitHub lists it: downloaded to this size and SHA-256 digest.</summary>
public sealed record UpstreamSetup(string Version, string File, Uri Url, long Size, string Sha256);

public sealed record UpstreamSetupResult(ComponentUpdateOutcome Outcome, UpstreamSetup? Setup = null);

/// <summary>
/// Finds component releases newer than what this app pins or has installed, without an app update. A candidate
/// counts only when its <c>component.json</c> names the same component, tag and version, speaks this app's
/// interface, accepts this app version, and every package it lists is an asset of that release with the same
/// size and GitHub digest. The installer then holds the download to that size and digest, as for a pin.
/// </summary>
public sealed class ComponentUpdateClient(HttpClient http, SemanticVersion app, Uri? releasesApi = null)
{
    private const long MaxList = 4L * 1024 * 1024;
    private const long MaxManifest = 64 * 1024;
    private readonly Uri _api = releasesApi ?? GitHubUpdateClient.DefaultReleasesApi;

    public async Task<ComponentUpdateResult> CheckAsync(string id, int contract, string? newerThan, bool includePrerelease, CancellationToken ct = default)
    {
        var (failure, releases) = await ListAsync(_api, ct).ConfigureAwait(false);
        if (failure is { } outcome) return new(outcome);

        string prefix = id + "-v";
        var candidates = releases.OfType<JsonObject>()
            .Select(r => (Release: r, Tag: Str(r["tag_name"])))
            .Where(x => x.Tag is not null && x.Tag.StartsWith(prefix, StringComparison.Ordinal) && !Bool(x.Release["draft"])
                && (includePrerelease || !Bool(x.Release["prerelease"])))
            .Select(x => (x.Release, x.Tag, Version: ComponentVersion.TryParse(x.Tag![prefix.Length..], out var v) ? v : null))
            .Where(x => x.Version is not null && ComponentVersion.IsNewer(x.Version.ToString(), newerThan))
            .OrderByDescending(x => x.Version)
            .Take(3);
        foreach (var (release, tag, _) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await DescribeAsync(release, id, tag!, contract, ct).ConfigureAwait(false) is { } offer) return new(ComponentUpdateOutcome.Available, offer);
        }
        return new(ComponentUpdateOutcome.UpToDate);
    }

    /// <summary>
    /// The installer of the newest official release of <paramref name="repositoryUrl"/>, looked up when asked: nothing
    /// about it (version, file, size, digest) is built into the app. Any major version and betas count; alphas (offered
    /// upstream only to who opts in) and drafts do not. It is the asset "&lt;Name&gt;-&lt;version&gt;-&lt;arch&gt;-setup.exe"
    /// that GitHub reports a SHA-256 digest for; a newer release without one for this architecture is passed over. The
    /// installed program then updates and uninstalls itself.
    /// </summary>
    /// <param name="architecture">The asset's architecture tag ("win64", "arm64").</param>
    public async Task<UpstreamSetupResult> FindUpstreamSetupAsync(string repositoryUrl, string architecture, CancellationToken ct = default)
    {
        if (UpstreamRepository(repositoryUrl) is not { } repository) return new(ComponentUpdateOutcome.Failed);
        var (failure, releases) = await ListAsync(new Uri($"https://api.github.com/repos/{repository}/releases?per_page=30"), ct).ConfigureAwait(false);
        if (failure is { } outcome) return new(outcome);
        var setup = releases.OfType<JsonObject>()
            .Select(r => (Release: r, Tag: Str(r["tag_name"])))
            .Where(x => x.Tag is { Length: > 1 } tag && tag[0] == 'v' && !Bool(x.Release["draft"]))
            .Select(x => (x.Release, x.Tag, Version: ComponentVersion.TryParse(x.Tag![1..] + "-r0", out var v) ? v : null))
            .Where(x => x.Version is not null && !IsAlpha(x.Version))
            .OrderByDescending(x => x.Version)
            .Select(x => DescribeSetup(x.Tag!, x.Release, architecture))
            .FirstOrDefault(s => s is not null);
        return setup is null ? new(ComponentUpdateOutcome.Failed) : new(ComponentUpdateOutcome.Available, setup);
    }

    /// <summary>The release's installer for <paramref name="architecture"/>, or null without one that has a SHA-256 digest.</summary>
    internal static UpstreamSetup? DescribeSetup(string tag, JsonObject release, string architecture)
    {
        string version = tag[1..];
        string ending = $"-{version}-{architecture}-setup.exe";
        var matches = (release["assets"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(a => Str(a["name"]) is { } n && n.EndsWith(ending, StringComparison.Ordinal) && IsProductName(n[..^ending.Length]))
            .ToList();
        if (matches is not [var asset]) return null;
        long size = asset["size"] is JsonValue s && s.TryGetValue(out long reported) ? reported : 0;
        string sha = Str(asset["digest"]) is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "";
        if (size is <= 0 or > 512L * 1024 * 1024 || sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return null;
        if (!Uri.TryCreate(Str(asset["browser_download_url"]), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com") return null;
        return new UpstreamSetup(version, Str(asset["name"])!, url, size, sha.ToLowerInvariant());
    }

    /// <summary>"owner/name" of a https://github.com/owner/name repository URL.</summary>
    private static string? UpstreamRepository(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com"
        && uri.AbsolutePath.Trim('/').Split('/') is [var owner, var name] && owner.Length > 0 && name.Length > 0 ? $"{owner}/{name}" : null;

    /// <summary>An upstream alpha ("3.0.0-beta.3.alpha.1"): tested less than a beta, and offered upstream only on request.</summary>
    private static bool IsAlpha(ComponentVersion version) =>
        version.Upstream.Prerelease.Any(p => p.StartsWith("alpha", StringComparison.OrdinalIgnoreCase));

    /// <summary>A product name in an asset's file name ("BlueCaster", "A2DPWB"): ASCII letters and digits, starting with a letter.</summary>
    private static bool IsProductName(string name) =>
        name.Length is > 0 and <= 64 && char.IsAsciiLetter(name[0]) && name.All(char.IsAsciiLetterOrDigit);

    /// <summary>A GitHub release list; a failure outcome instead when it could not be read.</summary>
    private async Task<(ComponentUpdateOutcome? Failure, JsonArray Releases)> ListAsync(Uri api, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = Request(api);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0"))
                return (ComponentUpdateOutcome.RateLimited, []);
            if (!response.IsSuccessStatusCode) return (ComponentUpdateOutcome.Failed, []);
            return (null, JsonNode.Parse(await ReadLimitedAsync(response, MaxList, timeout.Token).ConfigureAwait(false)) as JsonArray ?? []);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (ComponentUpdateOutcome.Offline, []); }
        catch (HttpRequestException ex) when (ex.StatusCode is null) { return (ComponentUpdateOutcome.Offline, []); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or IOException) { return (ComponentUpdateOutcome.Failed, []); }
    }

    /// <summary>The release's component.json, when it is consistent with the release and this app; null otherwise.</summary>
    private async Task<ComponentRelease?> DescribeAsync(JsonObject release, string id, string tag, int contract, CancellationToken ct)
    {
        var assets = (release["assets"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var manifest = assets.FirstOrDefault(a => Str(a["name"]) == ComponentCatalog.ManifestAsset);
        if (manifest is null || !Uri.TryCreate(Str(manifest["browser_download_url"]), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) return null;
        ComponentRelease offer;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = Request(url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            offer = ComponentCatalog.ParseRelease(System.Text.Encoding.UTF8.GetString(await ReadLimitedAsync(response, MaxManifest, timeout.Token).ConfigureAwait(false)).TrimStart((char)0xFEFF));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or IOException or InvalidOperationException or FormatException) { return null; }
        return Accepts(offer, id, tag, contract, assets) ? offer : null;
    }

    /// <summary>The checks a found release must pass.</summary>
    internal bool Accepts(ComponentRelease offer, string id, string tag, int contract, IReadOnlyList<JsonObject> assets)
    {
        if (offer.Id != id || offer.Tag != tag || offer.Interface != contract || offer.Packages.Count == 0) return false;
        if (offer.MinApp is { } min && SemanticVersion.TryParse(min, out var minimum) && app.CompareTo(minimum) < 0) return false;
        foreach (var package in offer.Packages)
        {
            if (!package.IsPublished) return false;
            var asset = assets.FirstOrDefault(a => Str(a["name"]) == package.File);
            if (asset is null || asset["size"]?.GetValue<long>() != package.Size) return false;
            // GitHub's own digest of the uploaded file must agree with the description, where GitHub reports one.
            if (Str(asset["digest"]) is { } digest && !string.Equals(digest, "sha256:" + package.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("EncoDeck", app.ToString()));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/TIANLI0/EncoDeck)"));
        return request;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response exceeds size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Response exceeds size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) && b;
}
