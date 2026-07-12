<#
.SYNOPSIS
    Builds the VeBeGe Testing tools: vebege_testing.exe (mp4 offline test) and
    vebege_live.exe (real-time webcam test).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found. Install Visual Studio 2019/2022 (with the C++ and .NET desktop workloads)."
}
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild `
                      -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found. Add the .NET desktop workload in the VS Installer." }

# Platform=AnyCPU: the csprojs target x64 internally (matches the OpenCV natives).
foreach ($proj in 'vebege_testing.csproj', 'vebege_live.csproj') {
    & $msbuild (Join-Path $root $proj) `
        /t:Build /p:Configuration=$Configuration /p:Platform=AnyCPU /m /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $proj (exit $LASTEXITCODE)." }
}

Write-Host "`nBuilt: $(Join-Path $root "bin\$Configuration\vebege_testing.exe")"
Write-Host "Built: $(Join-Path $root "bin\$Configuration\vebege_live.exe")"
