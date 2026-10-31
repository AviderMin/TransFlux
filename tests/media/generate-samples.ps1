[CmdletBinding()]
param(
    [string]$FfmpegDir,
    [string]$OutDir,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $FfmpegDir) { $FfmpegDir = Join-Path $repoRoot 'tools/ffmpeg/ffmpeg-master-latest-win64-gpl/bin' }
if (-not $OutDir)    { $OutDir    = Join-Path $repoRoot 'tests/media/samples' }
$ManifestPath = Join-Path $repoRoot 'tests/media/manifest.json'

$script:FfmpegExe  = Join-Path $FfmpegDir 'ffmpeg.exe'
$script:FfprobeExe = Join-Path $FfmpegDir 'ffprobe.exe'
if (-not (Test-Path -LiteralPath $script:FfmpegExe))  { throw "ffmpeg.exe not found at $($script:FfmpegExe)" }
if (-not (Test-Path -LiteralPath $script:FfprobeExe)) { throw "ffprobe.exe not found at $($script:FfprobeExe)" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Deterministic, metadata-free output options appended before the output path.
$det = @('-fflags', '+bitexact', '-map_metadata', '-1', '-threads', '1')

function Invoke-Ffmpeg {
    param([string[]]$Arguments)
    & $script:FfmpegExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg failed (exit $LASTEXITCODE): ffmpeg $($Arguments -join ' ')"
    }
}

function Get-RelativePath {
    param([string]$BasePath, [string]$TargetPath)
    $baseUri   = [System.Uri](([System.IO.Path]::GetFullPath($BasePath).TrimEnd('\')) + '\')
    $targetUri = [System.Uri]([System.IO.Path]::GetFullPath($TargetPath))
    $relative  = $baseUri.MakeRelativeUri($targetUri).ToString()
    return ([System.Uri]::UnescapeDataString($relative)) -replace '\\', '/'
}

function Get-ProbeSummary {
    param([string]$Path)
    try {
        $raw = & $script:FfprobeExe -v error -print_format json -show_format -show_streams $Path 2>$null | Out-String
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) { return 'probe_error' }
        $obj = $raw | ConvertFrom-Json
        $parts = foreach ($s in $obj.streams) {
            $desc = '{0}:{1}' -f $s.codec_type, $s.codec_name
            if ($s.width)       { $desc += (':{0}x{1}' -f $s.width, $s.height) }
            if ($s.channels)    { $desc += (':{0}ch' -f $s.channels) }
            if ($s.sample_rate) { $desc += ('@{0}Hz' -f $s.sample_rate) }
            $desc
        }
        return ($parts -join '; ')
    } catch {
        return 'probe_error'
    }
}

$entries = New-Object System.Collections.Generic.List[object]

function Add-Sample {
    param(
        [string]$SampleId,
        [string]$FullPath,
        [string]$Purpose,
        [string]$Streams,
        [string]$Notes
    )
    if (-not (Test-Path -LiteralPath $FullPath)) { throw "expected sample not produced: $FullPath" }
    $hash = (Get-FileHash -LiteralPath $FullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item -LiteralPath $FullPath).Length
    $entry = [ordered]@{
        sampleId     = $SampleId
        fileName     = [System.IO.Path]::GetFileName($FullPath)
        path         = (Get-RelativePath -BasePath $repoRoot -TargetPath $FullPath)
        purpose      = $Purpose
        streams      = $Streams
        sha256       = $hash
        sizeBytes    = $size
        probeSummary = (Get-ProbeSummary -Path $FullPath)
        notes        = $Notes
    }
    $entries.Add($entry)
    Write-Host ("  [{0}] {1} ({2:N0} bytes) sha256={3}" -f $SampleId, $entry.fileName, $size, $hash)
}

$videoColor = @('-color_primaries', 'bt709', '-color_trc', 'bt709', '-colorspace', 'bt709')

Write-Host "Generating deterministic samples into $OutDir"

# S01 - 1080p SDR BT.709 H.264/AAC 30fps 5s (basic CPU path)
$s01 = Join-Path $OutDir 'S01_1080p_sdr_bt709_h264_aac_5s.mp4'
Invoke-Ffmpeg -Arguments (@(
    '-hide_banner', '-y', '-nostdin', '-loglevel', 'error',
    '-f', 'lavfi', '-i', 'testsrc2=size=1920x1080:rate=30',
    '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=5',
    '-t', '5', '-map', '0:v:0', '-map', '1:a:0',
    '-vf', 'format=yuv420p', '-pix_fmt', 'yuv420p',
    '-c:v', 'libx264', '-preset', 'medium', '-crf', '23'
) + $videoColor + @(
    '-c:a', 'aac', '-b:a', '128k', '-ac', '2',
    '-movflags', '+faststart'
) + $det + @($s01))
Add-Sample -SampleId 'S01' -FullPath $s01 -Purpose '1080p SDR BT.709 H.264/AAC 30fps 5s baseline for CPU/GPU transcode paths' -Streams 'video h264 1920x1080 yuv420p bt709; audio aac stereo 48kHz' -Notes 'Generated from lavfi testsrc2 + sine; no personal media.'

# S02 - vertical portrait 1080x1920 SDR
$s02 = Join-Path $OutDir 'S02_vertical_1080x1920_sdr_h264_aac_5s.mp4'
Invoke-Ffmpeg -Arguments (@(
    '-hide_banner', '-y', '-nostdin', '-loglevel', 'error',
    '-f', 'lavfi', '-i', 'testsrc2=size=1080x1920:rate=30',
    '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=5',
    '-t', '5', '-map', '0:v:0', '-map', '1:a:0',
    '-vf', 'format=yuv420p', '-pix_fmt', 'yuv420p',
    '-c:v', 'libx264', '-preset', 'medium', '-crf', '23'
) + $videoColor + @(
    '-c:a', 'aac', '-b:a', '128k', '-ac', '2',
    '-movflags', '+faststart'
) + $det + @($s02))
Add-Sample -SampleId 'S02' -FullPath $s02 -Purpose 'Vertical/portrait 1080x1920 SDR sample for display-orientation handling' -Streams 'video h264 1080x1920 yuv420p bt709; audio aac stereo 48kHz' -Notes 'Portrait source; verifies rotation/display-size computation downstream.'

# S03 - non-square SAR 1440x1080 (SAR 4:3 -> DAR 16:9)
$s03 = Join-Path $OutDir 'S03_nonsquare_sar_1440x1080_sdr_h264_aac_5s.mp4'
Invoke-Ffmpeg -Arguments (@(
    '-hide_banner', '-y', '-nostdin', '-loglevel', 'error',
    '-f', 'lavfi', '-i', 'testsrc2=size=1440x1080:rate=30',
    '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=5',
    '-t', '5', '-map', '0:v:0', '-map', '1:a:0',
    '-vf', 'setsar=4/3,format=yuv420p', '-pix_fmt', 'yuv420p',
    '-c:v', 'libx264', '-preset', 'medium', '-crf', '23'
) + $videoColor + @(
    '-c:a', 'aac', '-b:a', '128k', '-ac', '2',
    '-movflags', '+faststart'
) + $det + @($s03))
Add-Sample -SampleId 'S03' -FullPath $s03 -Purpose 'Non-square sample aspect ratio (1440x1080 SAR 4:3 -> DAR 16:9)' -Streams 'video h264 1440x1080 SAR 4:3 yuv420p bt709; audio aac stereo 48kHz' -Notes 'Exercises square-pixel / DAR preservation rules.'

# S04 - video with no audio track
$s04 = Join-Path $OutDir 'S04_no_audio_1080p_h264_5s.mp4'
Invoke-Ffmpeg -Arguments (@(
    '-hide_banner', '-y', '-nostdin', '-loglevel', 'error',
    '-f', 'lavfi', '-i', 'testsrc2=size=1920x1080:rate=30',
    '-t', '5', '-an',
    '-vf', 'format=yuv420p', '-pix_fmt', 'yuv420p',
    '-c:v', 'libx264', '-preset', 'medium', '-crf', '23'
) + $videoColor + @(
    '-movflags', '+faststart'
) + $det + @($s04))
Add-Sample -SampleId 'S04' -FullPath $s04 -Purpose 'Video with no audio stream; verifies audio-extract blocking and silent transcode' -Streams 'video h264 1920x1080 yuv420p bt709; no audio' -Notes 'No-audio sample.'

# S05 - audio only (AAC m4a)
$s05 = Join-Path $OutDir 'S05_audio_only_aac_5s.m4a'
Invoke-Ffmpeg -Arguments (@(
    '-hide_banner', '-y', '-nostdin', '-loglevel', 'error',
    '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=5',
    '-t', '5', '-vn',
    '-c:a', 'aac', '-b:a', '128k', '-ac', '2',
    '-movflags', '+faststart'
) + $det + @($s05))
Add-Sample -SampleId 'S05' -FullPath $s05 -Purpose 'Audio-only source; verifies audio extraction and video-transcode blocking' -Streams 'audio aac stereo 48kHz; no video' -Notes 'Audio-only sample.'

# S06 - 5.1 surround audio in mkv
$s06 = Join-Path $OutDir 'S06_5.1_surround_h264_aac_5s.mkv'
Invoke-Ffmpeg -Arguments (@(
    '-hide_banner', '-y', '-nostdin', '-loglevel', 'error',
    '-f', 'lavfi', '-i', 'testsrc2=size=1920x1080:rate=30',
    '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=5',
    '-t', '5', '-map', '0:v:0', '-map', '1:a:0',
    '-vf', 'format=yuv420p', '-pix_fmt', 'yuv420p',
    '-c:v', 'libx264', '-preset', 'medium', '-crf', '23'
) + $videoColor + @(
    '-af', 'pan=5.1|FL=c0|FR=c0|FC=c0|LFE=c0|BL=c0|BR=c0',
    '-c:a', 'aac', '-b:a', '384k', '-ac', '6', '-channel_layout', '5.1'
) + $det + @($s06))
Add-Sample -SampleId 'S06' -FullPath $s06 -Purpose '5.1 surround audio; verifies channel-layout mapping and A/V sync' -Streams 'video h264 1920x1080 yuv420p bt709; audio aac 5.1 48kHz' -Notes 'Multichannel sample for docs/09 S10-style checks.'

# S07 - video + text subtitle (SRT) in mkv
$tempSrt = Join-Path $OutDir '_tmp_S07.srt'
Set-Content -LiteralPath $tempSrt -Encoding utf8NoBOM -Value @"
1
00:00:00,500 --> 00:00:02,000
TransFlux subtitle sample

2
00:00:02,500 --> 00:00:04,500
Second cue - 测试字幕
"@
$s07 = Join-Path $OutDir 'S07_text_subtitle_h264_aac_srt_5s.mkv'
try {
    Invoke-Ffmpeg -Arguments (@(
        '-hide_banner', '-y', '-nostdin', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=30',
        '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=5',
        '-i', $tempSrt,
        '-t', '5', '-map', '0:v:0', '-map', '1:a:0', '-map', '2:s:0',
        '-vf', 'format=yuv420p', '-pix_fmt', 'yuv420p',
        '-c:v', 'libx264', '-preset', 'medium', '-crf', '23'
    ) + $videoColor + @(
        '-c:a', 'aac', '-b:a', '128k', '-ac', '2',
        '-c:s', 'srt'
    ) + $det + @($s07))
} finally {
    Remove-Item -LiteralPath $tempSrt -Force -ErrorAction SilentlyContinue
}
Add-Sample -SampleId 'S07' -FullPath $s07 -Purpose 'MKV with a text (SRT/subrip) subtitle stream; remux subtitle compatibility' -Streams 'video h264 1280x720 yuv420p bt709; audio aac stereo; subtitle subrip' -Notes 'Text subtitle sample; bitmap subtitles are not included in first release.'

# S08 - truncated/corrupt copy of S01 (fixed fraction, deterministic)
$s01Path = Join-Path $OutDir 'S01_1080p_sdr_bt709_h264_aac_5s.mp4'
$s08 = Join-Path $OutDir 'S08_truncated_from_S01.mp4'
$inStream = [System.IO.File]::OpenRead($s01Path)
try {
    $keep = [int][Math]::Max(1024, [Math]::Floor($inStream.Length * 0.4))
    $buffer = New-Object byte[] 65536
    $outStream = [System.IO.File]::Create($s08)
    try {
        $remaining = $keep
        while ($remaining -gt 0) {
            $toRead = [int][Math]::Min($buffer.Length, $remaining)
            $read = $inStream.Read($buffer, 0, $toRead)
            if ($read -le 0) { break }
            $outStream.Write($buffer, 0, $read)
            $remaining -= $read
        }
    } finally { $outStream.Dispose() }
} finally { $inStream.Dispose() }
Add-Sample -SampleId 'S08' -FullPath $s08 -Purpose 'Truncated/corrupt container for decode-failure isolation and error handling' -Streams 'invalid (first 40% of S01 bytes)' -Notes 'Truncated from S01 at 40% of byte length; MP4 faststart keeps the header readable, so ffprobe may parse it but full decode fails.'

# S09 - special-character path (Chinese, spaces, ampersand, parentheses)
$s09Dir = Join-Path $OutDir '特殊 目录 & test'
New-Item -ItemType Directory -Force -Path $s09Dir | Out-Null
$s09 = Join-Path $s09Dir 'S09 空格 中文字符 & test (1).mp4'
Copy-Item -LiteralPath $s01Path -Destination $s09 -Force
Add-Sample -SampleId 'S09' -FullPath $s09 -Purpose 'Special-character path: Chinese, spaces, ampersand, parentheses' -Streams 'copy of S01 (video h264 1920x1080; audio aac stereo)' -Notes 'Verifies path handling with non-ASCII and shell-special characters.'

$ffmpegVersion = (& $script:FfmpegExe -hide_banner -version 2>$null | Select-Object -First 1)
$manifest = [ordered]@{
    schemaVersion = 1
    generatedUtc  = (Get-Date).ToUniversalTime().ToString('o')
    generator     = 'tests/media/generate-samples.ps1'
    ffmpegVersion = $ffmpegVersion
    samples       = $entries.ToArray()
}
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ManifestPath -Encoding utf8NoBOM

Write-Host ""
Write-Host ("Wrote manifest with {0} samples to {1}" -f $entries.Count, $ManifestPath)
