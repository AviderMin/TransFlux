using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;

namespace TransFlux.App.Components.Queue;

public sealed record HistoryViewProps(
    IReadOnlyList<QueueRow> Rows,
    Action<QueueRow> Retry,
    Action<QueueRow> Open,
    Action<QueueRow> Reveal);

/// <summary>历史 tab: completed/failed/canceled tasks with output access.</summary>
public sealed class HistoryView : Component<HistoryViewProps>
{
    public override Element Render()
    {
        var rows = Props.Rows;
        if (rows.Count == 0)
            return Ui.Card(VStack(Space.Sm, TextBlock("暂无历史").SemiBold(), Ui.Dim("已结束的任务会保留在这里，可重试或打开输出。")));

        return VStack(Space.Sm, rows
            .Select(r => QueueTaskCard.Render(r, _ => { }, row => Props.Retry(row), _ => { }, row => Props.Open(row), row => Props.Reveal(row), showOutput: true)
                .WithKey(r.Job.JobId.ToString()))
            .ToArray());
    }
}
