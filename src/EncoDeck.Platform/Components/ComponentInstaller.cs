// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EncoDeck.Platform.Updates;

namespace EncoDeck.Platform.Components;

/// <summary>A component unpacked on this PC.</summary>
/// <param name="Directory">The component's own folder (the package root inside the archive).</param>
public sealed record InstalledComponent(string Version, string Sha256, string Directory);

/// <summary>What a component's files must look like, checked after unpacking and before every use.</summary>
public sealed class ComponentLayout
{
    /// <summary>Paths relative to the component folder, with '/' separators.</summary>
    public IReadOnlyList<string> RequiredFiles { get; init; } = [];

    /// <summary>
    /// Further checks of the identity files: (folder, version, pinned source commit). The commit is given when a
    /// package is installed and null when an installed version is checked before use (it may be an older pin).
    /// Throws <see cref="InvalidDataException"/> when the folder is not that component.
    /// </summary>
    public Action<string, string, string?>? Validate { get; init; }

    /// <summary>A check of the files for a component whose file names depend on its version (BlueCaster, A2DPWB 2.0.x before it).</summary>
    public Func<string, bool>? HasFiles { get; init; }

    public bool HasRequiredFiles(string directory) =>
        RequiredFiles.All(p => File.Exists(Path.Combine(directory, p))) && (HasFiles?.Invoke(directory) ?? true);
}

/// <summary>
/// Installs one pinned component package under <c>Components\&lt;name&gt;\&lt;version&gt;-&lt;sha&gt;</c>. The pin
/// (size and SHA-256 in the app) decides what is accepted, so the bytes may come from the EncoDeck component
/// release or any mirror; a download resumes where it stopped. Installs never depend on the app version:
/// an app update keeps using what is installed, and an update replaces the installed version (the old one is
/// deleted at once, or at the next clean-up while a program still runs from it).
/// </summary>
public sealed class ComponentInstaller(HttpClient http, string root, ComponentRelease release, ComponentPackage? package, ComponentLayout layout)
{
    private const string Pointer = "current.json";
    private const string DownloadsFolder = "downloads";
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ComponentRelease Release { get; } = release;

    /// <summary>The package for this PC; null when the component has none for its architecture.</summary>
    public ComponentPackage? Package { get; } = package;

    public string Root { get; } = root;

    /// <summary>Wait before retrying a source (tests shorten it).</summary>
    internal Func<int, TimeSpan>? Backoff { get; init; }

    /// <summary>Folders here that are not component versions and are never cleaned up (e.g. LHDC's deployment results).</summary>
    public IReadOnlyCollection<string> KeepFolders { get; init; } = [];

    /// <summary>The installed version, if its files are complete; any version, not only the pinned one.</summary>
    public InstalledComponent? Installed
    {
        get
        {
            try
            {
                var pointer = ReadPointer();
                if (pointer is null) return null;
                string directory = Path.Combine(Root, pointer.Folder, pointer.PackageRoot);
                if (!layout.HasRequiredFiles(directory)) return null;
                layout.Validate?.Invoke(directory, pointer.Version, null);
                return new InstalledComponent(pointer.Version, pointer.Sha256, directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
                or InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
        }
    }

    /// <summary>Installed, and the pin is not newer (it may be the pin itself or a later release installed online).</summary>
    public bool IsCurrent => Package is { IsPublished: true } p && Installed is { } i
        && (i.Sha256 == p.Sha256 || !ComponentVersion.IsNewer(Release.Version, i.Version));

    public bool CanInstall => Package is { IsPublished: true };

    /// <summary>The package of <paramref name="release"/> for this PC (the architecture of the pinned one).</summary>
    public ComponentPackage? PackageOf(ComponentRelease release) =>
        ReferenceEquals(release, Release) ? Package : release.Packages.FirstOrDefault(p => p.Architecture == Package?.Architecture);

    /// <summary>The newest installable release: this app's pin, or <paramref name="online"/> when that is later.</summary>
    public ComponentRelease? Newest(ComponentRelease? online)
    {
        var pinned = CanInstall ? Release : null;
        if (online is null || online.Id != Release.Id || PackageOf(online) is not { IsPublished: true }) return pinned;
        return pinned is null || ComponentVersion.IsNewer(online.Version, pinned.Version) ? online : pinned;
    }

    /// <summary>What to update to: the newest installable release, when it is later than the installed version.</summary>
    public ComponentRelease? UpdateTarget(ComponentRelease? online) =>
        Installed is { } installed && Newest(online) is { } newest && ComponentVersion.IsNewer(newest.Version, installed.Version) ? newest : null;

    /// <summary>Downloads (resuming a partial file), verifies, unpacks and switches to the pinned package; a no-op when it is already installed.</summary>
    public Task<InstalledComponent> InstallAsync(IProgress<UpdateDownloadProgress>? progress = null, CancellationToken ct = default) =>
        InstallAsync(Release, progress, ct);

    /// <summary>
    /// Installs <paramref name="release"/> (the pin, or a later release found online with its own size and digest)
    /// and removes every other version.
    /// </summary>
    public async Task<InstalledComponent> InstallAsync(ComponentRelease release, IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        if (release.Id != Release.Id) throw new ArgumentException("Another component's release.", nameof(release));
        if (PackageOf(release) is not { IsPublished: true } package) throw new InvalidOperationException($"No published {release.Id} package for this PC.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? staging = null;
        try
        {
            if (Installed is { } installed && installed.Sha256 == package.Sha256) return installed;
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overall.CancelAfter(TimeSpan.FromMinutes(45));
            string downloads = Path.Combine(Root, DownloadsFolder);
            Directory.CreateDirectory(downloads);
            string zip = Path.Combine(downloads, package.File);
            try { await DownloadAsync(http, release.Sources(package), zip, package.Size, package.Sha256, progress, overall.Token, Backoff).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"The {release.Id} download took too long."); }

            string folder = $"{release.Version}-{package.Sha256[..12]}";
            staging = Path.Combine(Root, ".install-" + Guid.NewGuid().ToString("N"));
            await Task.Run(() => ExtractArchive(zip, staging, overall.Token), overall.Token).ConfigureAwait(false);
            string unpacked = Path.Combine(staging, package.Root);
            if (!layout.HasRequiredFiles(unpacked)) throw new InvalidDataException($"Incomplete {release.Id} package.");
            layout.Validate?.Invoke(unpacked, release.Version, release.Source.Commit);

            string destination = Path.Combine(Root, folder);
            // Only a damaged or orphaned copy of this version can be there (a valid current one returned above).
            if (Directory.Exists(destination) && !TryDelete(destination))
                throw new IOException($"The {release.Id} component folder is in use. Close the program that uses it and try again.");
            Directory.Move(staging, destination);
            staging = null;
            WritePointer(new PointerFile(release.Version, package.Sha256, folder, package.Root));
            TryDeleteFile(zip);
            CleanUp();
            return Installed ?? throw new InvalidDataException($"The {Release.Id} component did not validate after installation.");
        }
        finally
        {
            if (staging is not null) TryDelete(staging);
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the component entirely: every version, the pointer, downloads and kept folders. Nothing is removed
    /// while any file of it is in use (the folder is renamed first, which fails then); returns whether it is gone.
    /// </summary>
    public bool Uninstall()
    {
        // Never while an installation runs (it holds the gate): report "not removed" instead of blocking.
        if (!_gate.Wait(0)) return false;
        try { return TryDelete(Root); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Removes unpacked versions other than the current one, legacy layouts and stale staging
    /// folders. A folder whose files are in use (e.g. a running program) is left for a later pass.
    /// </summary>
    public void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var pointer = ReadPointer();
            var keep = new HashSet<string>(KeepFolders, StringComparer.OrdinalIgnoreCase) { DownloadsFolder };
            if (pointer is not null) keep.Add(pointer.Folder);
            foreach (string directory in Directory.EnumerateDirectories(Root))
            {
                string name = Path.GetFileName(directory);
                if (keep.Contains(name)) continue;
                TryDelete(directory);
            }
            string downloads = Path.Combine(Root, DownloadsFolder);
            if (Directory.Exists(downloads))
            {
                foreach (string file in Directory.EnumerateFiles(downloads))
                {
                    string name = Path.GetFileName(file);
                    // Recent partial downloads stay, so a retry (of the pin or of an update) resumes.
                    if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7)) continue;
                    TryDeleteFile(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private sealed record PointerFile(string Version, string Sha256, string Folder, string PackageRoot);

    private PointerFile? ReadPointer()
    {
        string path = Path.Combine(Root, Pointer);
        if (!File.Exists(path)) return null;
        var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
        if (node is null) return null;
        string? version = node["version"]?.GetValue<string>(), sha = node["sha256"]?.GetValue<string>(), folder = node["folder"]?.GetValue<string>();
        string packageRoot = node["root"]?.GetValue<string>() ?? "";
        if (version is null || sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit) || !IsFolderName(folder)
            || (packageRoot.Length > 0 && !IsFolderName(packageRoot))) return null;
        return new PointerFile(version, sha, folder!, packageRoot);
    }

    private void WritePointer(PointerFile pointer)
    {
        var node = new JsonObject
        {
            ["version"] = pointer.Version,
            ["sha256"] = pointer.Sha256,
            ["folder"] = pointer.Folder,
            ["root"] = pointer.PackageRoot,
            ["installedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        };
        string temp = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, Path.Combine(Root, Pointer), overwrite: true);
    }

    private static bool IsFolderName(string? name) => name is { Length: > 0 and <= 160 } && name == Path.GetFileName(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");

    /// <summary>
    /// Downloads <paramref name="target"/> from the first source that delivers the pinned bytes. Transient failures
    /// retry the same source and resume the partial file; a 4xx answer or wrong bytes move on to the next source.
    /// </summary>
    internal static async Task DownloadAsync(HttpClient http, IReadOnlyList<Uri> sources, string target, long size, string sha256,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken ct, Func<int, TimeSpan>? backoff = null)
    {
        if (File.Exists(target) && await HasDigestAsync(target, size, sha256, ct).ConfigureAwait(false))
        {
            progress?.Report(new UpdateDownloadProgress(size, size));
            return;
        }
        TryDeleteFile(target);
        string partial = target + ".partial";
        Exception? last = null;
        for (int index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            // A partial file is resumed only from the source that wrote it (the first source may resume an earlier run).
            if (index > 0) TryDeleteFile(partial);
            bool restarted = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                bool resuming = File.Exists(partial) && new FileInfo(partial).Length > 0;
                try
                {
                    await DownloadOnceAsync(http, source, partial, size, sha256, progress, ct).ConfigureAwait(false);
                    File.Move(partial, target, overwrite: true);
                    return;
                }
                catch (UpdateVerificationException ex)
                {
                    // Wrong bytes are never resumed. If part came from earlier, this source gets one fresh try.
                    last = ex;
                    TryDeleteFile(partial);
                    if (resuming && !restarted)
                    {
                        restarted = true;
                        attempt--;
                        continue;
                    }
                    break;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is { } status && (int)status is >= 400 and < 500 and not 408 and not 429)
                {
                    last = ex;
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
                {
                    last = ex is OperationCanceledException ? new TimeoutException("The download stalled.", ex) : ex;
                    await Task.Delay((backoff ?? DefaultBackoff)(attempt), ct).ConfigureAwait(false);
                }
            }
        }
        throw last ?? new IOException("No download source.");
    }

    private static TimeSpan DefaultBackoff(int attempt) => TimeSpan.FromSeconds(2 << attempt);

    private static async Task DownloadOnceAsync(HttpClient http, Uri source, string partial, long size, string sha256,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > size) { TryDeleteFile(partial); have = 0; }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("EncoDeck", SemanticVersion.FromAssembly(typeof(ComponentInstaller).Assembly).ToString()));
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange is { From: { } from } && from == have;
        if (have > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have == size)
        {
            if (!await HasDigestAsync(partial, size, sha256, ct).ConfigureAwait(false)) throw new UpdateVerificationException("Component SHA-256 verification failed.");
            return;
        }
        response.EnsureSuccessStatusCode();
        if (!resumed) have = 0;
        long expected = size - have;
        if (response.Content.Headers.ContentLength is { } length && length != expected)
            throw new UpdateVerificationException("The server sent a file of a different size.");
        if (have > 0)
        {
            await using var existing = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            byte[] chunk = new byte[81920];
            int n;
            while ((n = await existing.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0) hash.AppendData(chunk, 0, n);
        }
        long received = have, lastReport = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
        await using (var output = new FileStream(partial, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            int read;
            progress?.Report(new UpdateDownloadProgress(received, size));
            while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) != 0)
            {
                stall.CancelAfter(StallTimeout);
                received += read;
                if (received > size) throw new UpdateVerificationException("The download is larger than the pinned package.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                long now = Environment.TickCount64;
                if (now - lastReport >= 100 || received == size)
                {
                    lastReport = now;
                    progress?.Report(new UpdateDownloadProgress(received, size));
                }
            }
            await output.FlushAsync(stall.Token).ConfigureAwait(false);
        }
        // A short read is a dropped connection: keep the partial file so the retry resumes it.
        if (received != size) throw new IOException("The download is incomplete.");
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != sha256) throw new UpdateVerificationException("Component SHA-256 verification failed.");
    }

    private static async Task<bool> HasDigestAsync(string path, long size, string sha256, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length != size) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) == sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Unpacks a zip with no entry outside <paramref name="destination"/>, no links and bounded size.</summary>
    internal static void ExtractArchive(string zip, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        string prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("Too many package entries.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            total = checked(total + entry.Length);
            if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Package is too large.");
            string path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':')
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unsafe package path.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
    }

    /// <summary>
    /// Deletes a folder only when nothing in it is open: renaming it first fails while any file inside is in use
    /// (a running program), and then the folder is left whole instead of half deleted.
    /// </summary>
    internal static bool TryDelete(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return true;
            string trash = Path.Combine(Path.GetDirectoryName(directory)!, ".trash-" + Guid.NewGuid().ToString("N"));
            Directory.Move(directory, trash);
            Directory.Delete(trash, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
