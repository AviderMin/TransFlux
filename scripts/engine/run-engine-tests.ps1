# TransFlux engine verification: confirms the pinned core identity/hash and runs the
# real-core integration tests. Mirrors the module definition-of-done commands.
param(
  [string]$Configuration = "Debug"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$manifestPath = Join-Path $root "tools/ffmpeg/core-manifest.json"
if (-not (Test-Path $manifestPath)) { throw "core-manifest.json not found at $manifestPath" }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

$ff = Join-Path $root $manifest.ffmpeg.relativePath
$fp = Join-Path $root $manifest.ffprobe.relativePath
if (-not (Test-Path $ff)) { throw "ffmpeg not found: $ff (run scripts/fetch-ffmpeg.ps1)" }
if (-not (Test-Path $fp)) { throw "ffprobe not found: $fp" }

$ffHash = (Get-FileHash $ff -Algorithm SHA256).Hash
$fpHash = (Get-FileHash $fp -Algorithm SHA256).Hash
Write-Host "ffmpeg : $(& $ff -hide_banner -version | Select-Object -First 1)"
Write-Host "ffprobe: $(& $fp -hide_banner -version | Select-Object -First 1)"
Write-Host "ffmpeg  sha256: $ffHash"
Write-Host "ffprobe sha256: $fpHash"
if ($ffHash -ne $manifest.ffmpeg.sha256) { throw "ffmpeg hash mismatch: expected $($manifest.ffmpeg.sha256) got $ffHash" }
if ($fpHash -ne $manifest.ffprobe.sha256) { throw "ffprobe hash mismatch: expected $($manifest.ffprobe.sha256) got $fpHash" }
Write-Host "Core integrity OK."

Write-Host "
== dotnet build =="
dotnet build "src/TransFlux.Engine.FFmpeg/TransFlux.Engine.FFmpeg.csproj" -c $Configuration -v m
if ($LASTEXITCODE -ne 0) { throw "engine build failed" }

Write-Host "
== dotnet test =="
dotnet test "tests/TransFlux.Engine.IntegrationTests/TransFlux.Engine.IntegrationTests.csproj" -c $Configuration -v n
if ($LASTEXITCODE -ne 0) { throw "engine integration tests failed" }
Write-Host "
Engine verification complete."
