# SPDX-License-Identifier: GPL-3.0-only
# Builds EncoDeck.LeHook.dll (x64) with MinGW-w64 gcc; it imports only kernel32, advapi32 and msvcrt. Called by
# EncoDeck.App's build. Without gcc the build goes on (the Labs card then reports the component missing), except on
# CI, where a release must contain the DLL.
param([string]$OutDir = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'
$gcc = (Get-Command gcc -ErrorAction SilentlyContinue).Source
if (-not $gcc) {
    $gcc = @('C:\mingw64\bin\gcc.exe', 'C:\msys64\mingw64\bin\gcc.exe', 'C:\msys64\ucrt64\bin\gcc.exe', 'D:\msys2\mingw64\bin\gcc.exe') |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $gcc) {
    if ($env:CI) { throw 'MinGW-w64 gcc not found: EncoDeck.LeHook.dll cannot be built.' }
    Write-Warning 'MinGW-w64 gcc not found: skipping EncoDeck.LeHook.dll (the LE Audio bitrate card will report it missing).'
    exit 0
}
# gcc runs its own tools from its folder.
$env:PATH = (Split-Path $gcc) + ';' + $env:PATH
New-Item -ItemType Directory -Force $OutDir | Out-Null
$out = Join-Path $OutDir 'EncoDeck.LeHook.dll'
& $gcc -O2 -Wall -Wextra -Werror -shared -s -static-libgcc -o $out (Join-Path $PSScriptRoot 'lehook.c') -ladvapi32
if ($LASTEXITCODE -ne 0) { throw "gcc failed ($LASTEXITCODE)" }
Write-Output $out
