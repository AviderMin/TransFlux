# Fetches the pinned TransFlux media core into tools/ffmpeg and verifies its SHA-256.
# This is a rolling BtbN "latest" build; tools/ffmpeg/core-manifest.json pins the exact
# binaries by hash. For a reproducible release, substitute a fixed release tag/URL and
# update the manifest. The application never downloads a core at runtime.
param(
  [string]$Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
  [string]$ExpectedFfmpegSha256 = "C16F1190C3CF7236A3B9D9A49EC2C71D029531A8E438C04ABDE59F7B5B5DDB95"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root 'tools/ffmpeg'
New-Item -ItemType Directory -Force $dir | Out-Null
$zip = Join-Path $dir 'ffmpeg-gpl.zip'
if (-not (Test-Path $zip)) {
  Write-Host "Downloading $Url"
  Invoke-WebRequest -Uri $Url -OutFile $zip
}
Expand-Archive -Path $zip -DestinationPath $dir -Force
$ff = Get-ChildItem -Recurse -Filter ffmpeg.exe $dir | Select-Object -First 1
if (-not $ff) { throw 'ffmpeg.exe not found after extraction.' }
$hash = (Get-FileHash $ff.FullName -Algorithm SHA256).Hash
Write-Host "ffmpeg SHA256: $hash"
if ($ExpectedFfmpegSha256 -and $hash -ne $ExpectedFfmpegSha256) {
  throw "Core hash mismatch: expected $ExpectedFfmpegSha256 but got $hash"
}
Write-Host "Media core ready at $($ff.Directory.FullName)"
