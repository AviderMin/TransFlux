using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;

namespace TransFlux.App.Components.Workbench;

/// <summary>A responsive card whose identity survives enqueue, cancel, retry and completion.</summary>
public static class TaskRow
{
    public static Element Render(WorkbenchItem item, bool selected, bool paused,
        Action select, Action toggle, Action remove, Action cancel, Action copy,
        Action retry, Action editQueued, Action open, Action reveal, Action details)
    {
        var d = item.Draft;
        var job = item.Execution;
        var stateText = job is not null ? Fmt.JobStateText(job.State)
            : d.Submitting ? "正在加入队列" : d.IsProbing ? "读取中" : d.Status == DraftStatus.Invalid ? "读取失败"
            : item.Issues.Any(i => i.Blocking) ? "需要修改设置" : "待开始";
        if (job?.State == JobState.Queued && paused) stateText = "等待继续";
        var status = TextBlock(stateText).SemiBold().FontSize(12)
            .Foreground(job is not null ? Ui.StateColor(job.State) : Theme.SecondaryText);
        var lines = new List<Element>();
        lines.Add(Grid(columns: [GridSize.Auto, GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
            CheckBox(selected, v => { if (v != selected) toggle(); }, string.Empty)
                .Width(32).MinWidth(32).AutomationName("选择 " + d.FileName).Grid(column: 0),
            Button(TextBlock(d.FileName).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1), select)
                .SubtleButton().HAlign(HorizontalAlignment.Stretch).ToolTip(d.Path)
                .Set(b => b.HorizontalContentAlignment = HorizontalAlignment.Left)
                .AutomationName("任务 " + d.FileName).Grid(column: 1),
            status.VAlign(VerticalAlignment.Center).Margin(Space.Sm, 0, 0, 0).Grid(column: 2)));
        var source = d.Snapshot is { } s
            ? $"{Fmt.Resolution(s.PrimaryVideo)} · {Fmt.DurationMicros(s.DurationUs)} · {Fmt.Size(s.SizeBytes)}"
            : d.Path;
        lines.Add(Ui.Dim(source).FontSize(12).TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).ToolTip(d.Path));
        var quality = d.Options.Mode == ConversionMode.VideoTranscode
            ? " · " + (d.Options.Quality switch { QualityTier.SmallerSize => "较小体积", QualityTier.HigherQuality => "更高质量", _ => "均衡" })
                + (d.Options.TargetWidth is not null || d.Options.TargetHeight is not null ? $" · {d.Options.TargetWidth}×{d.Options.TargetHeight}"
                    : d.Options.ResizeMode == ResizeMode.Max1080p ? " · 最高 1080p" : " · 保持原始") : string.Empty;
        lines.Add(TextBlock("→ " + Fmt.TargetSummary(d.Options) + quality).FontSize(12).TextWrapping(TextWrapping.Wrap));

        if (job is { IsActive: true } && job.State != JobState.CancelRequested)
        {
            if (job.State is JobState.Running or JobState.Verifying or JobState.Committing)
                lines.Add(Ui.ProgressBar(job.Progress?.Fraction ?? 0));
            var activity = job.State == JobState.Running
                ? $"{Math.Clamp((int)Math.Round((job.Progress?.Fraction ?? 0) * 100), 0, 99)}% · {Fmt.Speed(job.Progress?.Speed)} · 剩余 {Fmt.Eta(job.Progress?.Eta)}"
                : job.State == JobState.Verifying ? "正在检查输出，检查通过后才会发布文件。"
                : job.State == JobState.Committing ? "正在安全保存，请稍候。" : "正在准备转换。";
            lines.Add(Ui.Dim(activity).FontSize(12).TextWrapping(TextWrapping.Wrap));
        }
        if (job is { State: JobState.Succeeded, Output: not null })
        {
            var inputSize = d.Snapshot?.SizeBytes ?? job.Job.SourceFingerprint.SizeBytes;
            var elapsed = job.Attempt?.EndedUtc - job.Attempt?.StartedUtc;
            lines.Add(TextBlock($"{Fmt.Size(inputSize)} → {Fmt.Size(job.Output.Size)} · 用时 {Fmt.Duration(elapsed)}")
                .FontSize(12).Foreground(Theme.SystemSuccess));
            lines.Add(Ui.Dim(job.Output.FinalPath).FontSize(12).TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).ToolTip(job.Output.FinalPath));
        }
        if (job is { State: JobState.Failed or JobState.Interrupted or JobState.NeedsAttention }
            || d.Status == DraftStatus.Invalid)
            lines.Add(TextBlock(WorkbenchRules.FailureText(job?.Failure ?? d.Failure)).FontSize(12)
                .Foreground(Theme.SystemCritical).TextWrapping(TextWrapping.Wrap));
        if (item.CanEdit && d.IsReady)
            foreach (var issue in item.Issues.Take(2))
                lines.Add(TextBlock(WorkbenchRules.IssueText(issue)).FontSize(12).TextWrapping(TextWrapping.Wrap)
                    .Foreground(issue.Blocking ? Theme.SystemCritical : Theme.SystemAttention));

        var actions = new List<Element>();
        void ActionButton(string label, Action action) => actions.Add(Button(label, action).SubtleButton().AutomationName(label + " " + d.FileName));
        if (job is { State: JobState.Succeeded, Output: not null }) { ActionButton("打开文件", open); ActionButton("打开文件夹", reveal); }
        if (job?.CanRetry == true || d.Status == DraftStatus.Invalid && d.CanEdit) ActionButton("重试", retry);
        if (job?.State == JobState.Queued) ActionButton("移出队列并编辑", editQueued);
        if (job?.CanCancel == true) ActionButton("取消任务", cancel);
        if (job is not null && job.State != JobState.NeedsAttention)
            ActionButton(job.State == JobState.Succeeded ? "再次转换" : "修改后转换", copy);
        if (job is not null || d.Status == DraftStatus.Invalid) ActionButton("技术详情", details);
        if (item.CanRemove) ActionButton("移除", remove);
        if (actions.Count > 0) lines.Add(FlexRow(actions.ToArray()) with { Wrap = FlexWrap.Wrap, ColumnGap = 4, RowGap = 4 });
        return Border(VStack(Space.Sm, lines.ToArray())).Padding(Space.Lg).CornerRadius(8)
            .Background(selected ? Theme.SubtleFill : Theme.CardBackground)
            .WithBorder(selected ? Theme.Accent : Theme.CardStroke, 1).WithKey(item.Id.ToString());
    }
}
