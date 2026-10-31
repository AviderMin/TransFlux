using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;

namespace TransFlux.App.Components.Convert;

/// <summary>One media row: thumbnail placeholder, file name, duration, track
/// summary, per-item state. Selection is a checkbox, not "open" (docs/03).</summary>
public static class MediaListRow
{
    public static Element Render(MediaDraft draft, bool selected, bool focused,
        Action<Guid> onToggleSelect, Action<Guid> onFocus, Action<Guid> onRemove)
    {
        var snapshot = draft.Snapshot;

        Element status = draft.Status switch
        {
            DraftStatus.Probing => HStack(Space.Sm, ProgressRing().Width(14).Height(14), Ui.Dim(UiText.Reading)),
            DraftStatus.Invalid => HStack(Space.Sm,
                Ui.Pill("无法转换", Theme.SystemCriticalBackground, Theme.SystemCritical),
                Ui.Dim(draft.Failure?.UserMessageKey ?? draft.ErrorText ?? "读取失败")),
            _ => HStack(Space.Sm,
                Ui.Pill("就绪", Theme.SystemSuccessBackground, Theme.SystemSuccess),
                Ui.Dim(Fmt.DurationMicros(snapshot?.DurationUs))),
        };

        var thumb = Border(Icon(SymbolIcon("Video"))
                .HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Center))
            .Width(56).Height(40).CornerRadius(6).Background(Theme.ControlFillTertiary);

        var middle = VStack(2,
            TextBlock(draft.FileName).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1),
            TextBlock(Fmt.TrackSummary(snapshot)).FontSize(12).Foreground(Theme.SecondaryText)
                .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1),
            status);

        var row = Grid(columns: [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
            CheckBox(selected, v => { if (v != selected) onToggleSelect(draft.MediaId); }, label: null)
                .AutomationName($"选择 {draft.FileName}").Grid(column: 0),
            thumb.Margin(Space.Sm, 0, Space.Md, 0).Grid(column: 1),
            middle.Grid(column: 2),
            Ui.IconButton("Cancel", "移除文件", "移除", () => onRemove(draft.MediaId)).Margin(Space.Sm, 0, 0, 0).Grid(column: 3));

        return Border(row)
            .IsTabStop(true)
            .Padding(Space.Md, Space.Sm)
            .CornerRadius(6)
            .Background(focused ? Theme.SubtleFill : Theme.CardBackground)
            .WithBorder(focused ? Theme.SystemAttention : Theme.CardStroke, 1)
            .OnTapped((_, _) => onFocus(draft.MediaId))
            .AutomationName($"文件 {draft.FileName}");
    }
}
