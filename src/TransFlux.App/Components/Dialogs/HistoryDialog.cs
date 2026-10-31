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
        var (error, setError) = UseState<string?>(null);
        var (notice, setNotice) = UseState<string?>(null);
        var (confirmClear, setConfirmClear) = UseState(false);
        var (busy, setBusy) = UseState(false);

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try { setEntries(await p.Services.History.GetRecentAsync(MaxEntries, cts.Token)); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    setError(ex.Message);
                    setEntries(Array.Empty<HistoryEntry>());
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
                setNotice("已清空历史记录。");
            }
            catch (Exception ex) { setError(ex.Message); }
            finally { setBusy(false); }
        }

        async Task RejoinAsync(HistoryEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.SourcePath)) return;
            try
            {
                var added = await p.Rejoin(entry.SourcePath);
                setNotice(added
                    ? "已重新加入工作台，正在读取媒体信息。"
                    : "无法重新加入：文件不存在或已在任务列表中。");
            }
            catch (Exception ex) { setError(ex.Message); }
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
                Ui.Dim(UiText.HistoryClearHint).FontSize(12).TextWrapping(TextWrapping.Wrap),
                HStack(Space.Sm,
                    Button(UiText.HistoryConfirmClear, () => _ = ClearAsync()).AccentButton()
                        .IsEnabled(!busy).AutomationName(UiText.HistoryConfirmClear),
                    Button(UiText.Cancel, () => setConfirmClear(false)).SubtleButton()
                        .AutomationName(UiText.Cancel)))));
        }

        if (error is { } err)
            children.Add(InfoBar("历史记录读取失败", err).Error().IsClosable(true).Closed(() => setError(null)));
        if (notice is { } msg)
            children.Add(InfoBar("历史记录", msg).Success().IsClosable(true).Closed(() => setNotice(null)));

        if (entries is null)
        {
            children.Add(HStack(Space.Sm, ProgressRing().Width(16).Height(16), Ui.Dim(UiText.HistoryLoading).FontSize(12)));
        }
        else if (entries.Count == 0)
        {
            children.Add(Ui.Card(Ui.Dim(UiText.HistoryEmpty).FontSize(12).HAlign(HorizontalAlignment.Center)));
        }
        else
        {
            children.Add(Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
                Ui.Dim($"共 {entries.Count} 条记录").FontSize(12).VAlign(VerticalAlignment.Center).Grid(column: 0),
                Button(UiText.HistoryClear, () => setConfirmClear(true)).SubtleButton()
                    .IsEnabled(!busy).AutomationName(UiText.HistoryClear).Grid(column: 1)));

            var cards = entries.Select(entry => EntryCard(entry, RejoinAsync)).ToArray();
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

    private static Element EntryCard(HistoryEntry entry, Func<HistoryEntry, Task> onRejoin)
    {
        var sourceName = string.IsNullOrWhiteSpace(entry.SourcePath)
            ? "未知来源"
            : Path.GetFileName(entry.SourcePath);
        var time = (entry.CompletedUtc ?? entry.CreatedUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var outputPath = entry.OutputPath ?? "—";
        var sourcePath = entry.SourcePath ?? "—";
        var canOpenOutput = !string.IsNullOrWhiteSpace(entry.OutputPath);
        var canRejoin = !string.IsNullOrWhiteSpace(entry.SourcePath);
        var (stateBackground, stateForeground) = StateColors(entry.State);

        var actions = new List<Element>
        {
            Button(UiText.OpenOutput, () =>
                {
                    if (!string.IsNullOrWhiteSpace(entry.OutputPath)) _ = LauncherInterop.OpenAsync(entry.OutputPath);
                })
                .SubtleButton().IsEnabled(canOpenOutput).AutomationName(UiText.OpenOutput + " " + sourceName).WithKey("open"),
            Button(UiText.OpenFolder, () =>
                {
                    if (!string.IsNullOrWhiteSpace(entry.OutputPath)) LauncherInterop.RevealInExplorer(entry.OutputPath);
                })
                .SubtleButton().IsEnabled(canOpenOutput).AutomationName(UiText.OpenFolder + " " + sourceName).WithKey("folder"),
            Button(UiText.Rejoin, () => _ = onRejoin(entry))
                .SubtleButton().IsEnabled(canRejoin).AutomationName(UiText.Rejoin + " " + sourceName).WithKey("rejoin"),
        };

        var details = VStack(Space.Xs,
            TextBlock(sourceName).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1)
                .ToolTip(sourcePath),
            TextBlock("来源：" + sourcePath).FontSize(12).Foreground(Theme.SecondaryText)
                .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).ToolTip(sourcePath),
            TextBlock("输出：" + outputPath).FontSize(12).Foreground(Theme.SecondaryText)
                .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).ToolTip(outputPath),
            HStack(Space.Sm,
                Ui.Pill(Fmt.JobStateText(entry.State), stateBackground, stateForeground),
                Ui.Dim(time).FontSize(12).VAlign(VerticalAlignment.Center),
                Ui.Dim(Fmt.Size(entry.OutputSizeBytes)).FontSize(12).VAlign(VerticalAlignment.Center)));

        return Border(Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
                details.Grid(column: 0),
                HStack(Space.Xs, actions.ToArray()).VAlign(VerticalAlignment.Top).Grid(column: 1)))
            .Padding(Space.Md, Space.Sm)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .WithKey(entry.JobId.ToString("n"));
    }

    private static (ThemeRef Background, ThemeRef Foreground) StateColors(JobState state) => state switch
    {
        JobState.Succeeded => (Theme.SystemSuccessBackground, Theme.SystemSuccess),
        JobState.Failed => (Theme.SystemCriticalBackground, Theme.SystemCritical),
        JobState.Interrupted or JobState.NeedsAttention => (Theme.SystemCautionBackground, Theme.SystemCaution),
        JobState.Canceled => (Theme.SystemNeutralBackground, Theme.SystemNeutral),
        _ => (Theme.SystemAttentionBackground, Theme.SystemAttention),
    };
}
