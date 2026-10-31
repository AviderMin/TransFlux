# Fetches the pinned TransFlux media core (shared GPL build) into tools/ffmpeg and verifies SHA-256.
# A rolling BtbN "latest" build; tools/ffmpeg/core-manifest.json pins the exact binaries by hash.
param(
  [string]$Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip",
  [string]$ExpectedFfmpegSha256 = "CA8204F0E87F90B7CDFE0F7217854733F7B0235F0D76671F82753852A4DEC9F7"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root 'tools/ffmpeg'
$bin = Join-Path $dir 'bin'
New-Item -ItemType Directory -Force $dir | Out-Null
$zip = Join-Path $dir 'ffmpeg-gpl-shared.zip'
if (-not (Test-Path $zip)) { Write-Host "Downloading $Url"; Invoke-WebRequest -Uri $Url -OutFile $zip }
$tmp = Join-Path $dir '_extract'
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
Expand-Archive -Path $zip -DestinationPath $tmp -Force
$srcBin = Get-ChildItem $tmp -Recurse -Directory -Filter bin | Select-Object -First 1
if (!$srcBin) { throw 'bin folder not found in archive' }
if (Test-Path $bin) { Remove-Item -Recurse -Force $bin }
New-Item -ItemType Directory -Force $bin | Out-Null
Get-ChildItem $srcBin.FullName -File | Where-Object { $_.Name -ne 'ffplay.exe' } | ForEach-Object { Copy-Item $_.FullName $bin -Force }
Copy-Item (Join-Path (Split-Path $srcBin.FullName -Parent) 'LICENSE.txt') (Join-Path $dir 'LICENSE.txt') -Force -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $tmp
$ff = Join-Path $bin 'ffmpeg.exe'
$hash = (Get-FileHash $ff -Algorithm SHA256).Hash
Write-Host "ffmpeg SHA256: $hash"
if ($ExpectedFfmpegSha256 -and $hash -ne $ExpectedFfmpegSha256) { throw "Core hash mismatch: expected $ExpectedFfmpegSha256 but got $hash" }
Write-Host "Media core ready at $bin"
