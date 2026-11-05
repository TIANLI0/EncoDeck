// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using EncoDeck.Platform.Updates;

namespace EncoDeck.Platform.Components;

/// <summary>One downloadable archive of a component, pinned by size and SHA-256.</summary>
public sealed record ComponentPackage
{
    /// <summary>"x64" or "arm64".</summary>
    public required string Architecture { get; init; }

    public required string File { get; init; }

    public long Size { get; init; }

    /// <summary>Lower-case hex; empty while the component release is not published yet.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>Folder inside the archive that holds the component (e.g. "A2DPWB-2.0.1"); empty for the archive root.</summary>
    public string Root { get; init; } = "";

    /// <summary>Other sources of the same bytes, tried after the EncoDeck component release; verified against the same digest.</summary>
    public IReadOnlyList<Uri> Mirrors { get; init; } = [];

    public bool IsPublished => Size > 0 && Sha256.Length == 64;
}

/// <summary>Where a component's code comes from (shown to the user and checked against the package).</summary>
public sealed record ComponentSource(string Repository, string Commit, string Release);

/// <summary>
/// A component version: the upstream version, possibly an upstream pre-release, and EncoDeck's build revision of it
/// ("0.8.8-r1", "2.0.2-beta.10-r1"). Upstream versions compare as SemVer (a pre-release before its release),
/// revisions as numbers, so r10 follows r9. Apps before 1.12 cannot read a pre-release and never take one.
/// </summary>
public sealed record ComponentVersion(SemanticVersion Upstream, int Revision) : IComparable<ComponentVersion>
{
    public static bool TryParse(string? text, [NotNullWhen(true)] out ComponentVersion? version)
    {
        version = null;
        if (text is null) return false;
        int dash = text.LastIndexOf("-r", StringComparison.Ordinal);
        if (dash <= 0 || !int.TryParse(text[(dash + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out int revision)) return false;
        string upstream = text[..dash];
        int pre = upstream.IndexOf('-');
        // Exactly x.y.z, with no "v" prefix or build metadata, as tags are named.
        if (!char.IsAsciiDigit(upstream[0]) || upstream.Contains('+') || (pre < 0 ? upstream : upstream[..pre]).Split('.').Length != 3) return false;
        if (!SemanticVersion.TryParse(upstream, out var parsed)) return false;
        version = new ComponentVersion(parsed, revision);
        return true;
    }

    public bool IsPrerelease => Upstream.IsPrerelease;

    public int CompareTo(ComponentVersion? other)
    {
        if (other is null) return 1;
        int c = Upstream.CompareTo(other.Upstream);
        return c != 0 ? c : Revision.CompareTo(other.Revision);
    }

    /// <summary>Whether <paramref name="candidate"/> is a later version than <paramref name="current"/> (unparsable ones never are).</summary>
    public static bool IsNewer(string candidate, string? current) =>
        TryParse(candidate, out var a) && (current is null || !TryParse(current, out var b) || a.CompareTo(b) > 0);

    public override string ToString() => $"{Upstream}-r{Revision}";
}

/// <summary>
/// A component release: its own tag in the EncoDeck repository, independent of app releases, so an app
/// update neither rebuilds nor re-downloads a component whose pin did not change.
/// </summary>
public sealed record ComponentRelease
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string Tag { get; init; }

    public required ComponentSource Source { get; init; }

    /// <summary>The contract between app and component (LHDC backend commands, A2DPWB pipe protocol); an app takes only its own.</summary>
    public int Interface { get; init; } = 1;

    /// <summary>The oldest app version the component works with (component releases found online are checked against it).</summary>
    public string? MinApp { get; init; }

    public IReadOnlyList<ComponentPackage> Packages { get; init; } = [];

    /// <summary>
    /// The packages are downloaded from the upstream project's own release (their mirrors) only, never from an EncoDeck
    /// component release: A2DP Windows Bridge, whose newer official 2.x builds are taken as they are published.
    /// </summary>
    public bool UpstreamDownloads { get; init; }

    /// <summary>The package for this machine: arm64 Windows gets an arm64 build when there is one (the app itself may run emulated).</summary>
    public ComponentPackage? PackageFor(Architecture os) =>
        Packages.FirstOrDefault(p => p.Architecture == (os == Architecture.Arm64 ? "arm64" : "x64"))
        ?? (os == Architecture.Arm64 ? Packages.FirstOrDefault(p => p.Architecture == "x64") : null);

    public ComponentPackage? CurrentPackage => PackageFor(RuntimeInformation.OSArchitecture);

    /// <summary>The EncoDeck component release first, then the package's mirrors; only the mirrors for <see cref="UpstreamDownloads"/>.</summary>
    public IReadOnlyList<Uri> Sources(ComponentPackage package) => UpstreamDownloads ? package.Mirrors :
        [new Uri($"https://github.com/{ComponentCatalog.Repository}/releases/download/{Uri.EscapeDataString(Tag)}/{Uri.EscapeDataString(package.File)}"), .. package.Mirrors];
}

/// <summary>The component pins built into this app (<c>components.json</c>); online releases are only offered on top of them.</summary>
public static class ComponentCatalog
{
    public const string Repository = "TIANLI0/EncoDeck";
    public const string LhdcId = "lhdc";
    public const string A2dpBridgeId = "a2dpwb";

    /// <summary>The asset of every component release that describes it (the same shape as an entry of components.json).</summary>
    public const string ManifestAsset = "component.json";

    private static readonly Lazy<IReadOnlyList<ComponentRelease>> All = new(() =>
    {
        using var stream = typeof(ComponentCatalog).Assembly.GetManifestResourceStream("EncoDeck.Platform.Components.components.json")
            ?? throw new InvalidOperationException("components.json is not embedded.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static IReadOnlyList<ComponentRelease> Components => All.Value;

    public static ComponentRelease Lhdc => Get(LhdcId);

    public static ComponentRelease A2dpBridge => Get(A2dpBridgeId);

    public static ComponentRelease Get(string id) => Components.FirstOrDefault(c => c.Id == id)
        ?? throw new KeyNotFoundException($"No component '{id}'.");

    /// <summary>Reads and validates the built-in manifest; anything malformed is rejected rather than half-used.</summary>
    internal static IReadOnlyList<ComponentRelease> Parse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Empty component manifest.");
        if (root["schema"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported component manifest schema.");
        var result = (root["components"]?.AsArray() ?? throw new InvalidDataException("No components."))
            .Select(node => ParseComponent(node?.AsObject() ?? throw new InvalidDataException("Invalid component."))).ToList();
        if (result.Select(c => c.Id).Distinct().Count() != result.Count) throw new InvalidDataException("Duplicate component id.");
        return result;
    }

    /// <summary>A release's own <see cref="ManifestAsset"/>: one component object with <c>"schema": 1</c>.</summary>
    internal static ComponentRelease ParseRelease(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Empty component description.");
        if (root["schema"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported component description schema.");
        return ParseComponent(root);
    }

    private static ComponentRelease ParseComponent(JsonObject c)
    {
        string id = Text(c, "id"), version = Text(c, "version"), tag = Text(c, "tag");
        if (!IsName(id) || !IsName(version) || !IsName(tag)) throw new InvalidDataException($"Invalid component identity: {id}.");
        if (!ComponentVersion.TryParse(version, out _) || tag != $"{id}-v{version}") throw new InvalidDataException($"{id}: version and tag must read <x.y.z>-r<n> and {id}-v<version>.");
        var source = c["source"]?.AsObject() ?? throw new InvalidDataException($"{id}: no source.");
        string commit = Text(source, "commit");
        if (commit.Length != 40 || !commit.All(Uri.IsHexDigit)) throw new InvalidDataException($"{id}: invalid source commit.");
        string? minApp = c["minApp"]?.GetValue<string>();
        if (minApp is not null && !SemanticVersion.TryParse(minApp, out _)) throw new InvalidDataException($"{id}: invalid minApp.");
        int contract = c["interface"]?.GetValue<int>() ?? 1;
        if (contract < 1) throw new InvalidDataException($"{id}: invalid interface.");
        var packages = new List<ComponentPackage>();
        foreach (var p in c["packages"]?.AsArray() ?? throw new InvalidDataException($"{id}: no packages."))
        {
            var o = p?.AsObject() ?? throw new InvalidDataException($"{id}: invalid package.");
            string file = Text(o, "file"), sha = (o["sha256"]?.GetValue<string>() ?? "").ToLowerInvariant();
            string root = o["root"]?.GetValue<string>() ?? "";
            if (!IsName(file) || Path.GetFileName(file) != file || !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{id}: invalid package file name.");
            if (sha.Length != 0 && (sha.Length != 64 || !sha.All(Uri.IsHexDigit))) throw new InvalidDataException($"{id}: invalid SHA-256.");
            if (root.Length != 0 && (!IsName(root) || root.Contains("..", StringComparison.Ordinal))) throw new InvalidDataException($"{id}: invalid package root.");
            var mirrors = (o["mirrors"]?.AsArray() ?? []).Select(m => Uri.TryCreate(m?.GetValue<string>(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? uri : throw new InvalidDataException($"{id}: mirrors must be HTTPS URLs.")).ToArray();
            packages.Add(new ComponentPackage
            {
                Architecture = Text(o, "architecture") is var arch && arch is "x64" or "arm64" ? arch : throw new InvalidDataException($"{id}: unknown architecture."),
                File = file,
                Size = o["size"]?.GetValue<long>() is >= 0 and <= 2L * 1024 * 1024 * 1024 and var size ? size : throw new InvalidDataException($"{id}: invalid size."),
                Sha256 = sha,
                Root = root,
                Mirrors = mirrors,
            });
        }
        bool upstream = c["upstreamDownloads"]?.GetValue<bool>() ?? false;
        if (upstream && packages.Any(p => p.Mirrors.Count == 0)) throw new InvalidDataException($"{id}: upstream downloads need a mirror for every package.");
        return new ComponentRelease
        {
            Id = id,
            Version = version,
            Tag = tag,
            Source = new ComponentSource(Text(source, "repository"), commit.ToLowerInvariant(), Text(source, "release")),
            Interface = contract,
            MinApp = minApp,
            Packages = packages,
            UpstreamDownloads = upstream,
        };
    }

    private static string Text(JsonObject o, string name) =>
        o[name]?.GetValue<string>() is { Length: > 0 } value ? value : throw new InvalidDataException($"Missing '{name}'.");

    /// <summary>Tags, versions and file names: letters, digits and . _ - only (no separators, no spaces).</summary>
    private static bool IsName(string value) => value.Length is > 0 and <= 128
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-') && value[0] != '.';
}
