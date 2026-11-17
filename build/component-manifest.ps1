# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
  Writes component.json for a component release: its entry of src/EncoDeck.Platform/Components/components.json
  with "schema": 1, and (with -Archive) the size and SHA-256 of a freshly built package.

.DESCRIPTION
  Every component release carries this file. Apps already released find later component releases through it
  (component updates without an app update) and accept one only when its interface, minimum app version and
  package sizes / digests agree with the release's own assets. With -Upload it is attached to the existing
  release of the component's tag (replacing an older component.json).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('lhdc')][string] $Id,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $Archive,
    [switch] $Upload
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pin = (Get-Content (Join-Path $root 'src/EncoDeck.Platform/Components/components.json') -Raw | ConvertFrom-Json).components | Where-Object id -eq $Id
if (-not $pin) { throw "No component '$Id' in components.json." }
if ($Archive) {
    $name = [IO.Path]::GetFileName($Archive)
    $package = $pin.packages | Where-Object file -eq $name
    if (-not $package) { throw "$name is not a package of $Id." }
    $package.size = (Get-Item -LiteralPath $Archive).Length
    $package.sha256 = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
}
foreach ($package in $pin.packages) {
    if (-not $package.sha256 -or $package.size -le 0) { throw "$($package.file) has no size and SHA-256 yet." }
}
$manifest = [ordered]@{ schema = 1 }
foreach ($property in $pin.PSObject.Properties) { $manifest[$property.Name] = $property.Value }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$path = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'component.json'
# UTF-8 without a byte order mark, in Windows PowerShell 5.1 as in PowerShell 7.
[IO.File]::WriteAllText($path, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
if ($Upload) {
    & gh release upload $pin.tag $path --clobber --repo TIANLI0/EncoDeck
    if ($LASTEXITCODE -ne 0) { throw "Could not attach component.json to $($pin.tag)." }
}
$path
