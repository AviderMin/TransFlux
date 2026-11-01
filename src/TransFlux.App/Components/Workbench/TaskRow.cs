using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;

namespace TransFlux.App.Components.Workbench;

/// <summary>Callbacks for one task card; bundled so Render keeps its signature readable.</summary>
public sealed record TaskRowActions(Action Select, Action ToggleSelect, Action Remove, Action Cancel, Action Copy,
    Action MoveUp, Action MoveDown, Action Retry, Action EditQueued, Action Open, Action Reveal, Action CopyPath,
    Action Details);

/// <summary>A responsive card whose identity survives enqueue, cancel, retry and completion.
/// The visual hierarchy is file -> source facts -> target facts -> live state -> actions.</summary>
public static class TaskRow
{
    public static Element Render(WorkbenchItem item, bool selected, bool paused, TaskRowActions actions)
    {
        var d = item.Draft;
        var job = item.Execution;
        var stateText = job is not null ? Fmt.JobStateText(job.State)
            : d.Submitting ? "正在加入队列" : d.IsProbing ? "读取中" : d.Status == DraftStatus.Invalid ? "读取失败"
            : item.Issues.Any(i => i.Blocking) ? "需要修改设置" : "待开始";
        if (job?.State == JobState.Queued && paused) stateText = "等待继续";
        var (statusBackground, statusForeground) = Ui.ToneColors(StatusTones.For(job?.State, d.Status,
            d.IsProbing, item.Issues.Any(i => i.Blocking)));

        var header = Grid(columns: [GridSize.Auto, GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
            CheckBox(selected, v => { if (v != selected) actions.ToggleSelect(); }, string.Empty)
                .Width(40).MinWidth(40).MinHeight(40).VAlign(VerticalAlignment.Top)
                .AutomationName("选择 " + d.FileName).Grid(column: 0),
            Button(BodyLarge(d.FileName).SemiBold().TextWrapping(TextWrapping.Wrap)
                .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(2), actions.Select)
                .SubtleButton().HAlign(HorizontalAlignment.Stretch).ToolTip(d.Path)
                .Set(b => b.HorizontalContentAlignment = HorizontalAlignment.Left)
                .AutomationName("任务 " + d.FileName).Grid(column: 1),
            StatusPill(stateText, statusBackground, statusForeground).VAlign(VerticalAlignment.Top).Grid(column: 2));

        var sourceLines = new List<Element>();
        if (d.Snapshot is { } snapshot)
            sourceLines.AddRange(Fmt.SourceStreamLines(snapshot, d.Options)
                .Select(line => Body(line).TextWrapping(TextWrapping.Wrap).ToolTip(d.Path)));
        else
            sourceLines.Add(Body(d.JobId is not null ? "已保留执行参数；源媒体信息尚未重新读取"
                : d.IsProbing ? "正在读取媒体信息…" : "源媒体信息不可用")
                .TextWrapping(TextWrapping.Wrap).ToolTip(d.Path));
        var sourceMeta = d.Snapshot is { } source
            ? $"{Fmt.DurationMicros(source.DurationUs)} · {Fmt.Size(source.SizeBytes)}"
            : d.Path;
        sourceLines.Add(Body(sourceMeta).Foreground(Theme.SecondaryText)
            .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).ToolTip(d.Path));
        var targetTitle = Fmt.TargetSummary(d.Options);
        var targetMeta = TargetMeta(d.Options);

        var facts = Grid(columns: [GridSize.Star(), GridSize.Star()], rows: [GridSize.Auto],
            VStack(Space.Xs, new[] { Caption("源文件").Foreground(Theme.SecondaryText) }.Concat(sourceLines).ToArray()).Grid(column: 0),
            VStack(Space.Xs,
                Caption("输出格式").Foreground(Theme.SecondaryText),
                Body(targetTitle).TextWrapping(TextWrapping.Wrap),
                Body(targetMeta).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap)).Grid(column: 1))
            with { ColumnSpacing = Space.Md };

        var lines = new List<Element> { header, facts };

        if (job is { IsActive: true } && job.State != JobState.CancelRequested)
        {
            var fraction = job.Progress?.Fraction is double value ? Math.Clamp(value, 0, 1) : (double?)null;
            var percent = fraction is double f ? Math.Clamp((int)Math.Round(f * 100), 0, 99) : 0;
            var activity = job.State == JobState.Running
                ? fraction is null
                    ? $"已处理 {Fmt.DurationMicros(job.Progress?.ProcessedUs)} · {Fmt.Speed(job.Progress?.Speed)} · 无法估计剩余时间"
                    : $"{percent}% · {Fmt.Speed(job.Progress?.Speed)} · 剩余 {Fmt.Eta(job.Progress?.Eta)}"
                : job.State == JobState.Verifying ? "正在检查输出，检查通过后才会发布文件。"
                : job.State == JobState.Committing ? "正在安全保存，请稍候。"
                : "正在准备转换。";
            Element progressVisual = Empty();
            if (job.State is JobState.Running or JobState.Verifying or JobState.Committing)
                progressVisual = job.State == JobState.Running && fraction is null
                    ? ProgressIndeterminate().Height(8).AutomationName($"任务 {d.FileName} 转换进度，无法估计完成时间")
                    : Progress((fraction ?? 0) * 100).Height(8).AutomationName($"任务 {d.FileName} 转换进度");
            lines.Add(VStack(Space.Xs,
                Body(activity).Foreground(Theme.SecondaryText),
                progressVisual));
        }
        if (job is { State: JobState.Succeeded, Output: not null })
        {
            var inputSize = d.Snapshot?.SizeBytes ?? job.Job.SourceFingerprint.SizeBytes;
            var elapsed = job.Attempt?.EndedUtc - job.Attempt?.StartedUtc;
            lines.Add(VStack(Space.Xs,
                Body($"{Fmt.Size(inputSize)} → {Fmt.Size(job.Output.Size)} · 用时 {Fmt.Duration(elapsed)}")
                    .Foreground(Theme.SystemSuccess),
                Caption(job.Output.FinalPath).Foreground(Theme.SecondaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).ToolTip(job.Output.FinalPath)));
        }
        if (job is { State: JobState.Canceled })
            lines.Add(Body("转换已取消，可重试或修改参数后转换。")
                .Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap));
        else if (job is { State: JobState.Failed or JobState.Interrupted or JobState.NeedsAttention }
            || d.Status == DraftStatus.Invalid)
            lines.Add(Body(WorkbenchRules.FailureText(job?.Failure ?? d.Failure))
                .Foreground(Theme.SystemCritical).TextWrapping(TextWrapping.Wrap));
        if (item.CanEdit && d.IsReady)
        {
            foreach (var issue in item.Issues.Take(2))
                lines.Add(Body(WorkbenchRules.IssueText(issue))
                    .Foreground(issue.Blocking ? Theme.SystemCritical : Theme.SystemAttention)
                    .TextWrapping(TextWrapping.Wrap));
            var hidden = item.Issues.Count - 2;
            if (hidden > 0)
                lines.Add(Body(string.Format(UiText.MoreIssuesFormat, hidden))
                    .Foreground(Theme.SystemAttention).TextWrapping(TextWrapping.Wrap));
        }

        var buttons = new List<Element>();
        void ActionButton(string label, Action action, bool primary = false)
        {
            var button = Button(label, action).AutomationName(label + " " + d.FileName);
            buttons.Add(primary ? button.AccentButton() : button.SubtleButton());
        }
        if (job is { State: JobState.Succeeded, Output: not null })
        {
            ActionButton("打开文件", actions.Open, primary: true);
            ActionButton("打开文件夹", actions.Reveal);
            ActionButton("复制路径", actions.CopyPath);
        }
        if (job?.CanRetry == true || d.Status == DraftStatus.Invalid && d.CanEdit) ActionButton("重试", actions.Retry, primary: true);
        if (job?.State == JobState.Queued)
        {
            buttons.Add(Ui.IconButton("\uE70E", "上移 " + d.FileName, UiText.MoveUpTip, actions.MoveUp));
            buttons.Add(Ui.IconButton("\uE70D", "下移 " + d.FileName, UiText.MoveDownTip, actions.MoveDown));
            ActionButton("移出队列并编辑", actions.EditQueued, primary: true);
        }
        if (job?.CanCancel == true) ActionButton("取消任务", actions.Cancel);
        if (job is not null)
            ActionButton(job.State switch
            {
                JobState.Succeeded => "再次转换",
                JobState.NeedsAttention => "核对后再次转换",
                _ => "修改后转换",
            }, actions.Copy);
        if (job is not null || d.Status == DraftStatus.Invalid || item.Issues.Count > 2) ActionButton("技术详情", actions.Details);
        if (item.CanRemove) ActionButton("移除", actions.Remove);
        if (buttons.Count > 0)
        {
            lines.Add(Ui.Divider());
            lines.Add(FlexRow(buttons.ToArray()) with { Wrap = FlexWrap.Wrap, ColumnGap = Space.Sm, RowGap = Space.Xs });
        }

        return Border(VStack(Space.Sm, lines.ToArray()))
            .Padding(Space.Md)
            .CornerRadius(8)
            .Background(selected ? Theme.SubtleFill : Theme.CardBackground)
            .WithBorder(selected ? Theme.Accent : Theme.CardStroke, selected ? 2 : 1)
            .WithKey(item.Id.ToString());
    }

    private static Element StatusPill(string text, ThemeRef background, ThemeRef foreground)
        => Border(BodyStrong(text).Foreground(foreground).LiveRegion(AutomationLiveSetting.Polite))
            .Background(background)
            .CornerRadius(10)
            .Padding(8, 2);

    private static string TargetMeta(ConversionOptions options)
    {
        if (options.Mode != ConversionMode.VideoTranscode)
            return options.Mode == ConversionMode.Remux ? "仅复制原始轨道" : "仅音频输出";
        var quality = options.Quality switch
        {
            QualityTier.SmallerSize => "较小体积",
            QualityTier.HigherQuality => "更高质量",
            _ => "均衡",
        };
        var size = options.TargetWidth is not null || options.TargetHeight is not null
            ? $"{options.TargetWidth}×{options.TargetHeight}"
            : options.ResizeMode == ResizeMode.Max1080p ? "最高 1080p" : "保持原始";
        return $"{quality} · {size}";
    }
}
