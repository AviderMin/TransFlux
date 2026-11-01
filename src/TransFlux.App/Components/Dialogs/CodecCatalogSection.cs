using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Theming;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;

namespace TransFlux.App.Components.Dialogs;

public sealed record CodecCatalogSectionProps(FfmpegCapabilityInfo? Info, CapabilitySnapshot? Capabilities = null);

/// <summary>Read-only codec catalog for diagnostics. Groups are collapsed by default so the
/// settings dialog stays scannable; expanded rows are compact name + capability pills.</summary>
public sealed class CodecCatalogSection : Component<CodecCatalogSectionProps>
{
    public override Element Render()
    {
        var (videoOpen, setVideoOpen) = UseState(false);
        var (audioOpen, setAudioOpen) = UseState(false);
        var (subtitleOpen, setSubtitleOpen) = UseState(false);
        var (otherOpen, setOtherOpen) = UseState(false);
        var info = Props.Info;
        if (info is null)
            return VStack(Space.Sm, ProgressIndeterminate().Width(120), Ui.Dim("正在读取编解码器…"));

        var video = info.Codecs.Where(c => c.Kind == MediaKind.Video).ToArray();
        var audio = info.Codecs.Where(c => c.Kind == MediaKind.Audio).ToArray();
        var subtitle = info.Codecs.Where(c => c.Kind == MediaKind.Subtitle).ToArray();
        var other = info.Codecs.Where(c => c.Kind is MediaKind.Data or MediaKind.Attachment).ToArray();
        var total = video.Length + audio.Length + subtitle.Length + other.Length;

        var availableHardware = Props.Capabilities?.Candidates
            .Where(c => c.Available && c.Backend != "cpu")
            .ToArray() ?? [];
        var children = new List<Element>
        {
            Ui.DimCaption($"共 {total} 个编解码器 · 展开分组查看，列表可独立滚动").TextWrapping(TextWrapping.Wrap),
            Summary(video.Length, audio.Length, subtitle.Length, other.Length),
            Legend(),
        };

        children.Add(Grid(columns: [GridSize.Star(), GridSize.Star(), GridSize.Star()], rows: [GridSize.Auto],
            Group("视频", video, videoOpen, setVideoOpen, availableHardware).Grid(column: 0),
            Group("音频", audio, audioOpen, setAudioOpen, availableHardware).Grid(column: 1),
            Group("字幕", subtitle, subtitleOpen, setSubtitleOpen, availableHardware).Grid(column: 2))
            with { ColumnSpacing = Space.Sm });
        if (other.Length > 0) children.Add(Group("其他", other, otherOpen, setOtherOpen, availableHardware));

        if (total == 0)
            children.Add(Ui.Dim("当前核心未报告可用于展示的编解码器。"));

        return VStack(Space.Md, children.ToArray());
    }

    private static Element Summary(int video, int audio, int subtitle, int other)
        => FlexRow(
            Ui.Pill($"视频 {video}", Theme.SystemAttentionBackground, Theme.SystemAttention),
            Ui.Pill($"音频 {audio}", Theme.SystemSuccessBackground, Theme.SystemSuccess),
            Ui.Pill($"字幕 {subtitle}", Theme.SystemNeutralBackground, Theme.SystemNeutral),
            Ui.Pill($"其他 {other}", Theme.SystemCautionBackground, Theme.SystemCaution))
            with { Wrap = FlexWrap.Wrap, ColumnGap = Space.Sm, RowGap = Space.Xs };

    private static Element Group(string label, FfmpegCodecInfo[] codecs,
        bool open, Action<bool> setOpen, IReadOnlyList<CandidateCapability> availableHardware)
    {
        var body = open
            ? codecs.Length == 0 ? Ui.DimCaption("核心未报告此类编解码器。")
                : ScrollViewer(LazyVStack(codecs, codec => codec.Name,
                    (codec, _) => CodecRow(codec, availableHardware)) with { Spacing = Space.Sm })
                    .Height(320).HorizontalScrollMode(ScrollMode.Disabled).VerticalScrollMode(ScrollMode.Auto)
                    .Set(s => { s.HorizontalContentAlignment = HorizontalAlignment.Stretch; s.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; })
            : Empty();
        return Expander($"{label} · {codecs.Length}", body, isExpanded: open, onIsExpandedChanged: setOpen)
            .AutomationName($"{label}编解码器，共 {codecs.Length} 项")
            .HAlign(HorizontalAlignment.Stretch).VAlign(VerticalAlignment.Top)
            .Set(e => e.HorizontalContentAlignment = HorizontalAlignment.Stretch);
    }

    private static Element CodecRow(FfmpegCodecInfo codec, IReadOnlyList<CandidateCapability> availableHardware)
    {
        var pills = new List<Element>();
        if (codec.CanDecode) pills.Add(Ui.Pill("解码", Theme.SystemNeutralBackground, Theme.SystemNeutral));
        if (codec.CanEncode) pills.Add(Ui.Pill("编码", Theme.SystemSuccessBackground, Theme.SystemSuccess));
        if (codec.HardwareEncoders.Count > 0)
        {
            var details = string.Join(" · ", codec.HardwareEncoders);
            var proven = availableHardware
            .Where(c => c.TrialCompleted && codec.HardwareEncoders.Contains(c.EncoderName, StringComparer.OrdinalIgnoreCase))
                .Select(c => c.EncoderName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            pills.Add(proven.Length > 0
                ? Ui.Pill("硬编可用", Theme.SystemSuccessBackground, Theme.SystemSuccess)
                    .ToolTip($"本机试编码通过：{string.Join(" · ", proven)}")
                : Ui.Pill("硬编待验证", Theme.SystemCautionBackground, Theme.SystemCaution)
                    .ToolTip($"核心包含硬件编码器：{details}；需在“设备与候选路径”完成试编码确认。"));
        }

        return Border(VStack(Space.Xs,
            BodyStrong(codec.Name).TextWrapping(TextWrapping.Wrap)
                .ToolTip($"{codec.Name} — {codec.LongName}"),
            FlexRow(pills.ToArray()) with { Wrap = FlexWrap.Wrap, ColumnGap = Space.Xs, RowGap = Space.Xs }))
            .Padding(Space.Sm)
            .CornerRadius(6)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    private static Element Legend()
        => FlexRow(
            Ui.Pill("解码", Theme.SystemNeutralBackground, Theme.SystemNeutral),
            Ui.Pill("编码", Theme.SystemSuccessBackground, Theme.SystemSuccess),
            Ui.Pill("硬编可用", Theme.SystemSuccessBackground, Theme.SystemSuccess),
            Ui.Pill("硬编待验证", Theme.SystemCautionBackground, Theme.SystemCaution))
            with { Wrap = FlexWrap.Wrap, ColumnGap = Space.Sm, RowGap = Space.Xs };
}
