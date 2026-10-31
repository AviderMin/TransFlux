# tests/media · generated sample media

Deterministic sample media used by integration/QA tests, plus a manifest of
SHA-256 hashes and purposes. No personal or unlicensed media is stored here.

## Generate

~~~
pwsh -File tests/media/generate-samples.ps1
# optional: -FfmpegDir <dir>  -OutDir <dir>  -Force
~~~

The generator uses the pinned FFmpeg core at
tools/ffmpeg/ffmpeg-master-latest-win64-gpl/bin and writes:

- tests/media/samples/...  -- generated media files
- tests/media/manifest.json  -- sampleId, path, SHA-256, size, streams, purpose

Encoding uses -fflags +bitexact -map_metadata -1 -threads 1 for reproducibility
on the same pinned core.

## Samples

| id | description |
|---|---|
| S01 | 1080p SDR BT.709 H.264/AAC 30fps 5s (baseline) |
| S02 | Vertical/portrait 1080x1920 SDR |
| S03 | Non-square SAR 1440x1080 (SAR 4:3 -> DAR 16:9) |
| S04 | Video with no audio track |
| S05 | Audio only (AAC m4a) |
| S06 | 5.1 surround audio |
| S07 | Text (SRT) subtitle in MKV |
| S08 | Truncated/corrupt copy of S01 |
| S09 | Special-character path (Chinese, spaces, ampersand, parentheses) |

## Manifest schema (tests/media/manifest.json)

~~~json
{
  "schemaVersion": 1,
  "generatedUtc": "ISO-8601",
  "generator": "tests/media/generate-samples.ps1",
  "ffmpegVersion": "ffmpeg version ...",
  "samples": [
    {
      "sampleId": "S01",
      "fileName": "S01_....mp4",
      "path": "tests/media/samples/S01_....mp4",
      "purpose": "why this sample exists",
      "streams": "video h264 1920x1080 ...; audio aac stereo 48kHz",
      "sha256": "64 lowercase hex chars",
      "sizeBytes": 123456,
      "probeSummary": "video:h264:1920x1080; audio:aac:2ch@48000Hz",
      "notes": "free-form notes"
    }
  ]
}
~~~

Sample binary files are generated, not hand-authored. If they are absent in a
checkout, run the generator before running media-dependent tests.
