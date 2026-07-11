<#
.SYNOPSIS
    Runs a video through the real VeBeGe filter and writes the four MP4s
    (processed, mask, background, heat). Always builds first (incremental,
    so it's fast when nothing changed) to never test stale filter code.

.PARAMETER Video
    Input video (any format OpenCV can read: mp4, mov, ...).

.PARAMETER Loops
    How many times to loop the input back-to-back. Default 2.

.PARAMETER OutputDir
    Where to write the outputs. Default: the input video's folder.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\process.ps1 -Video "C:\clips\test.mov"
    powershell -ExecutionPolicy Bypass -File .\process.ps1 -Video test.mp4 -Loops 3 -OutputDir .\out
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Video,
    [int]$Loops = 2,
    [string]$OutputDir,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not (Test-Path $Video)) { throw "Video not found: $Video" }

$exe = Join-Path $root "bin\$Configuration\vebege_testing.exe"
& (Join-Path $root 'build.ps1') -Configuration $Configuration

$callArgs = @((Resolve-Path $Video).Path, $Loops)
if ($OutputDir) { $callArgs += $OutputDir }

& $exe @callArgs
if ($LASTEXITCODE -ne 0) { throw "Processing failed (exit $LASTEXITCODE)." }
