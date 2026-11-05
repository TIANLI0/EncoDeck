// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.A2dpBridge;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Lhdc;
using EncoDeck.Platform.Updates;

namespace EncoDeck.Probe;

/// <summary>
/// Lists the component pins of this build; with --install &lt;id&gt; --root &lt;dir&gt; downloads and unpacks one into a
/// folder of your choice exactly as the app does (pinned digest, mirrors, resume). Never installs drivers.
/// </summary>
internal static class ComponentsCommand
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        foreach (var component in ComponentCatalog.Components)
        {
            Console.WriteLine($"{component.Id} {component.Version} (tag {component.Tag}; {component.Source.Repository} @ {component.Source.Commit[..12]})");
            foreach (var package in component.Packages)
                Console.WriteLine($"  {package.Architecture}: {package.File} {(package.IsPublished ? $"{package.Size} bytes sha256 {package.Sha256}" : "not pinned yet")}");
        }
        if (options.Has("check"))
        {
            // As the Labs page does, from a floor version (default: below everything, so the current releases show up).
            using var client = new HttpClient();
            var updates = new ComponentUpdateClient(client, SemanticVersion.FromAssembly(typeof(ComponentsCommand).Assembly));
            string floor = options.Get("floor") ?? "0.0.0-r0";
            var results = new[]
            {
                (ComponentCatalog.LhdcId, await updates.CheckAsync(ComponentCatalog.LhdcId, LhdcComponent.Interface, floor, options.Has("prerelease"), ct).ConfigureAwait(false)),
                // The official A2DPWB releases, as the Labs page does: the newest 2.x build, betas included.
                (ComponentCatalog.A2dpBridgeId, await updates.CheckUpstreamAsync(ComponentCatalog.A2dpBridge, floor, ct).ConfigureAwait(false)),
            };
            foreach (var (name, result) in results)
            {
                Console.WriteLine($"{name}: {result.Outcome} {result.Release?.Version} {(result.Release is { } r ? $"{r.Source.Release} @ {r.Source.Commit[..Math.Min(12, r.Source.Commit.Length)]}" : "")}");
                foreach (var package in result.Release?.Packages ?? [])
                    Console.WriteLine($"  {package.Architecture}: {package.File} {package.Size} bytes sha256 {package.Sha256} <- {string.Join(", ", result.Release!.Sources(package))}");
            }
        }
        if (options.Get("install") is not { } id) return 0;
        if (options.Get("root") is not { } root)
        {
            Console.Error.WriteLine("--install needs --root <folder> (the app uses %LOCALAPPDATA%\\EncoDeck\\Components\\<name>).");
            return 2;
        }
        using var http = new HttpClient();
        // --upstream: the newest official A2DPWB 2.x build instead of the pin, as the Labs page's update installs it.
        ComponentRelease? bridge = null;
        if (id == ComponentCatalog.A2dpBridgeId && options.Has("upstream"))
        {
            var found = await new ComponentUpdateClient(http, SemanticVersion.FromAssembly(typeof(ComponentsCommand).Assembly))
                .CheckUpstreamAsync(ComponentCatalog.A2dpBridge, "0.0.0-r0", ct).ConfigureAwait(false);
            if (found.Release is null)
            {
                Console.Error.WriteLine($"No official A2DPWB build found: {found.Outcome}.");
                return 3;
            }
            bridge = found.Release;
        }
        var installer = id switch
        {
            ComponentCatalog.LhdcId => LhdcComponent.CreateInstaller(http, Path.GetFullPath(root)),
            ComponentCatalog.A2dpBridgeId => A2dpBridgeComponent.CreateInstaller(http, Path.GetFullPath(root), bridge),
            _ => null,
        };
        if (installer is null)
        {
            Console.Error.WriteLine($"Unknown component '{id}'.");
            return 2;
        }
        long lastMb = -1;
        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            long mb = p.Received / 1048576;
            if (mb == lastMb && p.Received != p.Total) return;
            lastMb = mb;
            Console.WriteLine($"  {p.Received / 1048576.0:F1} / {p.Total / 1048576.0:F1} MB");
        });
        var installed = await installer.InstallAsync(progress, ct).ConfigureAwait(false);
        Console.WriteLine($"Installed {id} {installed.Version} at {installed.Directory} (current: {installer.IsCurrent})");
        return 0;
    }
}
