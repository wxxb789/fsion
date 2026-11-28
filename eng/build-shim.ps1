#!/usr/bin/env pwsh
<#
.SYNOPSIS
Builds the cross-platform Zig hot-path shim (`fsion` / `fsion.exe`) next to the NativeAOT F# fallback (`fsion-cli` / `fsion-cli.exe`).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$Runtime = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$shimSource = Join-Path $repoRoot 'src/fsion-shim/main.zig'

function Resolve-ZigBinary {
    $cmd = Get-Command zig -ErrorAction SilentlyContinue
    if ($cmd) {
        # Skip unconfigured mise shims that fail when zig isn't pinned in mise.toml.
        & $cmd.Source version *> $null
        if ($LASTEXITCODE -eq 0) { return $cmd.Source }
    }

    $miseRoots = @()
    if ($env:MISE_DATA_DIR) { $miseRoots += $env:MISE_DATA_DIR }
    $miseRoots += (Join-Path $HOME '.local/share/mise')
    if ($env:LOCALAPPDATA) { $miseRoots += (Join-Path $env:LOCALAPPDATA 'mise') }
    $miseRoots += 'Q:/.tools/mise/data'

    foreach ($root in $miseRoots) {
        foreach ($name in @('zig.exe', 'zig')) {
            $candidate = Join-Path $root "installs/zig/0.17.1/$name"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }

    throw "zig compiler not found on PATH (install zig 0.17.1 or run 'mise use zig@0.17.1')"
}

function Get-ZigTarget {
    param([string]$Rid)
    if (-not $Rid) {
        $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
            'X64' { 'x86_64' }
            'Arm64' { 'aarch64' }
            default { throw "unsupported architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
        }
        if ($IsWindows) { return @{ Target = "$arch-windows"; LinkLibc = $false; IsWin = $true } }
        elseif ($IsMacOS) { return @{ Target = "$arch-macos"; LinkLibc = $true; IsWin = $false } }
        else { return @{ Target = "$arch-linux"; LinkLibc = $false; IsWin = $false } }
    }

    switch ($Rid) {
        'win-x64' { return @{ Target = 'x86_64-windows'; LinkLibc = $false; IsWin = $true } }
        'win-arm64' { return @{ Target = 'aarch64-windows'; LinkLibc = $false; IsWin = $true } }
        'linux-x64' { return @{ Target = 'x86_64-linux'; LinkLibc = $false; IsWin = $false } }
        'linux-arm64' { return @{ Target = 'aarch64-linux'; LinkLibc = $false; IsWin = $false } }
        'osx-x64' { return @{ Target = 'x86_64-macos'; LinkLibc = $true; IsWin = $false } }
        'osx-arm64' { return @{ Target = 'aarch64-macos'; LinkLibc = $true; IsWin = $false } }
        default { throw "unsupported RID for Zig shim: $Rid" }
    }
}

$zig = Resolve-ZigBinary
$spec = Get-ZigTarget -Rid $Runtime
$clientName = if ($spec.IsWin) { 'fsion.exe' } else { 'fsion' }
$fallbackName = if ($spec.IsWin) { 'fsion-cli.exe' } else { 'fsion-cli' }

$clientPath = Join-Path $OutputDirectory $clientName
$fallbackPath = Join-Path $OutputDirectory $fallbackName

if (-not (Test-Path -LiteralPath $clientPath -PathType Leaf)) {
    throw "expected NativeAOT client before installing Zig shim: $clientPath"
}

Move-Item -LiteralPath $clientPath -Destination $fallbackPath -Force

$zigArgs = @(
    'build-exe', $shimSource,
    '-target', $spec.Target,
    '-OReleaseSmall',
    '-fstrip',
    "-femit-bin=$clientPath"
)
if ($spec.LinkLibc) {
    $zigArgs += '-lc'
}

& $zig @zigArgs
if ($LASTEXITCODE -ne 0) {
    throw "zig build-exe failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $clientPath -PathType Leaf)) {
    throw "Zig shim output missing: $clientPath"
}

$size = (Get-Item -LiteralPath $clientPath).Length
Write-Host "zig shim ready: $clientPath ($size bytes; fallback: $fallbackName)"
