# Fetch the exact archive and validate every shipped executable/DLL before replacing any core file.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$coreRoot = Join-Path $repoRoot 'tools/ffmpeg'
$manifest = Get-Content -LiteralPath (Join-Path $coreRoot 'core-manifest.json') -Raw | ConvertFrom-Json
$entries = @($manifest.ffmpeg, $manifest.ffprobe) + @($manifest.runtimeFiles)
function Test-CoreFile($path, $hash) {
    return (Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $hash
}
if (!$Force) {
    $ready = $true
    foreach ($entry in $entries) { if (!(Test-CoreFile (Join-Path $coreRoot $entry.relativePath) $entry.sha256)) { $ready = $false; break } }
    if ($ready) { Write-Output 'Pinned media core already verified.'; return }
}
$downloads = Join-Path $repoRoot 'artifacts/core-downloads'
$null = New-Item -ItemType Directory -Path $downloads -Force
$archivePath = Join-Path $downloads ([Uri]$manifest.sourceUrl).Segments[-1]
if (!(Test-CoreFile $archivePath $manifest.archiveSha256)) {
    Invoke-WebRequest -Uri $manifest.sourceUrl -OutFile $archivePath
}
if (!(Test-CoreFile $archivePath $manifest.archiveSha256)) { throw 'Media archive SHA-256 mismatch; existing core has not been changed.' }
$stage = Join-Path $downloads ('stage-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage
& tar.exe -xf $archivePath -C $stage "$($manifest.archiveRoot)/bin" "$($manifest.archiveRoot)/LICENSE"
if ($LASTEXITCODE -ne 0) { throw 'Media archive extraction failed.' }
$extracted = Join-Path $stage $manifest.archiveRoot
foreach ($entry in $entries) {
    if (!(Test-CoreFile (Join-Path $extracted $entry.relativePath) $entry.sha256)) { throw "Extracted core SHA-256 mismatch: $($entry.relativePath)" }
}
foreach ($entry in $entries) {
    $target = Join-Path $coreRoot $entry.relativePath
    $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
    Copy-Item -LiteralPath (Join-Path $extracted $entry.relativePath) -Destination $target -Force
}
Copy-Item -LiteralPath (Join-Path $extracted 'LICENSE') -Destination (Join-Path $coreRoot 'LICENSE.txt') -Force
Write-Output "Verified media core ready: $($manifest.ffmpeg.version)"
