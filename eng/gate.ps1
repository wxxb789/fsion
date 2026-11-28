#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [ValidateSet('all', 'net10.0', 'net11.0')]
    [string]$Framework = 'all',
    [string]$Filter = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$separator = [IO.Path]::PathSeparator
$frameworks = if ($Framework -eq 'all') { 'net10.0', 'net11.0' } else { $Framework }
$frameworkArgs = @(if ($Framework -ne 'all') { "-p:TargetFrameworks=$Framework" })
$filterArgs = @(if ($Filter) { '--filter'; $Filter })

function Invoke-Checked {
    param([string]$Label, [scriptblock]$Command)

    Write-Host ''
    Write-Host "==> $Label"
    & $Command

    if ($LASTEXITCODE -ne 0) {
        throw "$Label exited with code $LASTEXITCODE"
    }
}

function Assert-LineEndings {
    $eol = & git ls-files --eol
    # w/ is empty for a tracked path deleted from the working tree (for example a
    # goal file moved into the archive), which is not a CRLF problem.
    $badTracked = @($eol | Where-Object { $_ -notmatch '^i/(lf|none)\s+w/(lf|none)?\s' })

    if ($badTracked.Count -gt 0) {
        $badTracked | ForEach-Object { Write-Host "bad tracked eol: $_" }
        throw "git ls-files --eol reported $($badTracked.Count) non-LF entry/entries"
    }

    # Untracked files are not covered by git ls-files --eol; scan the bytes that a
    # commit would pick up so CRLF cannot slip in before the tree is staged.
    $files = & git ls-files -co --exclude-standard
    $badFiles = [System.Collections.Generic.List[string]]::new()

    foreach ($path in $files) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $path))

        if ([Array]::IndexOf($bytes, [byte]0) -ge 0) {
            continue
        }

        if ([Array]::IndexOf($bytes, [byte]13) -ge 0) {
            $badFiles.Add($path)
        }
    }

    if ($badFiles.Count -gt 0) {
        $badFiles | ForEach-Object { Write-Host "CR byte in text file: $_" }
        throw "$($badFiles.Count) text file(s) contain CR bytes"
    }

    Write-Host "line endings: $(@($eol).Count) tracked and $($files.Count) non-ignored files clean"
}

Push-Location $repoRoot

try {
    # VS18's vcvarsall.bat calls vswhere.exe by bare name; without the Installer
    # directory on PATH that error text pollutes ILCompiler 10's findvcvarsall
    # console capture and corrupts the link.exe path. No effect once resolvable.
    if ($IsWindows) {
        $vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'

        if (Test-Path (Join-Path $vsInstaller 'vswhere.exe')) {
            $env:PATH = "$vsInstaller$separator$env:PATH"
        }
    }

    $buildArgs = @('build', 'fsion.slnx', '-c', 'Release') + $frameworkArgs
    Invoke-Checked "dotnet $($buildArgs -join ' ')" {
        dotnet @buildArgs
    }

    $clientName = if ($IsWindows) { 'fsion.exe' } else { 'fsion' }

    foreach ($tfm in $frameworks) {
        Invoke-Checked "dotnet publish src/fsion -c Release -f $tfm" {
            dotnet publish src/fsion -c Release -f $tfm @frameworkArgs
        }

        $publishDir = Join-Path $repoRoot "artifacts/publish/fsion/$tfm"
        Invoke-Checked "build Zig hot-path shim ($tfm)" {
            & (Join-Path $repoRoot 'eng/build-shim.ps1') -OutputDirectory $publishDir
        }

        $native = Join-Path $publishDir $clientName

        if (-not (Test-Path -LiteralPath $native)) {
            throw "native fsion publish output missing: $native"
        }

        Write-Host "native client published: $native"
    }

    foreach ($tfm in $frameworks) {
        $testArgs = @('test', 'tests/Fsion.Tests/Fsion.Tests.fsproj', '-c', 'Release', '-f', $tfm, '--no-build') + $filterArgs
        Invoke-Checked "dotnet $($testArgs -join ' ')" {
            dotnet @testArgs
        }
    }

    Assert-LineEndings

    Write-Host ''
    Write-Host 'GATE PASSED'
    exit 0
}
catch {
    Write-Host ''
    Write-Host "GATE FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
