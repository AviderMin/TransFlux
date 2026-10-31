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
/// One row of the task table. Columns are shared with <see cref="Header"/> so the
/// table reads as aligned columns (文件 ｜ 目标 ｜ 状态/进度 ｜ 速度 ｜ 输出 ｜ 操作).
/// Pure render — the live job row is passed in by the host.
/// </summary>
public static class TaskRow
{
    /// <summary>Column tracks shared by the header and every row.</summary>
    public static GridSize[] Columns { get; } = new[]
    {
        GridSize.Px(36), GridSize.Star(), GridSize.Px(118), GridSize.Px(218),
        GridSize.Px(86), GridSize.Px(150), GridSize.Px(96),
    };

    public static Element Header() => Border(Grid(
            columns: Columns,
            rows: [GridSize.Auto],
            HeaderCell("").Grid(column: 0),
            HeaderCell("文件").Grid(column: 1),
            HeaderCell("目标格式").Grid(column: 2),
            HeaderCell("状态 / 进度").Grid(column: 3),
            HeaderCell("速度").Grid(column: 4),
            HeaderCell("输出").Grid(column: 5),
            HeaderCell("操作").Grid(column: 6))
        .Padding(Space.Sm, Space.Xs)
    ).WithBorder(Theme.DividerStroke, 1);

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
        var (status, progress) = BuildStatus(draft, job);
        var target = TextBlock(Fmt.TargetSummary(draft.Options)).FontSize(12)
            .Foreground(Theme.PrimaryText).TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1)
            .ToolTip(Fmt.TargetSummary(draft.Options));

        var speed = job is { IsTerminal: false }
            ? TextBlock(Fmt.Speed(job.Progress?.Speed) + " · " + Fmt.Eta(job.Progress?.Eta)).FontSize(12)
                .Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1)
            : Ui.Dim("—").FontSize(12);

        var output = job is { State: JobState.Succeeded, Output: not null }
            ? TextBlock(job.Output.FinalPath).FontSize(12).Foreground(Theme.SecondaryText)
                .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).ToolTip(job.Output.FinalPath)
            : Ui.Dim("—").FontSize(12);

        var actions = new List<Element>
        {
            IconAction("\uE713", "设置参数", "设置参数", () => onOpenParam(draft.MediaId)).WithKey("param"),
            IconAction("\uE74D", "移除文件", "移除", () => onRemove(draft.MediaId)).WithKey("remove"),
        };
        if (job is { State: JobState.Succeeded, Output: not null })
        {
            actions.Add(IconAction("\uE8E5", UiText.OpenOutput, UiText.OpenOutput, () => onOpenOutput(draft.MediaId)).WithKey("open"));
            actions.Add(IconAction("\uE838", UiText.RevealOutput, UiText.RevealOutput, () => onRevealOutput(draft.MediaId)).WithKey("reveal"));
        }

        var row = Grid(
            columns: Columns,
            rows: [GridSize.Auto],
            CheckBox(selected, v => { if (v != selected) onToggleSelect(draft.MediaId); }, string.Empty)
                .AutomationName("选择 " + draft.FileName).Grid(column: 0),
            VStack(2,
                TextBlock(draft.FileName).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1)
                    .ToolTip(draft.Path),
                TextBlock(Fmt.TrackSummary(draft.Snapshot)).FontSize(12).Foreground(Theme.SecondaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1)).Grid(column: 1),
            target.Grid(column: 2),
            VStack(2, status, progress).Grid(column: 3),
            speed.VAlign(VerticalAlignment.Center).Grid(column: 4),
            output.VAlign(VerticalAlignment.Center).Grid(column: 5),
            HStack(Space.Xs, actions.ToArray()).VAlign(VerticalAlignment.Center).Grid(column: 6));

        var border = Border(row)
            .Padding(Space.Sm, Space.Xs + 2)
            .Background(selected ? Theme.SubtleFill : Theme.CardBackground)
            .WithBorder(Theme.DividerStroke, 1)
            .IsTabStop(true)
            .OnTapped((_, _) => onFocus(draft.MediaId))
            .AutomationName("任务 " + draft.FileName)
            .WithKey(draft.MediaId.ToString());

        return border.IsVisible(true);
    }

    private static (Element Status, Element? Progress) BuildStatus(MediaDraft draft, QueueRow? job)
    {
        if (draft.Status == DraftStatus.Probing)
        {
            return (HStack(Space.Sm, ProgressRing().Width(14).Height(14), Ui.Dim(UiText.Reading).FontSize(12)), null);
        }

        if (draft.Status == DraftStatus.Invalid)
        {
            return (HStack(Space.Sm,
                Ui.Pill(UiText.InvalidText, Theme.SystemCriticalBackground, Theme.SystemCritical),
                Ui.Dim(draft.Failure?.UserMessageKey ?? draft.ErrorText ?? "读取失败").FontSize(12)
                    .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1)), null);
        }

        if (job is not null && !job.IsTerminal)
        {
            var fraction = job.Progress?.Fraction ?? 0;
            var percent = Math.Clamp((int)Math.Round(fraction * 100), 0, 99);
            var status = HStack(Space.Sm,
                Ui.Pill(Fmt.JobStateText(job.State), Theme.SystemAttentionBackground, Theme.SystemAttention),
                Ui.Dim(Fmt.StageText(job.Progress?.Stage ?? JobStage.Encoding) + " " + percent + "%").FontSize(12));
            return (status, Ui.ProgressBar(fraction));
        }

        if (job is not null && job.State == JobState.Succeeded)
        {
            return (HStack(Space.Sm, Ui.Pill(UiText.Done, Theme.SystemSuccessBackground, Theme.SystemSuccess)), null);
        }

        if (job is not null && job.State is JobState.Failed or JobState.Interrupted or JobState.Canceled)
        {
            return (HStack(Space.Sm,
                Ui.Pill(Fmt.JobStateText(job.State), Theme.SystemCriticalBackground, Theme.SystemCritical),
                Ui.Dim(job.Failure?.UserMessageKey ?? UiText.Interrupted).FontSize(12)
                    .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1)), null);
        }

        return (HStack(Space.Sm,
            Ui.Pill(UiText.Ready, Theme.ControlFillSecondary, Theme.SecondaryText),
            Ui.Dim(Fmt.DurationMicros(draft.Snapshot?.DurationUs)).FontSize(12)), null);
    }

    private static Element HeaderCell(string text)
        => TextBlock(text).FontSize(12).Foreground(Theme.SecondaryText).SemiBold();

    private static Element IconAction(string glyph, string automationName, string toolTip, Action onClick)
        => Button(Icon(FontIcon(glyph, fontSize: 14)), onClick)
            .SubtleButton().Width(30).Height(30).MinWidth(30).MinHeight(30).Padding(0)
            .ToolTip(toolTip).AutomationName(automationName)
            .Set(b => { b.HorizontalContentAlignment = HorizontalAlignment.Center; b.VerticalContentAlignment = VerticalAlignment.Center; });
}