using System.Buffers.Binary;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;
using TransFlux.Engine.FFmpeg.Core;
using TransFlux.Engine.FFmpeg.Processes;
using TransFlux.Engine.FFmpeg.Probe;

namespace TransFlux.Engine.FFmpeg.Verifier;

/// <summary>
/// Verifies a produced file against the plan's declared expectations: stream/codec/count,
/// dimensions, pixel format, duration tolerance, MP4 faststart ordering and a sampled decode
/// of first/middle/last (full decode when requested). A passing exit code alone is never enough.
/// </summary>
public sealed class FfmpegVerifier : IOutputVerifier
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly FfmpegCorePaths _core;

    public FfmpegVerifier(FfmpegCorePaths core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
    }

    public async Task<VerificationResult> VerifyAsync(VerificationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ct.IsCancellationRequested) ThrowCanceled(ct);
        var expected = request.Plan.ExpectedOutput;
        var outputPath = request.OutputPath;

        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
            return VerificationResult.Fail(FailureInfo.Of(FailureCode.VerifyFailed, "verify.missing", $"Output file missing: '{outputPath}'."), "Output file missing.");

        long size;
        try { size = new FileInfo(outputPath).Length; }
        catch (Exception ex) { return VerificationResult.Fail(FailureInfo.Of(FailureCode.VerifyFailed, "verify.unreadable", ex.Message), "Output file unreadable."); }
        if (size <= 0)
            return VerificationResult.Fail(FailureInfo.Of(FailureCode.VerifyFailed, "verify.empty", "Output file is empty."), "Output file is empty.");

        var probe = new FfmpegProbe(_core, ProbeTimeout);
        var probeResult = await probe.ProbeAsync(new SourceReference { AbsolutePath = outputPath }, ct).ConfigureAwait(false);
        if (probeResult.IsCanceled) ThrowCanceled(ct);
        if (!probeResult.IsSuccess || probeResult.Value is null)
            return VerificationResult.Fail(
                probeResult.Failure ?? FailureInfo.Of(FailureCode.VerifyFailed, "verify.probe_failed", "ffprobe could not read output."),
                "ffprobe could not read output file.");

        var actual = probeResult.Value;
        var problems = Compare(expected, actual, outputPath);

        if (problems.Count == 0)
        {
            var decode = await SampleDecodeAsync(outputPath, actual.DurationUs, request.FullDecode, ct).ConfigureAwait(false);
            if (decode.Canceled) ThrowCanceled(ct);
            if (!decode.Ok) problems.Add(decode.Error ?? "sampled decode failed");
        }

        var summary = problems.Count == 0
            ? $"Verified {expected.Container.DisplayName()}: {actual.VideoStreams.Count()} video / {actual.AudioStreams.Count()} audio, {size} bytes."
            : "Verification failed: " + string.Join("; ", problems);

        if (problems.Count > 0)
            return new VerificationResult
            {
                Passed = false,
                Summary = summary,
                Failure = FailureInfo.Of(FailureCode.VerifyFailed, "verify.mismatch", string.Join("; ", problems)),
                ActualSnapshot = actual,
            };

        return VerificationResult.Ok(summary, actual);
    }

    internal static List<string> Compare(ExpectedOutput expected, SourceSnapshot actual, string outputPath)
    {
        var problems = new List<string>();
        var video = actual.PrimaryVideo;
        var audio = actual.PrimaryAudio;

        var videoCount = actual.VideoStreams.Count();
        var audioCount = actual.AudioStreams.Count();
        var streamCount = actual.Streams.Count;
        if (expected.VideoStreamCount != videoCount)
            problems.Add($"video stream count {videoCount} != expected {expected.VideoStreamCount}");
        if (expected.AudioStreamCount != audioCount)
            problems.Add($"audio stream count {audioCount} != expected {expected.AudioStreamCount}");
        var attachmentCount = actual.Streams.Count(s => s.Kind == MediaKind.Attachment);
        if (expected.AttachmentStreamCount != attachmentCount)
            problems.Add($"attachment stream count {attachmentCount} != expected {expected.AttachmentStreamCount}");
        if (expected.StreamCount != streamCount)
            problems.Add($"stream count {streamCount} != expected {expected.StreamCount}");
        var expectedSubtitleCount = expected.StreamCount - expected.VideoStreamCount - expected.AudioStreamCount
            - expected.AttachmentStreamCount;
        var subtitleCount = actual.SubtitleStreams.Count();
        if (expectedSubtitleCount >= 0 && expectedSubtitleCount != subtitleCount)
            problems.Add($"subtitle stream count {subtitleCount} != expected {expectedSubtitleCount}");

        CheckLanguages(problems, "audio", expected.AudioLanguages, actual.AudioStreams.ToList());
        CheckLanguages(problems, "subtitle", expected.SubtitleLanguages, actual.SubtitleStreams.ToList());

        if (expected.VideoCodec is { } videoCodec && videoCodec != VideoCodec.Copy)
        {
            var want = videoCodec.ProbeName();
            if (video is null) problems.Add($"expected {want} video stream but none present");
            else if (!string.Equals(video.Codec, want, StringComparison.OrdinalIgnoreCase))
                problems.Add($"video codec '{video.Codec}' != expected '{want}'");
        }

        if (expected.AudioCodec is { } audioCodec)
        {
            var want = audioCodec == AudioCodec.Copy ? null : audioCodec.ProbeName();
            if (want is not null)
            {
                if (audio is null) problems.Add($"expected {want} audio stream but none present");
                else foreach (var track in actual.AudioStreams)
                    if (!string.Equals(track.Codec, want, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"audio codec '{track.Codec}' != expected '{want}'");
            }
            else if (audioCodec == AudioCodec.Copy && audio is null && expected.AudioStreamCount > 0)
            {
                problems.Add("expected copied audio stream but none present");
            }
        }

        if (expected.Width is int expectedWidth && video is not null && video.Width != expectedWidth)
            problems.Add($"width {video.Width?.ToString() ?? "?"} != expected {expectedWidth}");
        if (expected.Height is int expectedHeight && video is not null && video.Height != expectedHeight)
            problems.Add($"height {video.Height?.ToString() ?? "?"} != expected {expectedHeight}");
        if (expected.PixelFormat is not null && video is not null
            && !string.Equals(video.PixelFormat, expected.PixelFormat, StringComparison.OrdinalIgnoreCase))
            problems.Add($"pixel format '{video.PixelFormat}' != expected '{expected.PixelFormat}'");

        // Colour passthrough spot checks (null = not asserted).
        if (video is not null)
        {
            CheckColour(problems, "color primaries", expected.ColorPrimaries, video.ColorPrimaries);
            CheckColour(problems, "color transfer", expected.ColorTransfer, video.ColorTransfer);
            CheckColour(problems, "color space", expected.ColorSpace, video.ColorSpace);
            CheckColour(problems, "color range", expected.ColorRange, video.ColorRange);
        }

        if (expected.FastStart && expected.Container is ContainerFormat.Mp4 or ContainerFormat.Mov or ContainerFormat.M4a
            && !IsMoovBeforeMdat(outputPath))
            problems.Add("ISO BMFF output is not faststart (moov box not before mdat)");

        var wantContainer = expected.Container switch
        {
            ContainerFormat.Mp4 or ContainerFormat.Mov or ContainerFormat.M4a => "mov",
            ContainerFormat.Mkv or ContainerFormat.Webm => "matroska",
            ContainerFormat.Ogg or ContainerFormat.Opus => "ogg",
            ContainerFormat.Mp3 => "mp3", ContainerFormat.Flac => "flac", ContainerFormat.Wav => "wav", ContainerFormat.Aac => "aac",
            _ => null,
        };
        if (wantContainer is not null && !actual.Format.FormatName.Split(',').Contains(wantContainer, StringComparer.OrdinalIgnoreCase))
            problems.Add($"container '{actual.Format.FormatName}' != expected '{wantContainer}'");

        if (expected.DurationUs is long expectedUs)
        {
            if (actual.DurationUs is long actualUs)
            {
                var toleranceUs = DurationToleranceUs(expected, video, audio);
                var delta = Math.Abs(actualUs - expectedUs);
                if (delta > toleranceUs)
                    problems.Add($"duration {actualUs} us differs from expected {expectedUs} us by {delta} us (tolerance {toleranceUs} us)");
            }
            else if (RequiresKnownDuration(expected.Container))
            {
                problems.Add($"duration is unknown; expected {expectedUs} us");
            }
        }

        return problems;
    }

    private static void CheckColour(List<string> problems, string label, string? expectedValue, string? actualValue)
    {
        if (string.IsNullOrWhiteSpace(expectedValue)) return;
        var actual = actualValue?.Trim();
        if (string.Equals(actual, expectedValue.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        // "unspecified" range is treated as limited/tv by every decoder; the pinned core
        // cannot always write the tv marker back for Matroska sources.
        if (label == "color range" && expectedValue == "tv" && string.IsNullOrEmpty(actual)) return;
        problems.Add($"{label} '{actualValue ?? "unspecified"}' != expected '{expectedValue}'");
    }

    private static void CheckLanguages(List<string> problems, string label, IReadOnlyList<string> expected, IReadOnlyList<StreamDescriptor> actual)
    {
        if (expected.Count == 0) return;
        for (var i = 0; i < expected.Count; i++)
        {
            var want = expected[i];
            if (string.IsNullOrWhiteSpace(want) || want == "und") continue;
            var got = i < actual.Count ? actual[i].Language : null;
            if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{label} track {i} language '{got ?? "und"}' != expected '{want}'");
        }
    }

    private static bool RequiresKnownDuration(ContainerFormat container) =>
        container is not (ContainerFormat.Aac or ContainerFormat.Mp3);

    private static long DurationToleranceUs(ExpectedOutput expected, StreamDescriptor? video, StreamDescriptor? audio)
    {
        var frameDuration = 1.0 / 30.0;
        var rate = video?.AverageFrameRate ?? video?.FrameRate;
        if (rate is { } r && r.ToDouble() > 0) frameDuration = 1.0 / r.ToDouble();

        var toleranceSeconds = Math.Max(0.5, 2.0 * frameDuration);
        if (expected.AudioStreamCount > 0 || audio is not null)
            toleranceSeconds += 0.1; // AAC priming / encoder padding
        return (long)(toleranceSeconds * 1_000_000d);
    }

    /// <summary>Scans top-level ISO BMFF boxes to confirm moov precedes mdat (faststart).</summary>
    internal static bool IsMoovBeforeMdat(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[16];
            long position = 0;
            var length = stream.Length;
            bool? moovFirst = null;
            while (position + 8 <= length)
            {
                stream.Position = position;
                if (stream.Read(header, 0, 8) < 8) break;
                long boxSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
                var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                long headerSize = 8;
                if (boxSize == 1)
                {
                    if (stream.Read(header, 8, 8) < 8) break;
                    boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                    headerSize = 16;
                }
                else if (boxSize == 0)
                {
                    boxSize = length - position;
                }

                if (type == "moov" && moovFirst is null) moovFirst = true;
                else if (type == "mdat" && moovFirst is null) moovFirst = false;

                if (moovFirst is not null && (type == "mdat" || type == "moov")) { /* keep scanning for both */ }

                if (boxSize < headerSize) break;
                position += boxSize;
            }
            return moovFirst ?? false;
        }
        catch
        {
            return false;
        }
    }

    internal async Task<(bool Ok, bool Canceled, string? Error)> SampleDecodeAsync(string path, long? durationUs, bool full, CancellationToken ct)
    {
        if (full)
        {
            var fullArgs = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-i", path, "-f", "null", "-" };
            var fullResult = await ProcessCapture.RunAsync(_core.FfmpegPath, fullArgs, TimeSpan.FromMinutes(10), 256 * 1024, 64 * 1024, ct).ConfigureAwait(false);
            if (fullResult.Canceled) return (false, true, null);
            return fullResult.TimedOut
                ? (false, false, "full decode timed out")
                : fullResult.ExitCode == 0 ? (true, false, null) : (false, false, $"full decode failed: {Trim(fullResult.Stderr)}");
        }

        var points = new List<TimeSpan> { TimeSpan.Zero };
        if (durationUs is long durUs && durUs > 2_000_000)
        {
            var dur = TimeSpan.FromMicroseconds(durUs);
            points.Add(TimeSpan.FromTicks(dur.Ticks / 2));
            var last = dur - TimeSpan.FromMilliseconds(600);
            if (last > points[1]) points.Add(last);
        }

        foreach (var point in points)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
            if (point > TimeSpan.Zero)
            {
                args.Add("-ss");
                args.Add(point.TotalSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
            }
            args.Add("-i"); args.Add(path);
            args.Add("-t"); args.Add("0.5");
            args.Add("-f"); args.Add("null"); args.Add("-");

            var result = await ProcessCapture.RunAsync(_core.FfmpegPath, args, TimeSpan.FromSeconds(20), 128 * 1024, 64 * 1024, ct).ConfigureAwait(false);
            if (result.Canceled) return (false, true, null);
            if (result.TimedOut) return (false, false, $"sampled decode at {point.TotalSeconds:0.###}s timed out");
            if (result.ExitCode != 0) return (false, false, $"sampled decode at {point.TotalSeconds:0.###}s failed: {Trim(result.Stderr)}");
        }
        return (true, false, null);
    }

    /// <summary>
    /// Cancellation must surface as OCE so JobScheduler's existing catch marks the attempt/job
    /// canceled; returning Passed=false would be misread as an ordinary verification failure.
    /// </summary>
    private static void ThrowCanceled(CancellationToken ct) =>
        throw new OperationCanceledException("Verification canceled.", ct);

    private static string Trim(string value) => value.Length <= 500 ? value : value[^500..];
}
