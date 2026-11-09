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
    /// Finds the newest official release of <paramref name="pinned"/>'s upstream project (betas included, alphas not:
    /// upstream offers those only to who opts in) later than <paramref name="newerThan"/>, whatever its major version,
    /// and describes it as a component release downloaded from upstream only. Every package of the pin must have its
    /// counterpart there (the same architecture and kind, with the new version, under the project's current name) that
    /// GitHub reports a SHA-256 digest for; the installer holds the download to it and to the tag's commit. A later
    /// major version whose pipe protocol differs is refused by the core's handshake and shown as incompatible.
    /// </summary>
    public async Task<ComponentUpdateResult> CheckUpstreamAsync(ComponentRelease pinned, string? newerThan, CancellationToken ct = default)
    {
        if (UpstreamRepository(pinned.Source.Repository) is not { } repository
            || !ComponentVersion.TryParse(pinned.Version, out var current)) return new(ComponentUpdateOutcome.Failed);
        var (failure, releases) = await ListAsync(new Uri($"https://api.github.com/repos/{repository}/releases?per_page=30"), ct).ConfigureAwait(false);
        if (failure is { } outcome) return new(outcome);

        var candidates = releases.OfType<JsonObject>()
            .Select(r => (Release: r, Tag: Str(r["tag_name"])))
            .Where(x => x.Tag is { Length: > 1 } tag && tag[0] == 'v' && !Bool(x.Release["draft"]))
            .Select(x => (x.Release, x.Tag, Version: ComponentVersion.TryParse(x.Tag![1..] + "-r0", out var v) ? v : null))
            .Where(x => x.Version is not null && !IsAlpha(x.Version) && ComponentVersion.IsNewer(x.Version.ToString(), newerThan))
            .OrderByDescending(x => x.Version)
            .Take(3);
        foreach (var (release, tag, version) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (DescribeUpstream(pinned, current, version!, tag!, release) is not { } offer) continue;
            if (await TagCommitAsync(repository, tag!, ct).ConfigureAwait(false) is not { } commit) continue;
            return new(ComponentUpdateOutcome.Available, offer with { Source = offer.Source with { Commit = commit } });
        }
        return new(ComponentUpdateOutcome.UpToDate);
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

    /// <summary>
    /// The pin's packages as assets of <paramref name="release"/>, or null when one is missing or has no SHA-256 digest.
    /// A package "&lt;Name&gt;-&lt;version&gt;&lt;kind&gt;" ("A2DPWB-2.0.2-beta.11-win64-portable.zip") is matched by the
    /// asset "&lt;AnyName&gt;-&lt;new version&gt;&lt;kind&gt;" ("BlueCaster-3.0.0-beta.3-win64-portable.zip"): the project may
    /// have been renamed since the pin; its archive's folder follows the asset's name.
    /// </summary>
    internal static ComponentRelease? DescribeUpstream(ComponentRelease pinned, ComponentVersion current, ComponentVersion version, string tag, JsonObject release)
    {
        string from = current.Upstream.ToString(), to = version.Upstream.ToString();
        var assets = (release["assets"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var packages = new List<ComponentPackage>();
        foreach (var package in pinned.Packages)
        {
            int at = package.File.IndexOf("-" + from, StringComparison.Ordinal);
            if (at <= 0) return null;
            string kind = package.File[(at + 1 + from.Length)..];
            string ending = "-" + to + kind;
            var matches = assets.Where(a => Str(a["name"]) is { } n && n.EndsWith(ending, StringComparison.Ordinal) && IsProductName(n[..^ending.Length])).ToList();
            if (matches is not [var asset]) return null;
            string file = Str(asset["name"])!;
            string root = package.Root == package.File[..^kind.Length] ? file[..^kind.Length] : package.Root.Replace(from, to, StringComparison.Ordinal);
            long size = asset["size"] is JsonValue s && s.TryGetValue(out long reported) ? reported : 0;
            string sha = Str(asset["digest"]) is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "";
            if (size is <= 0 or > 2L * 1024 * 1024 * 1024 || sha.Length != 64 || !sha.All(Uri.IsHexDigit)
                || !Uri.TryCreate(Str(asset["browser_download_url"]), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com") return null;
            packages.Add(package with
            {
                File = file,
                Size = size,
                Sha256 = sha.ToLowerInvariant(),
                Root = root,
                Mirrors = [url],
            });
        }
        string componentVersion = version.ToString();
        return pinned with
        {
            Version = componentVersion,
            Tag = $"{pinned.Id}-v{componentVersion}",
            Source = pinned.Source with { Release = tag },
            MinApp = null,
            Packages = packages,
            UpstreamDownloads = true,
        };
    }

    /// <summary>The commit a tag points at (the installer checks the package's source note names it).</summary>
    private async Task<string?> TagCommitAsync(string repository, string tag, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = Request(new Uri($"https://api.github.com/repos/{repository}/commits/{Uri.EscapeDataString(tag)}"));
            request.Headers.Accept.ParseAdd("application/vnd.github.sha");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            string sha = System.Text.Encoding.ASCII.GetString(await ReadLimitedAsync(response, 256, timeout.Token).ConfigureAwait(false)).Trim();
            return sha.Length == 40 && sha.All(Uri.IsHexDigit) ? sha.ToLowerInvariant() : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException) { return null; }
    }

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
