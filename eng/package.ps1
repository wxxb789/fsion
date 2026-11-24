#!/usr/bin/env pwsh
<#
.SYNOPSIS
Builds and verifies a self-contained fsion distribution bundle for one RID and TFM.

.DESCRIPTION
Publishes the NativeAOT `fsion` client and the framework-dependent `fsiond` daemon
side by side, runs the bundle end to end from a fresh temporary directory (warmup,
script file, helpers, exit code, daemon stop), and archives the result.

NativeAOT cannot cross-compile to another operating system, so `-Runtime` must match
the current host OS; run this script on each OS (the 3-OS GitHub Actions matrix does
exactly that).

.EXAMPLE
pwsh -NoProfile -File eng/package.ps1
pwsh -NoProfile -File eng/package.ps1 -Framework net11.0 -Runtime win-x64
pwsh -NoProfile -File eng/package.ps1 -SkipSmokeTest
#>
[CmdletBinding()]
param(
    [string]$Runtime = '',
    [ValidateSet('net10.0', 'net11.0')]
    [string]$Framework = 'net10.0',
    [string]$OutputDirectory = '',
    [switch]$SkipSmokeTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$separator = [IO.Path]::PathSeparator

function Get-HostRuntimeIdentifier {
    $architecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        'X86' { 'x86' }
        default { throw "unsupported host architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
    }

    if ($IsWindows) { "win-$architecture" }
    elseif ($IsMacOS) { "osx-$architecture" }
    elseif ($IsLinux) { "linux-$architecture" }
    else { throw 'unsupported host operating system' }
}

function Assert-HostRuntime {
    param([string]$RuntimeIdentifier)

    if ($RuntimeIdentifier -notmatch '^(win|linux|osx)-') {
        throw "invalid runtime identifier: $RuntimeIdentifier (expected e.g. win-x64, linux-x64, osx-arm64)"
    }

    $hostOs = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
    $targetOs = $RuntimeIdentifier.Split('-')[0]

    if ($targetOs -ne $hostOs) {
        throw "NativeAOT cannot cross-compile $RuntimeIdentifier from $hostOs; run eng/package.ps1 on the target OS (the 3-OS CI matrix covers win-x64, linux-x64, and osx-arm64)"
    }
}

function Invoke-DotnetPublish {
    param([string]$Label, [string[]]$Arguments)

    Write-Host "==> $Label"
    & dotnet @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "$Label exited with code $LASTEXITCODE"
    }
}

function Invoke-SmokeCommand {
    param(
        [string]$Label,
        [string]$Executable,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [hashtable]$Environment,
        [int[]]$ExpectedExitCodes
    )

    $previous = @{}
    foreach ($key in $Environment.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $Environment[$key], 'Process')
    }

    try {
        Push-Location $WorkingDirectory
        try {
            $output = & $Executable @Arguments 2>&1
            $exitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
    }
    finally {
        foreach ($key in $previous.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process')
        }
    }

    $text = ($output | Out-String).TrimEnd()

    if ($ExpectedExitCodes -notcontains $exitCode) {
        throw "$Label exited with code $exitCode (expected $($ExpectedExitCodes -join ' or ')): $text"
    }

    Write-Host "    $Label -> exit $exitCode"
    if ($text.Length -gt 0) {
        Write-Host "    $text"
    }

    return @{ ExitCode = $exitCode; Output = $text }
}

if (-not $Runtime) {
    $Runtime = Get-HostRuntimeIdentifier
}

Assert-HostRuntime $Runtime

$packageName = "fsion-$Runtime-$Framework"
$packageRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repoRoot 'artifacts/package' }
$stage = Join-Path $packageRoot $packageName
$clientName = if ($Runtime -like 'win-*') { 'fsion.exe' } else { 'fsion' }
$daemonName = if ($Runtime -like 'win-*') { 'fsiond.exe' } else { 'fsiond' }

Write-Host "runtime: $Runtime"
Write-Host "framework: $Framework"
Write-Host "bundle: $stage"

# VS18's vcvarsall.bat calls vswhere.exe by bare name; without the Installer directory on
# PATH that error text pollutes ILCompiler 10's findvcvarsall capture and corrupts link.exe.
if ($IsWindows) {
    $vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'

    if (Test-Path (Join-Path $vsInstaller 'vswhere.exe')) {
        $env:PATH = "$vsInstaller$separator$env:PATH"
    }
}

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}

New-Item -ItemType Directory -Path $stage -Force | Out-Null

Invoke-DotnetPublish "dotnet publish src/fsion -c Release -f $Framework -r $Runtime (NativeAOT)" @(
    'publish', (Join-Path $repoRoot 'src/fsion/fsion.fsproj'),
    '-c', 'Release', '-f', $Framework, '-r', $Runtime, '-o', $stage
)

Invoke-DotnetPublish "dotnet publish src/fsiond -c Release -f $Framework -r $Runtime (framework-dependent ReadyToRun)" @(
    'publish', (Join-Path $repoRoot 'src/fsiond/fsiond.fsproj'),
    '-c', 'Release', '-f', $Framework, '-r', $Runtime, '--self-contained', 'false', '-p:PublishReadyToRun=true', '-o', $stage
)

Get-ChildItem -LiteralPath $stage -Filter '*.pdb' | Remove-Item -Force

$clientPath = Join-Path $stage $clientName
$daemonPath = Join-Path $stage $daemonName

if (-not (Test-Path -LiteralPath $clientPath -PathType Leaf)) {
    throw "packaged client is missing: $clientPath"
}

if (-not (Test-Path -LiteralPath $daemonPath -PathType Leaf)) {
    throw "packaged daemon is missing: $daemonPath"
}

$sep = if ($Runtime -like 'win-*') { '\' } else { '/' }
$prefix = if ($Runtime -like 'win-*') { $clientName } else { "./$clientName" }
$bundleReadme = @"
fsion $Framework for $Runtime
=============================

  $clientName   NativeAOT thin client (no .NET runtime required)
  $daemonName   fsiond daemon (framework-dependent; requires the $Framework runtime)

Run a script from this directory:

  $prefix path${sep}to${sep}script.fsx
  $prefix -e 'printfn "hello"'

fsion finds $daemonName next to itself and auto-starts it on first use.
"@ -replace "`r`n", "`n"

Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Value $bundleReadme -NoNewline -Encoding utf8NoBOM

if (-not $SkipSmokeTest) {
    $smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('fsion-package-smoke-' + [guid]::NewGuid().ToString('N'))
    $smokeBundle = Join-Path $smokeRoot 'bundle'
    $smokeWork = Join-Path $smokeRoot 'work'

    Write-Host "==> smoke test in $smokeRoot"

    try {
        New-Item -ItemType Directory -Path $smokeBundle -Force | Out-Null
        New-Item -ItemType Directory -Path $smokeWork -Force | Out-Null
        Copy-Item -Path (Join-Path $stage '*') -Destination $smokeBundle -Recurse -Force

        $smokeClient = Join-Path $smokeBundle $clientName
        $smokeDaemon = Join-Path $smokeBundle $daemonName

        if (-not (Test-Path -LiteralPath $smokeDaemon -PathType Leaf)) {
            throw "the copied bundle is missing $daemonName next to $clientName"
        }

        $smokeEnvironment = @{
            FSION_PIPE = 'fsion-pkg-' + [guid]::NewGuid().ToString('N').Substring(0, 16)
            FSIOND_PATH = $null
            FSION_PRELUDE = $null
            FSION_CONFIG_DIR = Join-Path $smokeWork '.fsion-config'
            FSION_CACHE_DIR = Join-Path $smokeWork '.fsion-cache'
        }

        $daemonStarted = $false
        try {
            $warmup = Invoke-SmokeCommand 'warmup' $smokeClient @('warmup') $smokeWork $smokeEnvironment @(0)
            $daemonStarted = $true

            if ($warmup.Output -notmatch 'fsiond ready') {
                throw "warmup did not report a ready daemon: $($warmup.Output)"
            }

        $scriptPath = Join-Path $smokeWork 'smoke.fsx'
        $script = @'
open System
open System.Text.Json

let payload = """{"name":"packaged","values":[3,1,2]}"""
let document = JsonDocument.Parse payload
let values =
    [ for element in document.RootElement.GetProperty("values").EnumerateArray() -> element.GetInt32() ]
let total = values |> List.sort |> List.sum
printfn "total=%d" total
printfn "name=%s" (document.RootElement.GetProperty("name").GetString())
printfn "helper=%s" (Fsion.envOr "FSION_SMOKE_HELPER" "fallback")
printfn "cwd=%s" (Fsion.cwd())
'@

        Set-Content -LiteralPath $scriptPath -Value $script -Encoding utf8NoBOM

        $run = Invoke-SmokeCommand 'script file' $smokeClient @($scriptPath) $smokeWork $smokeEnvironment @(0)

        if ($run.Output -notmatch 'total=6') {
            throw "the packaged bundle did not compute the expected result: $($run.Output)"
        }

        $exitRun = Invoke-SmokeCommand 'exit code passthrough' $smokeClient @('-e', 'printfn "before-exit"; exit 7') $smokeWork $smokeEnvironment @(7)

        if ($exitRun.Output -notmatch 'before-exit') {
            throw "exit passthrough did not run the script: $($exitRun.Output)"
        }

        $jsonRun = Invoke-SmokeCommand 'structured failure' $smokeClient @('--json', '-e', 'let broken: int = "nope"') $smokeWork $smokeEnvironment @(1)

        if ($jsonRun.Output -notmatch 'FS0001') {
            throw "structured diagnostics are missing from --json output: $($jsonRun.Output)"
        }

            Invoke-SmokeCommand 'daemon stop' $smokeClient @('daemon', 'stop') $smokeWork $smokeEnvironment @(0) | Out-Null
            $daemonStarted = $false
            Invoke-SmokeCommand 'daemon status after stop' $smokeClient @('daemon', 'status') $smokeWork $smokeEnvironment @(0) | Out-Null

            Write-Host "smoke test passed"
        }
        finally {
            if ($daemonStarted) {
                try {
                    Invoke-SmokeCommand 'daemon stop (cleanup)' $smokeClient @('daemon', 'stop') $smokeWork $smokeEnvironment @(0, 1) | Out-Null
                }
                catch {
                }
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $smokeRoot) {
            Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$archive = if ($IsWindows) {
    Join-Path $packageRoot "$packageName.zip"
} else {
    Join-Path $packageRoot "$packageName.tar.gz"
}

if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}

if ($IsWindows) {
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
} else {
    & tar -czf $archive -C $stage .

    if ($LASTEXITCODE -ne 0) {
        throw "archiving $archive exited with code $LASTEXITCODE"
    }
}

$bundleBytes = (Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object -Property Length -Sum).Sum
$archiveBytes = (Get-Item -LiteralPath $archive).Length

Write-Host ''
Write-Host "bundle ready: $stage"
Write-Host ("bundle size: {0:N1} MB; archive: {1} ({2:N1} MB)" -f ($bundleBytes / 1MB), $archive, ($archiveBytes / 1MB))
Write-Host 'PACKAGE OK'
exit 0
