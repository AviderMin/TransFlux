using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Jobs;

namespace TransFlux.App.Components.Workbench;

/// <summary>
/// One task row: filename, target-format summary, live per-item status/progress
/// (matched against the queue by SourceSnapshotId), speed/ETA, and parameter /
/// remove actions. Pure render — status is passed in.
/// </summary>
public static class TaskRow
{
    public static Element Render(
        MediaDraft draft,
        bool selected,
        bool focused,
        QueueRow? job,
        Action<Guid> onToggleSelect,
        Action<Guid> onFocus,
        Action<Guid> onRemove,
        Action<Guid> onOpenParam,
        Action<Guid> onOpenOutput,
        Action<Guid> onRevealOutput)
    {
        Element status;
        Element? progress = null;

        if (draft.Status == DraftStatus.Probing)
        {
            status = HStack(Space.Sm, ProgressRing().Width(14).Height(14), Ui.Dim(UiText.Reading));
        }
        else if (draft.Status == DraftStatus.Invalid)
        {
            status = HStack(Space.Sm,
                Ui.Pill(UiText.InvalidText, Theme.SystemCriticalBackground, Theme.SystemCritical),
                Ui.Dim(draft.Failure?.UserMessageKey ?? draft.ErrorText ?? "读取失败"));
        }
        else if (job is not null && !job.IsTerminal)
        {
            var fraction = job.Progress?.Fraction ?? 0;
            var percent = Math.Clamp((int)Math.Round(fraction * 100), 0, 99);
            status = HStack(Space.Md,
                Ui.Pill(Fmt.JobStateText(job.State), Theme.SystemAttentionBackground, Theme.SystemAttention),
                Ui.Dim(Fmt.StageText(job.Progress?.Stage ?? JobStage.Encoding) + " " + percent + "%"),
                Ui.Dim("速度 " + Fmt.Speed(job.Progress?.Speed)),
                Ui.Dim("剩余 " + Fmt.Eta(job.Progress?.Eta)));
            progress = Ui.ProgressBar(fraction);
        }
        else if (job is not null && job.State == JobState.Succeeded)
        {
            status = HStack(Space.Sm,
                Ui.Pill(UiText.Done, Theme.SystemSuccessBackground, Theme.SystemSuccess),
                Ui.Dim(job.Output?.FinalPath ?? string.Empty).TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1));
        }
        else if (job is not null && (job.State is JobState.Failed or JobState.Interrupted or JobState.Canceled))
        {
            status = HStack(Space.Sm,
                Ui.Pill(Fmt.JobStateText(job.State), Theme.SystemCriticalBackground, Theme.SystemCritical),
                Ui.Dim(job.Failure?.UserMessageKey ?? UiText.Interrupted));
        }
        else
        {
            status = HStack(Space.Sm,
                Ui.Pill(UiText.Ready, Theme.ControlFillSecondary, Theme.SecondaryText),
                Ui.Dim(Fmt.TargetSummary(draft.Options) + " · " + Fmt.DurationMicros(draft.Snapshot?.DurationUs)));
        }

        var thumb = Border(Icon(SymbolIcon("Video")).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
            .Width(56).Height(40).CornerRadius(6).Background(Theme.ControlFillTertiary);

        var middle = VStack(2,
            TextBlock(draft.FileName).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1),
            TextBlock(Fmt.TrackSummary(draft.Snapshot)).FontSize(12).Foreground(Theme.SecondaryText)
                .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1),
            status,
            progress);

        var actions = new List<Element>
        {
            IconAction("\uE713", "设置参数", "设置参数", () => onOpenParam(draft.MediaId)),
            IconAction("\uE74D", "移除文件", "移除", () => onRemove(draft.MediaId)),
        };
        if (job is { State: JobState.Succeeded, Output: not null })
        {
            actions.Add(IconAction("\uE8E5", UiText.OpenOutput, UiText.OpenOutput, () => onOpenOutput(draft.MediaId)));
            actions.Add(IconAction("\uE838", UiText.RevealOutput, UiText.RevealOutput, () => onRevealOutput(draft.MediaId)));
        }

        var row = Grid(columns: [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
            CheckBox(selected, v => { if (v != selected) onToggleSelect(draft.MediaId); }, label: null)
                .AutomationName("选择 " + draft.FileName).VAlign(VerticalAlignment.Center).Grid(column: 0),
            thumb.Margin(Space.Sm, 0, Space.Md, 0).VAlign(VerticalAlignment.Center).Grid(column: 1),
            middle.Grid(column: 2),
            HStack(Space.Xs, actions.ToArray()).VAlign(VerticalAlignment.Center).Grid(column: 3));

        return Border(row)
            .IsTabStop(true)
            .Padding(Space.Md, Space.Sm)
            .CornerRadius(6)
            .Background(focused ? Theme.SubtleFill : Theme.CardBackground)
            .WithBorder(focused ? Theme.SystemAttention : Theme.CardStroke, 1)
            .OnTapped((_, _) => onFocus(draft.MediaId))
            .AutomationName("文件 " + draft.FileName);
    }

    private static Element IconAction(string glyph, string automationName, string toolTip, Action onClick)
        => Button(Icon(FontIcon(glyph, fontSize: 14)), onClick)
            .SubtleButton()
            .Width(32).Height(32).MinWidth(32).MinHeight(32)
            .Padding(0)
            .Set(b =>
            {
                b.HorizontalContentAlignment = HorizontalAlignment.Center;
                b.VerticalContentAlignment = VerticalAlignment.Center;
            })
            .ToolTip(toolTip)
            .AutomationName(automationName);
}
