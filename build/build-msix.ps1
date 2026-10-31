# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
  Builds the Microsoft Store package (artifacts\EncoDeck-<version>.msixupload, x64 + ARM64).

.DESCRIPTION
  1. Runs the unit tests (unless -SkipTests).
  2. Publishes the WinUI app self-contained for each architecture.
  3. Adds packaging\Package.appxmanifest and the tile images, packs one .msix per architecture,
     bundles them and wraps the bundle as .msixupload for Partner Center.
  The Store signs the package; the local bundle is unsigned.

.EXAMPLE
  pwsh build\build-msix.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $Version,
    [string[]] $Architectures = @("x64", "arm64"),
    [switch] $SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $props = Get-Content (Join-Path $root "Directory.Build.props") -Raw
    $Version = [regex]::Match($props, "<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value
}
$numeric = ($Version -split "[-+]")[0]
# Store packages require the fourth (revision) field to be 0.
$parts = @($numeric -split "\.") + @("0", "0", "0")
$packageVersion = "$($parts[0]).$($parts[1]).$($parts[2]).0"
Write-Host "EncoDeck $Version (package $packageVersion)"

if (-not $SkipTests) {
    dotnet test tests/EncoDeck.Tests -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }
}

$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
$buildToolsVersion = [regex]::Match((Get-Content Directory.Packages.props -Raw), 'Microsoft\.Windows\.SDK\.BuildTools" Version="([^"]+)"').Groups[1].Value
$makeappx = Get-ChildItem (Join-Path $nugetRoot "microsoft.windows.sdk.buildtools\$buildToolsVersion\bin") -Recurse -Filter makeappx.exe |
    Where-Object { $_.DirectoryName -like "*\x64" } | Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found; run dotnet restore first." }
$makepri = Join-Path $makeappx.DirectoryName "makepri.exe"

$work = [IO.Path]::GetFullPath((Join-Path $root "artifacts\msix"))
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $work "bundle") | Out-Null

# Tile and Store images from the 256 px app icon.
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Image]::FromFile((Join-Path $root "src\EncoDeck.App\Assets\EncoDeck-256.png"))
function Save-Logo([string] $path, [int] $width, [int] $height, [double] $scale) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $size = [int]([Math]::Min($width, $height) * $scale)
    $g.DrawImage($icon, [int](($width - $size) / 2), [int](($height - $size) / 2), $size, $size)
    $g.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

foreach ($arch in $Architectures) {
    $rid = "win-$arch"
    $platform = if ($arch -eq "arm64") { "ARM64" } else { "x64" }
    $layout = Join-Path $work $arch
    dotnet publish src/EncoDeck.App -c $Configuration -r $rid -p:Platform=$platform `
        -p:VersionPrefix=$numeric -p:VersionSuffix="$(if ($Version -match '-(.+)$') { $Matches[1] } else { '' })" `
        -o $layout --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($arch) failed." }
    # The Labs LE Audio bitrate hook needs an elevated injector, which the Store package does not offer.
    Remove-Item (Join-Path $layout "EncoDeck.LeHook.dll") -ErrorAction SilentlyContinue

    # Scale variants plus targetsize *_altform-unplated icons: without the unplated ones the taskbar,
    # Start and Alt+Tab draw the icon on an accent-colored square plate.
    $images = New-Item -ItemType Directory -Force (Join-Path $layout "Images")
    foreach ($scale in 100, 200) {
        $f = $scale / 100
        Save-Logo (Join-Path $images "StoreLogo.scale-$scale.png") (50 * $f) (50 * $f) 1.0
        Save-Logo (Join-Path $images "Square44x44Logo.scale-$scale.png") (44 * $f) (44 * $f) 1.0
        Save-Logo (Join-Path $images "Square150x150Logo.scale-$scale.png") (150 * $f) (150 * $f) 0.66
        Save-Logo (Join-Path $images "Wide310x150Logo.scale-$scale.png") (310 * $f) (150 * $f) 0.66
    }
    foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
        foreach ($form in "", "_altform-unplated", "_altform-lightunplated") {
            Save-Logo (Join-Path $images "Square44x44Logo.targetsize-$size$form.png") $size $size 1.0
        }
    }

    $manifest = Join-Path $layout "AppxManifest.xml"
    (Get-Content packaging\Package.appxmanifest -Raw).Replace('$VERSION$', $packageVersion).Replace('$ARCH$', $arch) |
        Set-Content $manifest -Encoding utf8

    # The shell resolves those qualified names only through resources.pri. Once it exists the app's
    # MRT Core loads it instead of EncoDeck.pri, so merge EncoDeck.pri (XAML + strings) into it. The
    # staging folder keeps the folder indexer away from the rest of the publish output.
    $priStage = Join-Path $work "pri-$arch"
    New-Item -ItemType Directory -Force $priStage | Out-Null
    Copy-Item -LiteralPath $images -Destination $priStage -Recurse
    Copy-Item -LiteralPath (Join-Path $layout "EncoDeck.pri") -Destination $priStage
    Copy-Item -LiteralPath packaging\priconfig.xml -Destination $work -Force
    & $makepri new /pr $priStage /cf (Join-Path $work "priconfig.xml") /mn $manifest /of (Join-Path $layout "resources.pri") /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makepri new ($arch) failed." }

    & $makeappx.FullName pack /o /d $layout /p (Join-Path $work "bundle\EncoDeck_${packageVersion}_$arch.msix")
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack ($arch) failed." }
}
$icon.Dispose()

$bundle = Join-Path $work "EncoDeck_$packageVersion.msixbundle"
& $makeappx.FullName bundle /o /bv $packageVersion /d (Join-Path $work "bundle") /p $bundle
if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed." }

$upload = Join-Path $root "artifacts\EncoDeck-$Version.msixupload"
$zip = "$upload.zip"
Remove-Item -LiteralPath $upload, $zip -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $bundle -DestinationPath $zip
Move-Item -LiteralPath $zip -Destination $upload
Write-Host "Store upload: $upload"
