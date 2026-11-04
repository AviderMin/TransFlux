using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Interop;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;

namespace TransFlux.App.Components.Dialogs;

/// <summary>Props for the persistent conversion history dialog.</summary>
public sealed record HistoryDialogProps(
    AppServices Services,
    Func<string, Task<bool>> Rejoin,
    Action OnClosed);

/// <summary>
/// 历史记录 dialog (ContentDialog overlay, mounted only while open): recent
/// terminal jobs from <see cref="IHistoryService"/>, with 打开输出 / 打开文件夹 /
/// 重新加入（重新探测并作为新任务） actions and a two-step 清空历史 confirmation.
/// Clearing removes records only — output files are never touched.
/// </summary>
public sealed class HistoryDialog : Component<HistoryDialogProps>
{
    private const int MaxEntries = 100;

    public override Element Render()
    {
        var p = Props;
        var (open, setOpen) = UseState(true);
        var (entries, setEntries) = UseState<IReadOnlyList<HistoryEntry>?>(null);
        var (banner, setBanner) = UseState<Notice?>(null);
        var (confirmClear, setConfirmClear) = UseState(false);
        var (busy, setBusy) = UseState(false);

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            void Post(Action action)
            {
                if (dispatcher is null || dispatcher.HasThreadAccess) action();
                else dispatcher.TryEnqueue(() => action());
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    var loaded = await p.Services.History.GetRecentAsync(MaxEntries, cts.Token);
                    Post(() => setEntries(loaded));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Post(() =>
                    {
                        setBanner(Notice.Error(UiText.HistoryLoadFailedTitle, ex.Message));
                        setEntries(Array.Empty<HistoryEntry>());
                    });
                }
            });
            return () => cts.Cancel();
        }, p.Services);

        async Task ClearAsync()
        {
            if (busy) return;
            setBusy(true);
            try
            {
                await p.Services.History.ClearAsync(CancellationToken.None);
                setEntries(Array.Empty<HistoryEntry>());
                setConfirmClear(false);
                setBanner(Notice.Success(UiText.PromptTitle, UiText.HistoryCleared));
            }
            catch (Exception ex) { setBanner(Notice.Error(UiText.HistoryClearFailedTitle, ex.Message)); }
            finally { setBusy(false); }
        }

        async Task RejoinAsync(HistoryEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.SourcePath)) return;
            try
            {
                var added = await p.Rejoin(entry.SourcePath);
                setBanner(added
                    ? Notice.Success(UiText.PromptTitle, UiText.RejoinDone)
                    : Notice.Warning(UiText.PromptTitle, UiText.RejoinFailedExists));
            }
            catch (Exception ex) { setBanner(Notice.Error(UiText.RejoinFailedTitle, ex.Message)); }
        }

        async Task OpenOutputAsync(string path)
        {
            try { await LauncherInterop.OpenAsync(path); }
            catch (Exception ex) { setBanner(Notice.Error(UiText.OpenOutputFailedTitle, ex.Message)); }
        }
        void RevealOutput(string path)
        {
            try { LauncherInterop.RevealInExplorer(path); }
            catch (Exception ex) { setBanner(Notice.Error(UiText.RevealFailedTitle, ex.Message)); }
        }

        void Close()
        {
            setOpen(false);
            DialogChrome.DeferClose(p.OnClosed);
        }

        var children = new List<Element>();

        if (confirmClear)
        {
            children.Add(Ui.Card(VStack(Space.Sm,
                TextBlock(UiText.HistoryClearConfirm).SemiBold(),
                Ui.DimCaption(UiText.HistoryClearHint).TextWrapping(TextWrapping.Wrap),
                HStack(Space.Sm,
                    Button(UiText.HistoryConfirmClear, () => _ = ClearAsync()).AccentButton()
                        .IsEnabled(!busy).AutomationName(UiText.HistoryConfirmClear),
                    Button(UiText.Cancel, () => setConfirmClear(false)).SubtleButton()
                        .AutomationName(UiText.Cancel)))));
        }

        if (banner is { } shown) children.Add(Ui.NoticeBar(shown, () => setBanner(null)));

        if (entries is null)
        {
            children.Add(HStack(Space.Sm, ProgressRing().Width(16).Height(16), Ui.DimCaption(UiText.HistoryLoading)));
        }
        else if (entries.Count == 0)
        {
            children.Add(Ui.Card(Ui.DimCaption(UiText.HistoryEmpty).HAlign(HorizontalAlignment.Center)));
        }
        else
        {
            children.Add(Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
                Ui.DimCaption($"共 {entries.Count} 条记录").VAlign(VerticalAlignment.Center).Grid(column: 0),
                Button(UiText.HistoryClear, () => setConfirmClear(true)).SubtleButton()
                    .IsEnabled(!busy).AutomationName(UiText.HistoryClear).Grid(column: 1)));

            var cards = entries.Select(entry => EntryCard(entry, RejoinAsync, OpenOutputAsync, RevealOutput)).ToArray();
            children.Add(VStack(Space.Sm, cards));
        }

        var body = VStack(Space.Md, children.ToArray());

        return DialogChrome.Render(
            UiText.HistoryRecords,
            body,
            "关闭",
            null,
            _ => Close(),
            contentWidth: 760,
            maxHeight: 580,
            isOpen: open);
    }

    private static Element EntryCard(HistoryEntry entry, Func<HistoryEntry, Task> onRejoin,
        Func<string, Task> onOpen, Action<string> onReveal)
    {
        var sourceName = string.IsNullOrWhiteSpace(entry.SourcePath)
            ? "未知来源"
            : Path.GetFileName(entry.SourcePath);
        var time = (entry.CompletedUtc ?? entry.CreatedUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var outputPath = entry.OutputPath ?? "—";
        var sourcePath = entry.SourcePath ?? "—";
        var canOpenOutput = !string.IsNullOrWhiteSpace(entry.OutputPath);
        var canRejoin = !string.IsNullOrWhiteSpace(entry.SourcePath);
        var (stateBackground, stateForeground) = Ui.ToneColors(StatusTones.For(entry.State));

        var actions = new List<Element>
        {
            Button(UiText.OpenOutput, () =>
                {
                    if (!string.IsNullOrWhiteSpace(entry.OutputPath)) _ = onOpen(entry.OutputPath);
                })
                .SubtleButton().IsEnabled(canOpenOutput).AutomationName(UiText.OpenOutput + " " + sourceName).WithKey("open"),
            Button(UiText.OpenFolder, () =>
                {
                    if (!string.IsNullOrWhiteSpace(entry.OutputPath)) onReveal(entry.OutputPath);
                })
                .SubtleButton().IsEnabled(canOpenOutput).AutomationName(UiText.OpenFolder + " " + sourceName).WithKey("folder"),
            Button(UiText.Rejoin, () => _ = onRejoin(entry))
                .SubtleButton().IsEnabled(canRejoin).AutomationName(UiText.Rejoin + " " + sourceName).WithKey("rejoin"),
        };

        var details = VStack(Space.Xs,
            TextBlock(sourceName).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1)
                .ToolTip(sourcePath),
            Caption("来源：" + sourcePath).Foreground(Theme.SecondaryText)
                .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).ToolTip(sourcePath),
            Caption("输出：" + outputPath).Foreground(Theme.SecondaryText)
                .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).ToolTip(outputPath),
            HStack(Space.Sm,
                Ui.Pill(Fmt.JobStateText(entry.State), stateBackground, stateForeground),
                Ui.DimCaption(time).VAlign(VerticalAlignment.Center),
                Ui.DimCaption(Fmt.Size(entry.OutputSizeBytes)).VAlign(VerticalAlignment.Center)));

        return Border(Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
                details.Grid(column: 0),
                HStack(Space.Xs, actions.ToArray()).VAlign(VerticalAlignment.Top).Grid(column: 1)))
            .Padding(Space.Md, Space.Sm)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .WithKey(entry.JobId.ToString("n"));
    }

}
