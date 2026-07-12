<#
.SYNOPSIS
    Builds VeBeGe (Release) and packs it into both an MSIX (Store / sideload)
    and an MSI (quick testing), versioned from version.txt, into Installer\.

.DESCRIPTION
    1. Builds the solution Release x64 (build.ps1).
    2. Stages the runtime files + generated logo assets under msix\staging.
    3. Version comes from version.txt and drives the package versions AND the
       output file names: Installer\VeBeGe-<version>.msix / .msi.
    4. Packs the MSIX with makeappx (Windows 10 SDK).
    5. Builds the MSI with the WiX CLI (auto-installed as a dotnet tool if
       missing). The MSI adds VeBeGe to per-user login startup and launches it
       when the install finishes; on that first run the app opens the setup
       page (https://vebege.io/setup). The MSIX does the same login-startup via
       its startupTask; a per-user MSIX can't auto-launch, so open it from Start
       once and the app takes over from there.

    The MSIX is UNSIGNED. For sideloading, sign it:
        signtool sign /fd SHA256 /a /f yourcert.pfx /p pass Installer\VeBeGe-<ver>_x64.msix
    For the Store, upload unsigned - Partner Center signs it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build-msix.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$version = (Get-Content (Join-Path $root 'version.txt') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "version.txt must be four-part (e.g. 1.0.0.1), got '$version'." }

if (-not $SkipBuild) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Release build failed." }
}

$release   = Join-Path $root 'Release'
$staging   = Join-Path $root 'msix\staging'
$installer = Join-Path $root 'Installer'
New-Item -ItemType Directory -Force $installer | Out-Null
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force "$staging\Assets" | Out-Null

# Runtime payload: the service + everything it stages into ProgramData.
$payload = @(
    'vebege_service.exe',
    'vebege_cam.dll',
    'OpenCvSharp.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Runtime.CompilerServices.Unsafe.dll',
    'OpenCvSharpExtern.dll',
    'opencv_videoio_ffmpeg4110_64.dll',
    'face_detection_yunet_2023mar.onnx',
    'human_segmentation_pphumanseg_2023mar.onnx'
)
foreach ($f in $payload) {
    $p = Join-Path $release $f
    if (-not (Test-Path $p)) { throw "Missing build output: $p" }
    Copy-Item $p $staging
}

# Logo assets generated from icon.png (Store requires these sizes).
Add-Type -AssemblyName System.Drawing
$icon = Join-Path $root 'icon.png'
$sizes = @{ 'Square44x44Logo.png' = 44; 'Square150x150Logo.png' = 150; 'StoreLogo.png' = 50 }
$srcImg = [System.Drawing.Image]::FromFile($icon)
try {
    foreach ($name in $sizes.Keys) {
        $s = $sizes[$name]
        $bmp = New-Object System.Drawing.Bitmap($s, $s)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($srcImg, 0, 0, $s, $s)
        $g.Dispose()
        $bmp.Save((Join-Path "$staging\Assets" $name), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }
} finally { $srcImg.Dispose() }

# --- MSIX -------------------------------------------------------------------
# Real Store identity lives in the git-ignored identity.local.ps1 (the manifest
# ships with PLACEHOLDER values). Load it and stamp identity + version, then pack.
$idFile = Join-Path $root 'msix\identity.local.ps1'
if (-not (Test-Path $idFile)) { throw "Missing $idFile - copy identity.local.ps1.example and fill in the Partner Center values." }
$id = & $idFile
foreach ($k in 'Name','Publisher','PublisherDisplayName') {
    if ([string]::IsNullOrWhiteSpace($id.$k)) { throw "identity.local.ps1 is missing '$k'." }
}

$manifest = Get-Content (Join-Path $root 'msix\Package.appxmanifest') -Raw
$manifest = [regex]::Replace($manifest, '(<Identity\b[^>]*?\bName=")[^"]*(")', "`${1}$($id.Name)`$2")
$manifest = [regex]::Replace($manifest, '(<Identity\b[^>]*?\bPublisher=")[^"]*(")', "`${1}$($id.Publisher)`$2")
$manifest = [regex]::Replace($manifest, '(<Identity\b[^>]*?\bVersion=")[^"]*(")', "`${1}$version`$2")
$manifest = [regex]::Replace($manifest, '(<PublisherDisplayName>)[^<]*(</PublisherDisplayName>)', "`${1}$($id.PublisherDisplayName)`$2")
Set-Content -Path (Join-Path $staging 'AppxManifest.xml') -Value $manifest -Encoding UTF8

# Find makeappx in the newest installed Windows 10/11 SDK.
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeappx = Get-ChildItem -Path $kits -Filter makeappx.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found - install a Windows 10/11 SDK." }

$msixOut = Join-Path $installer "VeBeGe-$version.msix"
if (Test-Path $msixOut) { Remove-Item $msixOut -Force }
& $makeappx.FullName pack /d $staging /p $msixOut /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed (exit $LASTEXITCODE)." }
Write-Host "`nMSIX created: $msixOut (unsigned)"

# --- MSI (WiX) --------------------------------------------------------------
# Per-user install: files under %LOCALAPPDATA%\VeBeGe, a HKCU Run entry for
# login startup, and a launch-on-finish custom action. Fixed component GUIDs
# are auto-assigned by WiX; only the UpgradeCode must stay constant.
# Pin WiX v5: v6+ require accepting the Open Source Maintenance Fee EULA, which
# would make an unattended build fail. v5 uses the same v4 schema this .wxs targets.
$wixVersion = '5.0.2'
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "wix CLI not found - installing WiX $wixVersion (dotnet tool)..."
    & dotnet tool update --global wix --version $wixVersion 2>&1 | Write-Host
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "WiX CLI unavailable. Install it: dotnet tool install --global wix --version $wixVersion (MSIX above still built)."
}

$exeId = 'f_vebege_service_exe'
$comps = ''
foreach ($f in $payload) {
    $id  = 'f_' + ($f -replace '[^A-Za-z0-9_]', '_')
    $src = Join-Path $release $f
    $run = ''
    if ($f -eq 'vebege_service.exe') {
        $run = "`n        <RegistryValue Root=`"HKCU`" Key=`"Software\Microsoft\Windows\CurrentVersion\Run`" Name=`"VeBeGe`" Type=`"string`" Value=`"[#$id]`" />"
    }
    $comps += @"
      <Component Directory="INSTALLFOLDER">
        <File Id="$id" Source="$src" KeyPath="yes" />$run
      </Component>
"@
}

# UpgradeCode: constant identity of the product across versions (git-ignored).
$upgradeCode = $id.MsiUpgradeCode
if ([string]::IsNullOrWhiteSpace($upgradeCode)) { throw "identity.local.ps1 is missing 'MsiUpgradeCode'." }
$wxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="VeBeGe" Manufacturer="VeBeGe" Version="$version" UpgradeCode="$upgradeCode" Scope="perUser" Compressed="yes">
    <MediaTemplate EmbedCab="yes" />
    <MajorUpgrade DowngradeErrorMessage="A newer version of VeBeGe is already installed." />
    <Icon Id="AppIcon" SourceFile="$root\icon.ico" />
    <Property Id="ARPPRODUCTICON" Value="AppIcon" />
    <StandardDirectory Id="LocalAppDataFolder">
      <Directory Id="INSTALLFOLDER" Name="VeBeGe" />
    </StandardDirectory>
    <Feature Id="Main" Title="VeBeGe">
$comps
    </Feature>
    <!-- Launch the app when install finishes; its first run opens the setup page. -->
    <CustomAction Id="LaunchApp" FileRef="$exeId" ExeCommand="" Return="asyncNoWait" Impersonate="yes" />
    <InstallExecuteSequence>
      <Custom Action="LaunchApp" After="InstallFinalize" Condition="NOT Installed" />
    </InstallExecuteSequence>
  </Package>
</Wix>
"@
$wxsPath = Join-Path $installer 'VeBeGe.wxs'
Set-Content -Path $wxsPath -Value $wxs -Encoding UTF8

$msiOut = Join-Path $installer "VeBeGe-$version.msi"
if (Test-Path $msiOut) { Remove-Item $msiOut -Force }
& wix build $wxsPath -arch x64 -o $msiOut
if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)." }
Remove-Item $wxsPath -Force

Write-Host "MSI created : $msiOut"
Write-Host "`nInstallers in $installer"
