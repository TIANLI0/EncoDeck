# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
  Checks that every component package pinned in components.json is published in its component
  release with exactly the pinned size and SHA-256. CI runs it before an app release, so an app is
  never published with a pin nobody can download.
#>
[CmdletBinding()]
param([string]$Repository = 'TIANLI0/EncoDeck')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content (Join-Path $root 'src/EncoDeck.Platform/Components/components.json') -Raw | ConvertFrom-Json
$headers = @{ 'Accept' = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'EncoDeck-CI' }
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
$failures = @()
foreach ($component in $manifest.components) {
    if ($component.upstreamDownloads) {
        # Downloaded from the upstream release only (BlueCaster): the pin must match its assets there.
        $upstream = ([Uri]$component.source.repository).AbsolutePath.Trim('/')
        try { $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$upstream/releases/tags/$($component.source.release)" -Headers $headers }
        catch { $failures += "$($component.id): upstream release $upstream $($component.source.release) not found"; continue }
        foreach ($package in $component.packages) {
            $asset = $release.assets | Where-Object name -eq $package.file
            if (-not $asset) { $failures += "$($component.id): $($package.file) missing from $upstream $($component.source.release)"; continue }
            if ($asset.size -ne $package.size) { $failures += "$($component.id): $($package.file) is $($asset.size) bytes upstream, pinned $($package.size)" }
            if ($asset.digest -and $asset.digest -ne "sha256:$($package.sha256)") { $failures += "$($component.id): $($package.file) upstream digest $($asset.digest) differs from the pin" }
            if ($package.mirrors -notcontains $asset.browser_download_url) { $failures += "$($component.id): $($package.file) mirror is not the upstream asset" }
            Write-Host "$($component.id) $($component.version): $($package.file) ok (upstream)"
        }
        continue
    }
    try { $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/tags/$($component.tag)" -Headers $headers }
    catch { $failures += "$($component.id): release $($component.tag) not found"; continue }
    if ($release.draft) { $failures += "$($component.id): release $($component.tag) is a draft" }
    # component.json lets apps released earlier find this release; it must describe exactly the pin.
    $description = $release.assets | Where-Object name -eq 'component.json'
    if (-not $description) { $failures += "$($component.id): component.json missing from $($component.tag)" }
    else {
        $raw = Invoke-WebRequest -Uri $description.browser_download_url -Headers @{ 'User-Agent' = 'EncoDeck-CI' } -UseBasicParsing
        $manifest = [Text.Encoding]::UTF8.GetString($raw.RawContentStream.ToArray()).TrimStart([char]0xFEFF) | ConvertFrom-Json
        if ($manifest.schema -ne 1 -or $manifest.id -ne $component.id -or $manifest.version -ne $component.version -or $manifest.tag -ne $component.tag `
            -or $manifest.interface -ne $component.interface -or $manifest.minApp -ne $component.minApp) {
            $failures += "$($component.id): component.json of $($component.tag) does not describe the pin"
        }
        foreach ($package in $component.packages) {
            $described = $manifest.packages | Where-Object file -eq $package.file
            if (-not $described -or $described.size -ne $package.size -or $described.sha256 -ne $package.sha256) {
                $failures += "$($component.id): component.json lists $($package.file) differently from the pin"
            }
        }
    }
    foreach ($package in $component.packages) {
        if (-not $package.sha256 -or $package.size -le 0) { $failures += "$($component.id): $($package.file) is not pinned"; continue }
        $asset = $release.assets | Where-Object name -eq $package.file
        if (-not $asset) { $failures += "$($component.id): $($package.file) missing from $($component.tag)"; continue }
        if ($asset.size -ne $package.size) { $failures += "$($component.id): $($package.file) is $($asset.size) bytes, pinned $($package.size)" }
        if ($asset.digest -and $asset.digest -ne "sha256:$($package.sha256)") { $failures += "$($component.id): $($package.file) digest $($asset.digest) differs from the pin" }
        Write-Host "$($component.id) $($component.version): $($package.file) ok"
    }
}
if ($failures) { $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }; throw 'Component pins do not match the published component releases.' }
