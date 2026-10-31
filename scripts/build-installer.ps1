# Builds the TransFlux Inno Setup installer (per-user, bundles Windows App Runtime).
# Requires: .NET 10 SDK, Inno Setup 6 (ISCC.exe), network for FFmpeg + Windows App Runtime on first run.
param(
  [string]$Version = "1.0.0.1",
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$stageApp = Join-Path $root 'installer/stage/app'
$stagePrereq = Join-Path $root 'installer/stage/prereq'
$ffmpegBin = Join-Path $root 'tools/ffmpeg/ffmpeg-master-latest-win64-gpl/bin'

function Find-Iscc {
  $cands = @(
    (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
  )
  foreach ($c in $cands) { if (Test-Path $c) { return $c } }
  $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  throw 'ISCC.exe (Inno Setup 6) not found. Install Inno Setup 6.'
}

# 1) media core
if (-not (Test-Path (Join-Path $ffmpegBin 'ffmpeg.exe'))) {
  Write-Host 'Fetching pinned media core...'
  & (Join-Path $root 'scripts/fetch-ffmpeg.ps1')
}

# 2) publish self-contained .NET, framework-dependent Windows App SDK, unpackaged
Write-Host "Publishing TransFlux $Version ..."
if (Test-Path $stageApp) { Remove-Item -Recurse -Force $stageApp }
dotnet publish (Join-Path $root 'src/TransFlux.App/TransFlux.App.csproj') -c $Configuration -r $Runtime `
  --self-contained true -p:WindowsPackageType=None -p:PublishReadyToRun=false -p:PublishTrimmed=false `
  -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version -o $stageApp | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# 3) stage ffmpeg beside the exe with an installed-layout manifest
$dest = Join-Path $stageApp 'ffmpeg'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $ffmpegBin 'ffmpeg.exe') $dest -Force
Copy-Item (Join-Path $ffmpegBin 'ffprobe.exe') $dest -Force
$m = Get-Content (Join-Path $root 'tools/ffmpeg/core-manifest.json') -Raw | ConvertFrom-Json
$m.ffmpeg.relativePath = 'ffmpeg.exe'
$m.ffprobe.relativePath = 'ffprobe.exe'
$m.reproducibilityNote = 'Installed layout: ffmpeg.exe/ffprobe.exe beside this manifest under <install>\ffmpeg.'
$m | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $dest 'core-manifest.json') -Encoding utf8

# 4) Windows App Runtime prerequisite installer (for clean machines)
New-Item -ItemType Directory -Force $stagePrereq | Out-Null
$warp = Join-Path $stagePrereq 'WindowsAppRuntimeInstall-x64.exe'
if (-not (Test-Path $warp)) {
  Write-Host 'Downloading Windows App Runtime installer...'
  Invoke-WebRequest -Uri 'https://aka.ms/windowsappsdk/2.6/latest/windowsappruntimeinstall-x64.exe' -OutFile $warp
}

# 5) setup icon
Copy-Item (Join-Path $root 'src/TransFlux.App/Assets/AppIcon.ico') (Join-Path $root 'installer/stage/appicon.ico') -Force

# 6) compile
$iscc = Find-Iscc
Write-Host "Compiling installer with $iscc ..."
& $iscc (Join-Path $root 'installer/transflux.iss') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
Write-Host "Done: artifacts/installer/TransFlux-Setup-$Version.exe"

