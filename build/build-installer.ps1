# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
  Builds EncoDeck and produces the Windows installer (EncoDeck-Setup-<version>-win-x64.exe).

.DESCRIPTION
  1. Runs the unit tests (unless -SkipTests).
  2. Publishes the WinUI app for win-x64 twice: self-contained into artifacts\EncoDeck-win-x64 and framework-dependent
     into artifacts\EncoDeck-win-x64-shared, the installer's two variants (setup installs the shared one where .NET 10
     and the Windows App Runtime are already present). -NoPublish: uses both folders as CI published them, so the zip
     and the installer carry the same files.
  3. Code signing, only when the TIANLI0/codesign action has signed in (release builds in CI; it sets
     CODESIGN_SIGN and CODESIGN_THUMBPRINT): EncoDeck's own EncoDeck*.exe / EncoDeck*.dll, then FluentSetup signs the
     setup and its uninstall.exe. The .NET, Windows App SDK and NuGet libraries are not ours and stay as they are.
  4. Builds the installer with FluentSetup (installer\fluentsetup.json). The fluentsetup CLI comes from
     $env:FLUENTSETUP or PATH (https://github.com/TIANLI0/FluentSetup/releases; CI uses the TIANLI0/FluentSetup action).
     The .sha256 next to it is computed after signing.
  Device images and animations are downloaded by the installed app through EncoAPI.

.EXAMPLE
  pwsh build\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $Version,
    [switch] $SkipTests,
    [switch] $NoPublish,
    [switch] $WithLhdc,
    [string] $LhdcPowerShellDirectory
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $props = Get-Content (Join-Path $root "Directory.Build.props") -Raw
    $Version = [regex]::Match($props, "<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value
}
$numeric = ($Version -split "[-+]")[0]
while (($numeric -split "\.").Count -lt 4) { $numeric += ".0" }
Write-Host "EncoDeck $Version ($numeric)"

if (-not $SkipTests) {
    dotnet test tests/EncoDeck.Tests -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }
}

# The installer's two variants (installer\fluentsetup.json): self-contained (.NET and Windows App SDK included) and
# shared runtimes (framework-dependent; setup installs the runtimes when they are missing).
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root "artifacts")) + [IO.Path]::DirectorySeparatorChar
$builds = @(
    @{ Path = [IO.Path]::GetFullPath((Join-Path $root "artifacts\EncoDeck-win-x64")); Shared = $false },
    @{ Path = [IO.Path]::GetFullPath((Join-Path $root "artifacts\EncoDeck-win-x64-shared")); Shared = $true }
)
$publish = $builds[0].Path
foreach ($build in $builds) {
    $folder = $build.Path
    if (-not $folder.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected publish directory: $folder" }
    # Replacing the folder under a running copy would delete only the files it does not lock.
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith("$folder\", [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw "EncoDeck is running from $folder (PID $($running.Id -join ', ')). Close it first; the build replaces that folder." }
    if ($NoPublish) {
        if (-not (Test-Path -LiteralPath (Join-Path $folder 'EncoDeck.exe'))) { throw "-NoPublish: $folder has no EncoDeck.exe." }
    } else {
        if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
        $runtimeArgs = if ($build.Shared) { @('-p:SelfContained=false', '-p:WindowsAppSDKSelfContained=false') } else { @() }
        dotnet publish src/EncoDeck.App -c $Configuration -r win-x64 -p:Platform=x64 @runtimeArgs `
            -p:VersionPrefix=$((($Version -split "[-+]")[0])) -p:VersionSuffix="$(if ($Version -match '-(.+)$') { $Matches[1] } else { '' })" `
            -o $folder --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
    }

    $required = @("EncoDeck.exe", "EncoDeck.pri", "EncoDeck.LeHook.dll", "LICENSE", "THIRD_PARTY_NOTICES.md", "Assets\EncoDeck.ico", "Assets\EncoDeck-48.png")
    # Only the self-contained build carries the Windows App SDK; the shared one must not (it would shadow the runtime).
    if ($build.Shared) {
        if (Test-Path (Join-Path $folder "Microsoft.ui.xaml.dll")) { throw "$folder is not framework-dependent (has Microsoft.ui.xaml.dll)." }
    } else {
        $required += "Microsoft.WindowsAppRuntime.dll", "Microsoft.ui.xaml.dll"
    }
    foreach ($file in $required) {
        if (-not (Test-Path (Join-Path $folder $file))) { throw "Publish output $folder is missing $file." }
    }
    Copy-Item README.md, README_EN.md $folder -ErrorAction SilentlyContinue

    if ($WithLhdc) {
        # The pinned LHDC component (its own version, not the app's), for an offline installer.
        & (Join-Path $PSScriptRoot 'build-lhdc.ps1') -Destination (Join-Path $folder 'LHDC') -PowerShellDirectory $LhdcPowerShellDirectory
        if (-not $?) { throw 'LHDC component build failed.' }
    }
}

$fluentsetup = if ($env:FLUENTSETUP) { $env:FLUENTSETUP } else { (Get-Command fluentsetup -ErrorAction SilentlyContinue).Source }
if (-not $fluentsetup) { throw "fluentsetup not found. Install FluentSetup (https://github.com/TIANLI0/FluentSetup/releases) or set FLUENTSETUP to fluentsetup.exe." }

$signArgs = @()
if ($env:CODESIGN_SIGN) {
    foreach ($build in $builds) {
        & $env:CODESIGN_SIGN -Root $build.Path -Path 'EncoDeck*.exe', 'EncoDeck*.dll' -Description 'EncoDeck' -DescriptionUrl 'https://github.com/TIANLI0/EncoDeck'
    }
    $signArgs = @('--sign', '-D', "CodesignThumbprint=$env:CODESIGN_THUMBPRINT")
}

$output = Join-Path $root "artifacts\installer"
& $fluentsetup build installer\fluentsetup.json -D "Version=$Version" @signArgs -o $output
if ($LASTEXITCODE -ne 0) { throw "Building the installer failed." }

$setup = Get-ChildItem $output -Filter "EncoDeck-Setup-$Version-win-x64.exe" | Select-Object -First 1
$hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash
"{0}  {1}" -f $hash, $setup.Name | Set-Content (Join-Path $output "$($setup.Name).sha256") -Encoding ascii
Write-Host ("Installer: {0} ({1:N1} MB)`nSHA-256:   {2}" -f $setup.FullName, ($setup.Length / 1MB), $hash)
