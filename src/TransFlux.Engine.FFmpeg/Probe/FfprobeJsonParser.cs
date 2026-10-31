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
            var (rotation, sideSummary, hasHdr) = ParseSideData(s);
            if (rotation is null) rotation = ParseTagRotation(s);
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
                HasHdrSideData = hasHdr,
                SideDataSummary = sideSummary,
                BitRate = Long(s, "bit_rate"),
                DurationUs = SecondsToUs(Str(s, "duration")),
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
            };
        }
        else
        {
            descriptor = descriptor with
            {
                BitRate = Long(s, "bit_rate"),
                DurationUs = SecondsToUs(Str(s, "duration")),
            };
        }

        return descriptor;
    }

    private static (int? Rotation, string? SideSummary, bool HasHdr) ParseSideData(JsonElement s)
    {
        int? rotation = null;
        var summary = new List<string>();
        var hasHdr = false;
        if (!s.TryGetProperty("side_data_list", out var list) || list.ValueKind != JsonValueKind.Array)
            return (rotation, null, hasHdr);

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

            if (lower.Contains("display matrix") && item.TryGetProperty("rotation", out var rotEl))
            {
                if (rotEl.ValueKind == JsonValueKind.Number && rotEl.TryGetDouble(out var r))
                    rotation = NormalizeClockwiseRotation(-r);
                else if (rotEl.ValueKind == JsonValueKind.String && double.TryParse(rotEl.GetString(), out var rs))
                    rotation = NormalizeClockwiseRotation(-rs);
            }
        }

        return (rotation, summary.Count == 0 ? null : string.Join("; ", summary), hasHdr);
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
