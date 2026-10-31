using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.App.Interop;
using TransFlux.Domain.Jobs;

namespace TransFlux.App.Components.Queue;

public sealed record QueuePageProps(AppServices Services, JobQueueStore Queue);

public enum QueueSort { EnqueueOrder, Created, State }

/// <summary>任务 page: queue cards, pause/resume, cancel confirm, retry, remove,
/// plus 历史 and 诊断 tabs (docs/03).</summary>
public sealed class QueuePage : Component<QueuePageProps>
{
    public override Element Render()
    {
        var p = Props;
        var snapshot = UseExternalStore<QueueSnapshot>(p.Queue.Subscribe, p.Queue.GetSnapshot);
        var (tab, setTab) = UseState(0);
        var (sort, setSort) = UseState(QueueSort.EnqueueOrder);
        var (cancelTarget, setCancelTarget) = UseState<QueueRow?>(null);
        var (notice, setNotice) = UseState<string?>(null);
        async Task TogglePauseAsync()
        {
            try
            {
                if (snapshot.Paused) { await p.Services.Jobs.ResumeQueueAsync(CancellationToken.None); p.Queue.SetPaused(false); }
                else { await p.Services.Jobs.PauseQueueAsync(CancellationToken.None); p.Queue.SetPaused(true); }
                p.Queue.RefreshControl();
            }
            catch (Exception ex) { setNotice(ex.Message); }
        }

        async Task ConfirmCancelAsync(QueueRow row)
        {
            setCancelTarget(null);
            try
            {
                var result = await p.Services.Jobs.CancelAsync(row.Job.JobId, CancellationToken.None);
                setNotice(result.Accepted ? "已请求取消" : "无法取消：" + result.Message);
            }
            catch (Exception ex) { setNotice(ex.Message); }
        }

        async Task RetryAsync(QueueRow row)
        {
            try { await p.Services.Jobs.RetryAsync(row.Job.JobId, CancellationToken.None); p.Queue.RefreshControl(); }
            catch (Exception ex) { setNotice(ex.Message); }
        }

        async Task RemoveAsync(QueueRow row)
        {
            try { await p.Services.Jobs.RemoveQueuedAsync(row.Job.JobId, CancellationToken.None); }
            catch (Exception ex) { setNotice(ex.Message); }
        }

        void OpenOutput(QueueRow row)
        {
            if (row.Output?.FinalPath is { } path) _ = LauncherInterop.OpenAsync(path);
        }

        void RevealOutput(QueueRow row)
        {
            if (row.Output?.FinalPath is { } path) LauncherInterop.RevealInExplorer(path);
        }

        var ordered = SortRows(snapshot.Rows, sort);
        var activeRows = ordered.Where(r => !r.IsTerminal).ToList();
        var historyRows = ordered.Where(r => r.IsTerminal).ToList();

        Element header = HStack(Space.Sm,
            Heading(UiText.QueueTitle),
            Ui.Dim($"{snapshot.RunningCount} 运行 · {snapshot.QueuedCount} 排队"),
            Ui.Spacer(),
            DropDownButton("排序", flyout: MenuItems(
                MenuItem("按加入顺序", () => setSort(QueueSort.EnqueueOrder)),
                MenuItem("按创建时间", () => setSort(QueueSort.Created)),
                MenuItem("按状态", () => setSort(QueueSort.State)))),
            Button(snapshot.Paused ? UiText.ResumeQueue : UiText.PauseQueue, () => _ = TogglePauseAsync())
                .AutomationName(snapshot.Paused ? UiText.ResumeQueue : UiText.PauseQueue),
            Ui.Dim(UiText.PauseHint));

        var tabs = SelectorBar(new[]
        {
            SelectorBarItem("队列"),
            SelectorBarItem(UiText.History),
            SelectorBarItem(UiText.Diagnostics),
        }, tab, i => setTab(i));

        Element body = tab switch
        {
            1 => Component<HistoryView, HistoryViewProps>(new HistoryViewProps(historyRows, row => _ = RetryAsync(row), OpenOutput, RevealOutput)),
            2 => Component<DiagnosticsView, DiagnosticsViewProps>(new DiagnosticsViewProps(p.Services)),
            _ => activeRows.Count == 0
                ? Ui.Card(VStack(Space.Sm, TextBlock("队列为空").SemiBold(), Ui.Dim("在“转换”页添加文件并开始转换后，任务会显示在这里。")))
                : VStack(Space.Sm, activeRows.Select(r => QueueTaskCard.Render(r, row => setCancelTarget(row), row => _ = RetryAsync(row), row => _ = RemoveAsync(row), OpenOutput, RevealOutput, false).WithKey(r.Job.JobId.ToString())).ToArray()),
        };

        var noticeBar = notice is { } msg
            ? InfoBar("提示", msg).Informational().IsClosable(true).Closed(() => setNotice(null)).Margin(0, 0, 0, Space.Sm)
            : null;

        var cancelDialog = ContentDialog(UiText.CancelConfirmTitle,
                TextBlock(UiText.CancelConfirmBody).TextWrapping(TextWrapping.Wrap),
                primaryButtonText: UiText.Confirm) with
        {
            IsOpen = cancelTarget is not null,
            SecondaryButtonText = UiText.CancelAction,
            OnClosed = result =>
            {
                var target = cancelTarget;
                setCancelTarget(null);
                if (result == ContentDialogResult.Primary && target is not null) _ = ConfirmCancelAsync(target);
            },
        };

        return VStack(0, noticeBar, header, tabs, ScrollView(VStack(0, body).Padding(0, Space.Sm)).Flex(grow: 1, basis: 0), cancelDialog)
            .Padding(Space.PagePadding);
    }

    private static IReadOnlyList<QueueRow> SortRows(IReadOnlyList<QueueRow> rows, QueueSort sort)
    {
        IEnumerable<QueueRow> q = sort switch
        {
            QueueSort.Created => rows.OrderByDescending(r => r.Job.CreatedUtc),
            QueueSort.State => rows.OrderBy(r => r.State).ThenBy(r => r.Job.EnqueueOrder),
            _ => rows.OrderBy(r => r.Job.EnqueueOrder),
        };
        // Active tasks first, then history.
        return q.OrderBy(r => r.IsTerminal ? 1 : 0).ToList();
    }
}
