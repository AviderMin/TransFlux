using System.Globalization;
using System.Text.Json;
using TransFlux.Domain.Common;
using TransFlux.Domain.Media;

namespace TransFlux.Engine.FFmpeg.Probe;

/// <summary>Normalizes ffprobe JSON (-show_format -show_streams -show_chapters) into a SourceSnapshot.</summary>
public static class FfprobeJsonParser
{
    public static SourceSnapshot Parse(
        string json,
        string absolutePath,
        long sizeBytes,
        DateTimeOffset modifiedUtc,
        Guid mediaId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var streams = new List<StreamDescriptor>();
        if (root.TryGetProperty("streams", out var streamsEl) && streamsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streamsEl.EnumerateArray())
            {
                var descriptor = ParseStream(s);
                if (descriptor is not null) streams.Add(descriptor);
            }
        }

        // FFmpeg 8 attaches HDR static metadata (mastering display / content light) to the
        // decoded frame rather than the stream; merge the first-frame side data when present
        // (FfmpegProbe requests -show_frames with a one-packet read interval).
        if (root.TryGetProperty("frames", out var framesEl) && framesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var fr in framesEl.EnumerateArray())
            {
                if (fr.ValueKind != JsonValueKind.Object) continue;
                var index = Int(fr, "stream_index");
                if (index is null) continue;
                var at = streams.FindIndex(st => st.SourceIndex == index.Value);
                if (at < 0) continue;
                streams[at] = MergeSideInfo(streams[at], ParseSideData(fr));
            }
        }

        var format = new MediaFormat();
        if (root.TryGetProperty("format", out var formatEl) && formatEl.ValueKind == JsonValueKind.Object)
        {
            int? chapterCount = null;
            if (root.TryGetProperty("chapters", out var chaptersEl) && chaptersEl.ValueKind == JsonValueKind.Array)
                chapterCount = chaptersEl.GetArrayLength();

            format = new MediaFormat
            {
                FormatName = Str(formatEl, "format_name") ?? string.Empty,
                FormatLongName = Str(formatEl, "format_long_name"),
                DurationUs = SecondsToUs(Str(formatEl, "duration")),
                BitRate = Long(formatEl, "bit_rate"),
                SizeBytes = Long(formatEl, "size") ?? sizeBytes,
                ChapterCount = chapterCount,
            };
        }

        long? durationUs = format.DurationUs;
        if (durationUs is null)
        {
            var maxStream = streams.Where(s => s.DurationUs is > 0).Select(s => s.DurationUs!.Value).DefaultIfEmpty(0).Max();
            if (maxStream > 0) durationUs = maxStream;
        }

        var snapshot = new SourceSnapshot
        {
            MediaId = mediaId,
            AbsolutePath = absolutePath,
            SizeBytes = sizeBytes,
            ModifiedUtc = modifiedUtc,
            ProbeSchemaVersion = 1,
            Streams = streams,
            Format = format,
            DurationUs = durationUs,
        };

        return snapshot with { ColorClassification = ColorClassifier.Classify(snapshot.PrimaryVideo) };
    }

    private static StreamDescriptor? ParseStream(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) return null;
        var index = Int(s, "index");
        if (index is null) return null;

        var kind = ParseKind(Str(s, "codec_type"));
        var disposition = s.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;

        var descriptor = new StreamDescriptor
        {
            SourceIndex = index.Value,
            Kind = kind,
            Codec = Str(s, "codec_name") ?? Str(s, "codec_tag_string") ?? string.Empty,
            CodecLongName = Str(s, "codec_long_name"),
            Language = Tag(s, "language"),
            Title = Tag(s, "title"),
            IsDefault = Flag(disposition, "default"),
            IsForced = Flag(disposition, "forced"),
            IsAttachedPicture = Flag(disposition, "attached_pic"),
        };

        if (kind == MediaKind.Video)
        {
            var info = ParseSideData(s);
            var rotation = info.Rotation ?? ParseTagRotation(s);
            var pixFmt = Str(s, "pix_fmt");
            var bitDepth = Int(s, "bits_per_raw_sample") ?? Int(s, "bits_per_sample") ?? ColorClassifier.InferBitDepth((pixFmt ?? string.Empty).ToLowerInvariant());

            descriptor = descriptor with
            {
                Width = Int(s, "width"),
                Height = Int(s, "height"),
                RotationDegrees = rotation,
                PixelFormat = pixFmt,
                BitDepth = bitDepth,
                SampleAspectRatio = ParseRational(Str(s, "sample_aspect_ratio")),
                DisplayAspectRatio = ParseRational(Str(s, "display_aspect_ratio")),
                FrameRate = ParseRational(Str(s, "r_frame_rate")),
                AverageFrameRate = ParseRational(Str(s, "avg_frame_rate")),
                ColorRange = Str(s, "color_range"),
                ColorSpace = Str(s, "color_space"),
                ColorTransfer = Str(s, "color_transfer"),
                ColorPrimaries = Str(s, "color_primaries"),
                HasHdrSideData = info.HasHdr,
                SideDataSummary = info.Summary,
                DvProfile = info.DvProfile,
                DvBlSignalCompatibilityId = info.DvBlCompat,
                MasterDisplay = info.MasterDisplay,
                MaxCLL = info.MaxCLL,
                MaxFALL = info.MaxFALL,
                BitRate = Long(s, "bit_rate"),
                DurationUs = SecondsToUs(Str(s, "duration")),
                StartUs = SecondsToUs(Str(s, "start_time")),
                TimeBase = ParseRational(Str(s, "time_base")),
            };
        }
        else if (kind == MediaKind.Audio)
        {
            descriptor = descriptor with
            {
                Channels = Int(s, "channels"),
                ChannelLayout = Str(s, "channel_layout"),
                SampleRate = Int(s, "sample_rate"),
                BitRate = Long(s, "bit_rate"),
                DurationUs = SecondsToUs(Str(s, "duration")),
                StartUs = SecondsToUs(Str(s, "start_time")),
                TimeBase = ParseRational(Str(s, "time_base")),
            };
        }
        else
        {
            descriptor = descriptor with
            {
                BitRate = Long(s, "bit_rate"),
                DurationUs = SecondsToUs(Str(s, "duration")),
                StartUs = SecondsToUs(Str(s, "start_time")),
                TimeBase = ParseRational(Str(s, "time_base")),
                AttachmentFileName = kind == MediaKind.Attachment ? Tag(s, "filename") : null,
                AttachmentMimeType = kind == MediaKind.Attachment ? Tag(s, "mimetype") : null,
            };
        }

        return descriptor;
    }

    /// <summary>Structured view over one side_data_list (stream or frame level).</summary>
    private sealed record SideInfo(
        int? Rotation,
        string? Summary,
        bool HasHdr,
        int? DvProfile,
        int? DvBlCompat,
        string? MasterDisplay,
        int? MaxCLL,
        int? MaxFALL);

    private static SideInfo ParseSideData(JsonElement s)
    {
        int? rotation = null;
        var summary = new List<string>();
        var hasHdr = false;
        int? dvProfile = null, dvBlCompat = null, maxCLL = null, maxFALL = null;
        string? masterDisplay = null;
        if (!s.TryGetProperty("side_data_list", out var list) || list.ValueKind != JsonValueKind.Array)
            return new SideInfo(rotation, null, hasHdr, dvProfile, dvBlCompat, masterDisplay, maxCLL, maxFALL);

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var type = Str(item, "side_data_type");
            if (!string.IsNullOrEmpty(type)) summary.Add(type!);

            var lower = (type ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("mastering display") || lower.Contains("content light")
                || lower.Contains("hdr10+") || lower.Contains("dynamic metadata")
                || lower.Contains("dolby vision") || lower.Contains("dovi"))
            {
                hasHdr = true;
            }

            if (lower.Contains("dovi") || lower.Contains("dolby vision") || lower.Contains("dv configuration"))
            {
                dvProfile ??= Int(item, "dv_profile");
                dvBlCompat ??= Int(item, "dv_bl_signal_compatibility_id") ?? Int(item, "bl_signal_compatibility_id");
            }
            if (lower.Contains("mastering display"))
                masterDisplay ??= BuildMasterDisplay(item);
            if (lower.Contains("content light"))
            {
                maxCLL ??= Int(item, "max_content");
                maxFALL ??= Int(item, "max_average");
            }

            if (lower.Contains("display matrix") && item.TryGetProperty("rotation", out var rotEl))
            {
                if (rotEl.ValueKind == JsonValueKind.Number && rotEl.TryGetDouble(out var r))
                    rotation = NormalizeClockwiseRotation(-r);
                else if (rotEl.ValueKind == JsonValueKind.String && double.TryParse(rotEl.GetString(), out var rs))
                    rotation = NormalizeClockwiseRotation(-rs);
            }
        }

        return new SideInfo(rotation, summary.Count == 0 ? null : string.Join("; ", summary), hasHdr,
            dvProfile, dvBlCompat, masterDisplay, maxCLL, maxFALL);
    }

    /// <summary>
    /// Builds the x265 -master-display syntax G(x,y)B(x,y)R(x,y)WP(x,y)L(max,min):
    /// chromaticities scaled to 50000, luminances in 0.0001 cd/m2 units.
    /// </summary>
    private static string? BuildMasterDisplay(JsonElement item)
    {
        var gx = ScaleFraction(Str(item, "green_x"), 50_000d);
        var gy = ScaleFraction(Str(item, "green_y"), 50_000d);
        var bx = ScaleFraction(Str(item, "blue_x"), 50_000d);
        var by = ScaleFraction(Str(item, "blue_y"), 50_000d);
        var rx = ScaleFraction(Str(item, "red_x"), 50_000d);
        var ry = ScaleFraction(Str(item, "red_y"), 50_000d);
        var wx = ScaleFraction(Str(item, "white_point_x"), 50_000d);
        var wy = ScaleFraction(Str(item, "white_point_y"), 50_000d);
        var maxL = ScaleFraction(Str(item, "max_luminance"), 10_000d);
        var minL = ScaleFraction(Str(item, "min_luminance"), 10_000d);
        if (gx is null || gy is null || bx is null || by is null || rx is null || ry is null
            || wx is null || wy is null || maxL is null || minL is null) return null;
        return FormattableString.Invariant(
            $"G({gx},{gy})B({bx},{by})R({rx},{ry})WP({wx},{wy})L({maxL},{minL})");
    }

    /// <summary>Parses "num/den" or plain decimal into round(value * scale).</summary>
    private static long? ScaleFraction(string? text, double scale)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('/');
        double value;
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den != 0)
            value = num / den;
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var single))
            value = single;
        else
            return null;
        return (long)Math.Round(value * scale);
    }

    private static StreamDescriptor MergeSideInfo(StreamDescriptor s, SideInfo info)
    {
        var summary = s.SideDataSummary;
        if (info.Summary is not null)
        {
            var parts = (summary ?? string.Empty).Split("; ", StringSplitOptions.RemoveEmptyEntries)
                .Concat(info.Summary.Split("; ", StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.Ordinal);
            summary = string.Join("; ", parts);
        }
        return s with
        {
            DvProfile = s.DvProfile ?? info.DvProfile,
            DvBlSignalCompatibilityId = s.DvBlSignalCompatibilityId ?? info.DvBlCompat,
            MasterDisplay = s.MasterDisplay ?? info.MasterDisplay,
            MaxCLL = s.MaxCLL ?? info.MaxCLL,
            MaxFALL = s.MaxFALL ?? info.MaxFALL,
            HasHdrSideData = s.HasHdrSideData || info.HasHdr,
            SideDataSummary = summary,
        };
    }

    private static int? ParseTagRotation(JsonElement s)
    {
        var raw = Tag(s, "rotate");
        if (raw is null) return null;
        return double.TryParse(raw, out var r) ? NormalizeClockwiseRotation(r) : null;
    }

    /// <summary>Normalizes any angle to a clockwise display rotation in {0,90,180,270}.</summary>
    public static int NormalizeClockwiseRotation(double degrees)
    {
        var value = (int)Math.Round(degrees);
        value %= 360;
        if (value < 0) value += 360;
        return value switch
        {
            >= 45 and < 135 => 90,
            >= 135 and < 225 => 180,
            >= 225 and < 315 => 270,
            _ => 0,
        };
    }

    private static MediaKind ParseKind(string? codecType) => codecType?.ToLowerInvariant() switch
    {
        "video" => MediaKind.Video,
        "audio" => MediaKind.Audio,
        "subtitle" => MediaKind.Subtitle,
        "attachment" => MediaKind.Attachment,
        "data" => MediaKind.Data,
        _ => MediaKind.Unknown,
    };

    private static Rational? ParseRational(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = value.Split('/');
        if (parts.Length != 2) return null;
        return int.TryParse(parts[0], out var num) && int.TryParse(parts[1], out var den)
            ? Rational.Create(num, den)
            : null;
    }

    private static long? SecondsToUs(string? seconds)
    {
        if (string.IsNullOrWhiteSpace(seconds) || seconds.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return null;
        return double.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)
            ? (long)Math.Round(s * 1_000_000d)
            : null;
    }

    private static string? Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static string? Tag(JsonElement e, string name)
    {
        if (!e.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return null;
        return Str(tags, name);
    }

    private static bool Flag(JsonElement disposition, string name)
    {
        if (disposition.ValueKind != JsonValueKind.Object) return false;
        return Int(disposition, name) is > 0;
    }

    private static int? Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }
}
