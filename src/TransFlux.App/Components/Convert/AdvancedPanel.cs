using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.App.Components.Convert;

/// <summary> Advanced panel: stream selection, resize, trim, metadata (docs/03 AdvancedPanel).</summary>
public static class AdvancedPanel
{
    public static Element Render(ConversionOptions current, SourceSnapshot? snapshot, Action<Func<ConversionOptions, ConversionOptions>> apply)
    {
        var children = new List<Element>
        {
            TextBlock(UiText.Streams).SemiBold(),
        };

        if (snapshot is null)
        {
            children.Add(Ui.Dim("导入媒体后可选择要输出的轨道。"));
        }
        else
        {
            var baseSet = current.SelectedStreams.Count > 0
                ? current.SelectedStreams.ToHashSet()
                : DefaultIndices(snapshot);
            foreach (var stream in snapshot.Streams)
            {
                if (stream.Kind is not (MediaKind.Video or MediaKind.Audio or MediaKind.Subtitle)) continue;
                if (stream.IsAttachedPicture) continue;
                var index = stream.SourceIndex;
                var label = StreamLabel(stream, snapshot);
                children.Add(CheckBox(baseSet.Contains(index), _ =>
                {
                    var next = new HashSet<int>(baseSet);
                    if (!next.Remove(index)) next.Add(index);
                    var list = next.OrderBy(x => x).ToArray();
                    apply(o => o with { SelectedStreams = list });
                }, label: label));
            }
        }

        children.Add(Ui.Divider());
        children.Add(TextBlock(UiText.Resize).SemiBold());
        children.Add(RadioButtons(
            new[] { "保持原尺寸", "限制到 1080p（不放大）" },
            current.ResizeMode == ResizeMode.Max1080p ? 1 : 0,
            i => apply(o => o with { ResizeMode = i == 1 ? ResizeMode.Max1080p : ResizeMode.KeepDisplaySize })));
        children.Add(CheckBox(current.AllowUpscale, v => apply(o => o with { AllowUpscale = v }), "允许放大"));

        children.Add(Ui.Divider());
        children.Add(TextBlock(UiText.Trim + "（格式 时:分:秒.毫秒）").SemiBold());
        children.Add(Grid(columns: [GridSize.Star(), GridSize.Star()], rows: [GridSize.Auto],
            VStack(4, Ui.FieldLabel("开始"),
                TextBox(FormatTimecode(current.TrimStart), t => apply(o => o with { TrimStart = ParseTimecode(t) }))
                    .PlaceholderText("00:00:00.000").AutomationName("裁剪开始时间")).Margin(0, 0, Space.Sm, 0).Grid(column: 0),
            VStack(4, Ui.FieldLabel("结束"),
                TextBox(FormatTimecode(current.TrimEnd), t => apply(o => o with { TrimEnd = ParseTimecode(t) }))
                    .PlaceholderText("00:00:10.000").AutomationName("裁剪结束时间")).Grid(column: 1)));

        children.Add(Ui.Divider());
        children.Add(TextBlock(UiText.Metadata).SemiBold());
        children.Add(CheckBox(current.MetadataPolicy == MetadataPolicy.PreserveTitleAndChapters,
            v => apply(o => o with { MetadataPolicy = v ? MetadataPolicy.PreserveTitleAndChapters : MetadataPolicy.StripPersonalTags }),
            "保留标题与章节（可能含个人信息）"));

        return VStack(Space.Md, children.ToArray());
    }

    private static HashSet<int> DefaultIndices(SourceSnapshot snapshot)
    {
        var set = new HashSet<int>();
        if (snapshot.PrimaryVideo is { } v) set.Add(v.SourceIndex);
        if (snapshot.PrimaryAudio is { } a) set.Add(a.SourceIndex);
        return set;
    }

    private static string StreamLabel(StreamDescriptor s, SourceSnapshot snapshot)
    {
        var kind = s.Kind switch { MediaKind.Video => "视频", MediaKind.Audio => "音频", MediaKind.Subtitle => "字幕", _ => "轨道" };
        var codec = Fmt.CodecName(s.Codec);
        var extra = s.Kind switch
        {
            MediaKind.Video => $"{s.Width}×{s.Height}",
            MediaKind.Audio => s.ChannelLayout ?? (s.Channels is int c ? $"{c}ch" : string.Empty),
            _ => s.Language ?? string.Empty,
        };
        var parts = new[] { $"#{s.SourceIndex}", kind, codec, extra, s.Language, s.Title }
            .Where(p => !string.IsNullOrEmpty(p));
        return string.Join(" · ", parts);
    }

    private static string FormatTimecode(TimeSpan? t)
        => t is TimeSpan v ? $"{(int)v.TotalHours:00}:{v.Minutes:00}:{v.Seconds:00}.{v.Milliseconds:000}" : string.Empty;

    private static TimeSpan? ParseTimecode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] formats = { @"hh\:mm\:ss\.fff", @"h\:mm\:ss\.fff", @"mm\:ss\.fff", @"hh\:mm\:ss", @"mm\:ss" };
        return TimeSpan.TryParseExact(text.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result
            : TimeSpan.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var loose) ? loose : null;
    }
}
