<#
.SYNOPSIS
    Automated batch: runs every input sample through the real VeBeGe filter and
    writes its outputs (incl. the perf log) to a matching out_* folder. Builds
    once, then processes each sample. Add a sample by dropping in_<name>\*.mp4.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\test-samples.ps1
    powershell -ExecutionPolicy Bypass -File .\test-samples.ps1 -Loops 3
#>
[CmdletBinding()]
param(
    [int]$Loops = 2,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

& (Join-Path $root 'build.ps1') -Configuration $Configuration
$exe = Join-Path $root "bin\$Configuration\vebege_testing.exe"

# Each in_<name> folder maps to an out_<name> folder.
$samples = Get-ChildItem -Path $root -Directory -Filter 'in_*'
if (-not $samples) { throw "No in_* sample folders found under $root." }

foreach ($dir in $samples) {
    $name = $dir.Name -replace '^in_', ''
    $video = Get-ChildItem -Path $dir.FullName -File -Include *.mp4, *.mov, *.webm -Recurse | Select-Object -First 1
    if (-not $video) { Write-Warning "No video in $($dir.Name), skipping."; continue }

    $outDir = Join-Path $root "out_$name"
    New-Item -ItemType Directory -Force $outDir | Out-Null

    Write-Host "`n=== $name : $($video.Name) -> out_$name ===" -ForegroundColor Cyan
    & $exe $video.FullName $Loops $outDir
    if ($LASTEXITCODE -ne 0) { throw "Processing failed for $name (exit $LASTEXITCODE)." }
}

# Surface the perf summaries so a run tells you throughput at a glance.
Write-Host "`n=== perf summaries ===" -ForegroundColor Cyan
Get-ChildItem -Path $root -Directory -Filter 'out_*' | ForEach-Object {
    Get-ChildItem -Path $_.FullName -Filter '*_perf.log' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "`n$($_.FullName)"
        Get-Content $_.FullName | Where-Object { $_ -like '#*' }
    }
}
