<#
.SYNOPSIS
    Runs the real VeBeGe filter live on a selected webcam and shows the panes
    (processed, mask, background, tier2, heat) in OpenCV windows. Always builds
    first (incremental) so it never tests stale filter code. Per-frame
    performance is logged to vebege_live_perf.log beside the exe.

.PARAMETER Camera
    DirectShow device index. Omit to be prompted with the device list.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\live.ps1
    powershell -ExecutionPolicy Bypass -File .\live.ps1 -Camera 1
#>
[CmdletBinding()]
param(
    [int]$Camera = -1,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$exe = Join-Path $root "bin\$Configuration\vebege_live.exe"
& (Join-Path $root 'build.ps1') -Configuration $Configuration

$callArgs = @()
if ($Camera -ge 0) { $callArgs += $Camera }

& $exe @callArgs
if ($LASTEXITCODE -ne 0) { throw "Live test failed (exit $LASTEXITCODE)." }
