# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
  Mirrors the official BlueCaster (A2DP Windows Bridge up to 2.0.x) portable builds pinned in components.json into EncoDeck's
  a2dpwb-v<version> component release, byte for byte.

.DESCRIPTION
  Downloads each pinned package from the upstream release, refuses anything whose size or SHA-256
  differs from the pin, and (with -Publish) creates the component release with the unchanged files.
  EncoDeck neither rebuilds nor modifies BlueCaster; the upstream release stays the app's fallback source.
#>
[CmdletBinding()]
param([string]$OutputDirectory, [switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pin = (Get-Content (Join-Path $root 'src/EncoDeck.Platform/Components/components.json') -Raw | ConvertFrom-Json).components | Where-Object id -eq 'a2dpwb'
$tag = $pin.tag
if ($tag -ne "a2dpwb-v$($pin.version)") { throw "The A2DPWB tag must be a2dpwb-v$($pin.version), not $tag." }
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root ('artifacts/a2dpwb-mirror/' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force $output | Out-Null
$files = foreach ($package in $pin.packages) {
    $upstream = @($package.mirrors | Where-Object { $_ -like 'https://github.com/SeiyaFunaokaJP/BlueCaster/releases/download/*' -or $_ -like 'https://github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge/releases/download/*' })[0]
    if (-not $upstream) { throw "$($package.file): no upstream release URL in the pin." }
    $target = Join-Path $output $package.file
    Invoke-WebRequest -Uri $upstream -OutFile $target -UseBasicParsing
    $size = (Get-Item -LiteralPath $target).Length
    $sha = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($size -ne $package.size -or $sha -ne $package.sha256) { throw "$($package.file) does not match its pin ($size bytes, $sha)." }
    "$sha  $($package.file)" | Set-Content ($target + '.sha256') -Encoding ascii
    Write-Host "$($package.file): $size bytes, sha256 $sha (matches the pin)"
    $target
    $target + '.sha256'
}
# What apps released earlier read to find this component without an app update.
$files = @($files) + @(& (Join-Path $PSScriptRoot 'component-manifest.ps1') -Id a2dpwb -OutputDirectory $output)
if ($Publish) {
    & gh release view $tag --repo TIANLI0/EncoDeck *> $null
    if ($LASTEXITCODE -eq 0) { throw "Component release $tag exists already." }
    $notes = @"
Unmodified official BlueCaster $($pin.source.release) portable builds, mirrored for EncoDeck's Labs page.

Source: $($pin.source.repository) at commit $($pin.source.commit) (see licenses/README.txt inside each archive).
BlueCaster is Apache-2.0 OR MIT; the distributed bluecaster-core.exe statically links BTstack, licensed for personal, non-commercial use only. See the licenses folder inside the archive.
"@
    # An upstream pre-release (2.0.2-beta.10-r1) is published as one: apps without pre-release updates skip it.
    $options = @('--repo', 'TIANLI0/EncoDeck', '--title', "BlueCaster component $($pin.version)", '--notes', $notes, '--latest=false')
    if (($pin.version -replace '-r\d+$', '').Contains('-')) { $options += '--prerelease' }
    & gh release create $tag @files @options
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish the BlueCaster component release.' }
}
