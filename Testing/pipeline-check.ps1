<#
.SYNOPSIS
    Self-check for the output stage: vebege_service\VbgPipeline.cs and
    LoadingOverlay.cs, the part the service pump and both Testing tools share.

.DESCRIPTION
    Compiles PipelineCheck.cs (csc, no project) against the built service
    assembly, into its output folder so the OpenCV natives and the AI models
    resolve, then runs it. Asserts the loading screen's schedule, that
    StartupSeconds=0 disables it, and that the pipeline's output stage really
    does outrun the filter (camera rate out, filter rate in). Writes stills of
    the dial (ring000.png ... ring100.png) and loading.mp4 of the whole startup
    as a consuming app sees it, next to the input clip.

    Takes about StartupSeconds + 5 s: the pipeline part runs in real time.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\pipeline-check.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Video,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Video)     { $Video = Join-Path $PSScriptRoot 'in_bbc\bbc.mp4' }
if (-not $OutputDir) { $OutputDir = Split-Path $Video -Parent }
if (-not (Test-Path $Video)) { throw "Video not found: $Video" }

$out = Join-Path $root $Configuration
if (-not (Test-Path (Join-Path $out 'vebege_service.exe'))) {
    throw "Build the solution first ($Configuration): no vebege_service.exe in $out"
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$csc = & $vswhere -latest -requires Microsoft.Component.MSBuild `
                  -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
if (-not $csc) { throw "csc.exe not found (Visual Studio 2019/2022 required)." }

# vebege_check: the name vebege_service grants InternalsVisibleTo.
& $csc /nologo /platform:x64 /out:"$out\vebege_check.exe" `
    /r:"$root\packages\OpenCvSharp4.4.11.0.20250507\lib\net48\OpenCvSharp.dll" `
    /r:"$out\vebege_service.exe" "$PSScriptRoot\PipelineCheck.cs"
if ($LASTEXITCODE -ne 0) { throw "Compile failed." }

# Resolve before the Push-Location: relative paths are the caller's, not $out's.
$videoPath = (Resolve-Path $Video).Path
$outPath = (Resolve-Path $OutputDir).Path

Push-Location $out
try {
    & "$out\vebege_check.exe" $videoPath $outPath
    if ($LASTEXITCODE -ne 0) { throw "Pipeline check FAILED (exit $LASTEXITCODE)." }
} finally { Pop-Location }

Write-Host "Wrote: $OutputDir\ring000.png ... ring100.png, $OutputDir\loading.mp4"
