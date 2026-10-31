# SPDX-License-Identifier: GPL-3.0-only
# CI: makes sure MinGW-w64 gcc is available for src/EncoDeck.LeHook/build.ps1 (EncoDeck.LeHook.dll). GitHub's Windows
# images ship MSYS2 at C:\msys64; its mingw64 toolchain is installed when no gcc is found.
$ErrorActionPreference = 'Stop'
$known = @('C:\mingw64\bin\gcc.exe', 'C:\msys64\mingw64\bin\gcc.exe', 'C:\msys64\ucrt64\bin\gcc.exe')
$gcc = (Get-Command gcc -ErrorAction SilentlyContinue).Source
if (-not $gcc) { $gcc = $known | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $gcc) {
    $bash = 'C:\msys64\usr\bin\bash.exe'
    if (-not (Test-Path $bash)) { throw 'Neither MinGW-w64 gcc nor MSYS2 found.' }
    & $bash -lc 'pacman -S --noconfirm --needed mingw-w64-x86_64-gcc'
    if ($LASTEXITCODE -ne 0) { throw "pacman failed ($LASTEXITCODE)" }
    $gcc = 'C:\msys64\mingw64\bin\gcc.exe'
    if (-not (Test-Path $gcc)) { throw 'gcc still missing after installing mingw-w64-x86_64-gcc.' }
}
& $gcc --version | Select-Object -First 1
