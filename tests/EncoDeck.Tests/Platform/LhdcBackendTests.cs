// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Lhdc;

namespace EncoDeck.Tests.Platform;

public sealed class LhdcBackendTests
{
    [Theory]
    [InlineData(22050, 24, 400)]
    [InlineData(48000, 32, 400)]
    [InlineData(48000, 24, -1)]
    public void RejectsUnsupportedFormatsBeforeStartingDeployment(int rate, int bits, int kbps) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LhdcBackend.ModeCommand("C:/runtime", "BTHENUM\\device", true, new(rate, bits, kbps), "C:/result.json"));

    [Fact]
    public void QuotesDeviceAndPathsAsLiterals()
    {
        string command = LhdcBackend.ModeCommand("C:/O'Brien/runtime", "BTHENUM\\device'; Write-Host injected; 'x", true, new(), "C:/O'Brien/result.json");
        Assert.Contains("C:/O''Brien", command);
        Assert.Contains("-DeviceInstance 'BTHENUM\\device''; Write-Host injected; ''x'", command);
        Assert.Contains("-Mode LHDC", command);
    }

    private static string Runtime(out string script)
    {
        string root = Path.Combine(Path.GetTempPath(), "encodeck-lhdc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        Directory.CreateDirectory(Path.Combine(root, "runtime", "powershell"));
        script = Path.Combine(root, "scripts", "Set-AudioMode.ps1");
        File.WriteAllText(script, "param()");
        File.WriteAllText(Path.Combine(root, "runtime", "powershell", "pwsh.exe"), "pwsh");
        // As build/build-lhdc.ps1 writes it: every file but the list itself, forward slashes, upper-case hashes.
        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => new
        {
            Path = Path.GetRelativePath(root, f).Replace('\\', '/'),
            SHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))),
        });
        File.WriteAllText(Path.Combine(root, "checksums.json"), System.Text.Json.JsonSerializer.Serialize(entries));
        return root;
    }

    [Fact]
    public void AnUnchangedRuntimeVerifies()
    {
        string root = Runtime(out _);
        try { LhdcComponent.VerifyFiles(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("no list")]
    public async Task AnAlteredRuntimeIsNotRunAsAdministrator(string change)
    {
        string root = Runtime(out string script);
        try
        {
            switch (change)
            {
                case "changed": File.AppendAllText(script, "; Start-Process calc"); break;
                // pwsh.exe would load a DLL placed next to it.
                case "added": File.WriteAllText(Path.Combine(root, "runtime", "powershell", "version.dll"), "x"); break;
                case "removed": File.Delete(script); break;
                default: File.Delete(Path.Combine(root, "checksums.json")); break;
            }
            Assert.Throws<InvalidDataException>(() => LhdcComponent.VerifyFiles(root));
            // The deployment stops before the administrator prompt (it would otherwise fail to start "pwsh.exe").
            var backend = new LhdcBackend(root, Path.Combine(root, "..", Path.GetFileName(root) + "-results"));
            await Assert.ThrowsAsync<InvalidDataException>(() => backend.ApplyAsync("BTHENUM\\device", true, new()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            string results = root + "-results";
            if (Directory.Exists(results)) Directory.Delete(results, recursive: true);
        }
    }

    [Fact]
    public async Task QueryCannotInvokeMutatingCommands()
    {
        var backend = new LhdcBackend("missing", "missing");
        await Assert.ThrowsAsync<ArgumentException>(() => backend.QueryAsync(null, "hires", "on"));
    }
}
