using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Jobs;

namespace TransFlux.App.Components.Queue;

/// <summary>A queue/history task card showing state, stage, progress (&lt;=99%),
/// speed, ETA and path, plus cancel/retry/remove actions (docs/03 QueueTaskCard).</summary>
public static class QueueTaskCard
{
    public static Element Render(QueueRow row,
        Action<QueueRow> onCancel, Action<QueueRow> onRetry, Action<QueueRow> onRemove,
        Action<QueueRow> onOpen, Action<QueueRow> onReveal, bool showOutput)
    {
        var state = row.State;
        var fraction = row.Progress?.Fraction ?? 0;
        var runningPercent = Math.Clamp((int)Math.Round(fraction * 100), 0, 99);
        var percentText = state == JobState.Succeeded ? "100%" : state == JobState.Running ? $"{runningPercent}%" : string.Empty;

        var title = row.Job.SourcePath is { } src
            ? System.IO.Path.GetFileName(src)
            : $"任务 {row.Job.JobId.ToString()[..8]}";

        Element status = Ui.Pill(Fmt.JobStateText(state), Theme.ControlFillSecondary, Ui.StateColor(state));

        Element progressBlock = state switch
        {
            JobState.Running or JobState.Preflighting or JobState.Verifying or JobState.Committing =>
                VStack(4,
                    Ui.ProgressBar(fraction),
                    HStack(Space.Md,
                        Ui.Dim($"{Fmt.StageText(row.Progress?.Stage ?? JobStage.Encoding)} {percentText}"),
                        Ui.Dim("速度 " + Fmt.Speed(row.Progress?.Speed)),
                        Ui.Dim("剩余 " + Fmt.Eta(row.Progress?.Eta)))),
            JobState.Queued => Ui.Dim("排队中，等待开始"),
            JobState.Succeeded => Ui.Dim("已完成"),
            JobState.Failed => Ui.Dim(row.Failure?.UserMessageKey ?? "转换失败"),
            JobState.Interrupted => Ui.Dim(UiText.Interrupted),
            _ => Ui.Dim(Fmt.JobStateText(state)),
        };

        var actions = new List<Element>();
        if (row.CanCancel) actions.Add(Ui.IconTextButton("Cancel", UiText.Cancel, () => onCancel(row)));
        if (row.CanRetry) actions.Add(Ui.IconTextButton("Refresh", UiText.Retry, () => onRetry(row)));
        if (row.CanRemove) actions.Add(Ui.IconTextButton("Delete", UiText.Remove, () => onRemove(row)));
        if (showOutput && row.Output is { } output)
        {
            actions.Add(Ui.IconTextButton("OpenFile", UiText.OpenOutput, () => onOpen(row)));
            actions.Add(Ui.IconTextButton("Folder", UiText.RevealOutput, () => onReveal(row)));
        }

        Element pathLine = row.Output is { } o
            ? Ui.KeyValue("输出", o.FinalPath, 48)
            : row.Attempt?.ReservedFinalPath is { } reserved ? Ui.KeyValue("目标", reserved, 48)
            : row.Job.SourcePath is { } sp ? Ui.KeyValue("源文件", sp, 48) : Empty();

        return Ui.Card(VStack(Space.Sm,
            Grid(columns: [GridSize.Auto, GridSize.Star()], rows: [GridSize.Auto],
                status.Margin(0, 0, Space.Sm, 0).Grid(column: 0),
                TextBlock(title).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).Grid(column: 1)),
            progressBlock,
            pathLine,
            actions.Count > 0 ? HStack(Space.Xs, actions.ToArray()) : null))
            .AutomationName($"任务 {title}，{Fmt.JobStateText(state)}");
    }
}
