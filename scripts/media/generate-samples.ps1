# Convenience wrapper. The canonical sample generator lives in tests/media/generate-samples.ps1.
[CmdletBinding()]
param(
    [string]$FfmpegDir,
    [string]$OutDir,
    [switch]$Force
)

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$target = Join-Path $repoRoot 'tests/media/generate-samples.ps1'
$forward = @{}
if ($FfmpegDir) { $forward['FfmpegDir'] = $FfmpegDir }
if ($OutDir)    { $forward['OutDir'] = $OutDir }
if ($Force)     { $forward['Force'] = $true }

& $target @forward
