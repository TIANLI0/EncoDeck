// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EncoDeck.Platform.Updates;

/// <summary>A downloadable file attached to a release.</summary>
/// <param name="Sha256">Lower-case hex digest GitHub computed for the asset, when the API reports one.</param>
public sealed record UpdateAsset(string Name, long Size, Uri DownloadUrl, string? Sha256 = null);

/// <summary>A release that has a Windows x64 installer for this app.</summary>
public sealed record UpdateRelease(SemanticVersion Version, string Tag, string? Title, Uri? NotesUrl, DateTimeOffset? PublishedAt,
    bool IsPrerelease, UpdateAsset Installer, UpdateAsset? Checksum, string? Notes = null);

public enum UpdateCheckOutcome
{
    UpToDate,
    Available,
    /// <summary>No connection or a timeout.</summary>
    Offline,
    /// <summary>GitHub's anonymous API limit (60 requests per hour per address) was reached.</summary>
    RateLimited,
    /// <summary>The repository or its releases could not be found.</summary>
    NotFound,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, UpdateRelease? Release = null);

public readonly record struct UpdateDownloadProgress(long Received, long Total);

/// <summary>A downloaded installer and the lower-case hex SHA-256 it was verified against.</summary>
public sealed record VerifiedInstaller(string Path, string Sha256);

/// <summary>The downloaded installer does not match the release's size or checksum.</summary>
public sealed class UpdateVerificationException(string message) : IOException(message);

internal sealed record GitHubAsset(string? Name, long Size, string? BrowserDownloadUrl, string? Digest);
internal sealed record GitHubRelease(string? TagName, string? Name, bool Draft, bool Prerelease, string? HtmlUrl, DateTimeOffset? PublishedAt, List<GitHubAsset>? Assets, string? Body = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;

/// <summary>
/// Reads GitHub Releases for the newest installer and downloads it with size and SHA-256 verification.
/// Uses the anonymous REST API; failures are reported as outcomes, never thrown at the caller.
/// </summary>
public sealed partial class GitHubUpdateClient(HttpClient http, string downloadDirectory, SemanticVersion current, Uri? releasesApi = null) : IDisposable
{
    /// <summary>Component releases (lhdc-v…, a2dpwb-v…) share this list; their tags never parse as app versions.</summary>
    public static Uri DefaultReleasesApi { get; } = new("https://api.github.com/repos/TIANLI0/EncoDeck/releases?per_page=30");

    private const long MaxMetadata = 4L * 1024 * 1024;
    private const long MaxChecksumFile = 64 * 1024;
    private const long MaxInstaller = 1024L * 1024 * 1024;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly Uri _api = releasesApi ?? DefaultReleasesApi;

    public SemanticVersion Current { get; } = current;

    public string DownloadDirectory { get; } = downloadDirectory;

    /// <summary>Matches build/build-installer.ps1's output name.</summary>
    [GeneratedRegex(@"^EncoDeck-Setup-(?<version>[0-9A-Za-z.\-]+)-win-x64\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallerName();

    [GeneratedRegex(@"^(?<hash>[0-9A-Fa-f]{64})(?:\s+\*?(?<name>.+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLine();

    [GeneratedRegex(@"^SHA256\s*\((?<name>.+)\)\s*=\s*(?<hash>[0-9A-Fa-f]{64})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BsdChecksumLine();

    public async Task<UpdateCheckResult> CheckAsync(bool includePrerelease, CancellationToken cancellationToken = default)
    {
        List<GitHubRelease>? releases;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = Request(_api);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(UpdateCheckOutcome.NotFound);
            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0"))
                return new(UpdateCheckOutcome.RateLimited);
            if (!response.IsSuccessStatusCode) return new(UpdateCheckOutcome.Failed);
            if (response.Content.Headers.ContentLength > MaxMetadata) return new(UpdateCheckOutcome.Failed);
            byte[] body = await ReadLimitedAsync(response, MaxMetadata, timeout.Token).ConfigureAwait(false);
            releases = JsonSerializer.Deserialize(body, GitHubJsonContext.Default.ListGitHubRelease);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(UpdateCheckOutcome.Offline); }
        catch (HttpRequestException ex) when (ex.StatusCode is null) { return new(UpdateCheckOutcome.Offline); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or IOException) { return new(UpdateCheckOutcome.Failed); }

        var newest = SelectRelease(releases ?? [], includePrerelease);
        return newest is not null && newest.Version > Current ? new(UpdateCheckOutcome.Available, newest) : new(UpdateCheckOutcome.UpToDate, newest);
    }

    /// <summary>
    /// The highest-versioned published release with an x64 installer. Drafts are never offered;
    /// pre-releases (flagged by GitHub or by a "-suffix" tag) only when <paramref name="includePrerelease"/>.
    /// A release still missing its installer is skipped, so an older complete one can still be offered.
    /// </summary>
    internal static UpdateRelease? SelectRelease(IEnumerable<GitHubRelease> releases, bool includePrerelease)
    {
        UpdateRelease? best = null;
        foreach (var release in releases)
        {
            if (release is null || release.Draft || !SemanticVersion.TryParse(release.TagName, out var version)) continue;
            bool prerelease = release.Prerelease || version.IsPrerelease;
            if (prerelease && !includePrerelease) continue;
            var assets = (release.Assets ?? []).Where(a => a is not null).Select(ToAsset).OfType<UpdateAsset>().ToList();
            var installer = assets.FirstOrDefault(a => a.Size > 0 && InstallerName().Match(a.Name) is { Success: true } m
                && SemanticVersion.TryParse(m.Groups["version"].Value, out var named) && named.Equals(version));
            if (installer is null) continue;
            var checksum = assets.FirstOrDefault(a => string.Equals(a.Name, installer.Name + ".sha256", StringComparison.OrdinalIgnoreCase))
                ?? assets.FirstOrDefault(a => a.Name.ToUpperInvariant() is "SHA256SUMS" or "SHA256SUMS.TXT");
            if (best is null || version > best.Version)
            {
                best = new UpdateRelease(version, release.TagName!, release.Name, Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var notes) && notes.Scheme == Uri.UriSchemeHttps ? notes : null,
                    release.PublishedAt, prerelease, installer, checksum, ReleaseNotes.ToPlainText(release.Body));
            }
        }
        return best;
    }

    private static UpdateAsset? ToAsset(GitHubAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Name) || Path.GetFileName(asset.Name) != asset.Name || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) return null;
        string? digest = asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && d.Length == 71 && d[7..].All(Uri.IsHexDigit)
            ? d[7..].ToLowerInvariant() : null;
        return new UpdateAsset(asset.Name, asset.Size, url, digest);
    }

    /// <summary>
    /// The SHA-256 for <paramref name="fileName"/> in a checksum file: "hash  name", "hash *name",
    /// "SHA256 (name) = hash", or a lone hash (a per-file .sha256).
    /// </summary>
    internal static string? FindChecksum(string text, string fileName)
    {
        string? lone = null;
        int hashes = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == '#') continue;
            var match = ChecksumLine().Match(line);
            if (!match.Success) match = BsdChecksumLine().Match(line);
            if (!match.Success) continue;
            hashes++;
            string hash = match.Groups["hash"].Value.ToLowerInvariant();
            if (!match.Groups["name"].Success) { lone = hash; continue; }
            string name = match.Groups["name"].Value.Trim();
            name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return hash;
        }
        return hashes == 1 ? lone : null;
    }

    /// <summary>
    /// Downloads the installer into <see cref="DownloadDirectory"/> (or reuses an already verified copy) and
    /// returns its path. The file appears under its final name only after size and checksum verification.
    /// </summary>
    /// <exception cref="UpdateVerificationException">
    /// No SHA-256 published for the installer, size or checksum mismatch, or a checksum file without this installer.
    /// </exception>
    public async Task<string> DownloadAsync(UpdateRelease release, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
        (await DownloadVerifiedAsync(release, progress, cancellationToken).ConfigureAwait(false)).Path;

    /// <summary>
    /// As <see cref="DownloadAsync"/>, with the SHA-256 the file was verified against, so it can be checked again
    /// right before it is started (the download folder is writable by the user, and the file may wait there for days).
    /// </summary>
    public async Task<VerifiedInstaller> DownloadVerifiedAsync(UpdateRelease release, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var installer = release.Installer;
        if (installer.Size is <= 0 or > MaxInstaller) throw new UpdateVerificationException("Unexpected installer size.");
        string? expected = installer.Sha256;
        if (release.Checksum is { } checksum)
        {
            string text = Encoding.UTF8.GetString(await GetSmallAsync(checksum.DownloadUrl, MaxChecksumFile, cancellationToken).ConfigureAwait(false));
            string published = FindChecksum(text, installer.Name) ?? throw new UpdateVerificationException("The checksum file does not list the installer.");
            if (expected is not null && expected != published) throw new UpdateVerificationException("Published checksums disagree.");
            expected = published;
        }
        // Fail closed: a size alone proves nothing about the file, so an installer without a published SHA-256 (no
        // GitHub asset digest, no checksum file) is never downloaded. The release page stays available to the user.
        if (expected is null) throw new UpdateVerificationException("The release publishes no SHA-256 for the installer.");

        Directory.CreateDirectory(DownloadDirectory);
        string target = Path.Combine(DownloadDirectory, installer.Name);
        if (File.Exists(target) && await VerifyAsync(target, installer.Size, expected, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(new UpdateDownloadProgress(installer.Size, installer.Size));
            return new VerifiedInstaller(target, expected);
        }
        CleanUp(keep: installer.Name);

        string partial = Path.Combine(DownloadDirectory, $"{installer.Name}.{Guid.NewGuid():N}.partial");
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);
            using var request = Request(installer.DownloadUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != installer.Size)
                throw new UpdateVerificationException("The server sent a file of a different size.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0, lastReport = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                progress?.Report(new UpdateDownloadProgress(0, installer.Size));
                while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) != 0)
                {
                    stall.CancelAfter(StallTimeout);
                    received += read;
                    if (received > installer.Size) throw new UpdateVerificationException("The download is larger than the published installer.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    long now = Environment.TickCount64;
                    if (now - lastReport >= 100 || received == installer.Size)
                    {
                        lastReport = now;
                        progress?.Report(new UpdateDownloadProgress(received, installer.Size));
                    }
                }
                await output.FlushAsync(stall.Token).ConfigureAwait(false);
            }
            if (received != installer.Size) throw new UpdateVerificationException("The download is incomplete.");
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expected)
                throw new UpdateVerificationException("Installer SHA-256 verification failed.");
            File.Move(partial, target, overwrite: true);
            return new VerifiedInstaller(target, expected);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The download stalled.");
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes unfinished downloads and installers other than <paramref name="keep"/>; best effort.</summary>
    public void CleanUp(string? keep = null)
    {
        try
        {
            if (!Directory.Exists(DownloadDirectory)) return;
            foreach (string file in Directory.EnumerateFiles(DownloadDirectory))
            {
                string name = Path.GetFileName(file);
                bool stale = name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                    || (InstallerName().IsMatch(name) && !string.Equals(name, keep, StringComparison.OrdinalIgnoreCase));
                if (!stale) continue;
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes downloaded installers that are not newer than the running version (after an update).</summary>
    public void CleanUpInstalled()
    {
        try
        {
            if (!Directory.Exists(DownloadDirectory)) return;
            foreach (string file in Directory.EnumerateFiles(DownloadDirectory))
            {
                var match = InstallerName().Match(Path.GetFileName(file));
                bool stale = file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                    || (match.Success && (!SemanticVersion.TryParse(match.Groups["version"].Value, out var version) || version <= Current));
                if (!stale) continue;
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<bool> VerifyAsync(string path, long size, string sha256, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length != size) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) == sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // GitHub rejects API requests without a User-Agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("EncoDeck", Current.ToString()));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/TIANLI0/EncoDeck)"));
        return request;
    }

    private async Task<byte[]> GetSmallAsync(Uri uri, long limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedAsync(response, limit, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
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

    public void Dispose() => http.Dispose();
}
