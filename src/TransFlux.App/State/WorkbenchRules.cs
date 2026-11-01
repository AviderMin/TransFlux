using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.App.State;

public sealed record WorkbenchItem(MediaDraft Draft, QueueRow? Execution)
{
    public Guid Id => Draft.MediaId;
    public bool CanEdit => Draft.CanEdit;
    public bool CanRemove => !Draft.Submitting && (CanEdit || Execution is { IsTerminal: true } || Execution?.State == JobState.NeedsAttention);
    public bool CanStart => CanEdit && Draft.IsReady && !Issues.Any(i => i.Blocking);
    public IReadOnlyList<CompatibilityIssue> Issues => WorkbenchRules.Validate(Draft);
}

public static class WorkbenchRules
{
    public static IReadOnlyList<CompatibilityIssue> Validate(MediaDraft draft)
    {
        if (draft.Snapshot is not { } source) return Array.Empty<CompatibilityIssue>();
        var o = draft.Options;
        var issues = MediaCompatibility.Validate(o.ToRequest(draft.MediaId), source).ToList();
        if ((o.TrimStart.HasValue || o.TrimEnd.HasValue)
            && (o.TrimStart is not { } start || o.TrimEnd is not { } end || start < TimeSpan.Zero || end <= start
                || source.DurationUs is long us && end.TotalMicroseconds > us))
            issues.Add(CompatibilityIssue.Block("TRIM_RANGE", "裁剪范围无效，请检查开始和结束时间。"));
        if (o.Mode == ConversionMode.VideoTranscode && source.SubtitleStreams.Any())
            issues.Add(CompatibilityIssue.Warn("SUBTITLE_LOSS", "此输出将不包含字幕；需要保留时请选择兼容的转封装。"));
        return issues;
    }
    public static string IssueText(CompatibilityIssue issue) => issue.Code switch
    {
        "NO_VIDEO" => "源文件没有视频，请选择提取音频格式。",
        "NO_AUDIO" => "源文件没有可提取的音轨，请更改输出格式。",
        "NON_SDR" => "此视频的色彩暂不支持转码，可尝试 MKV 转封装。",
        "HIGH_BIT_DEPTH" or "PIX_FMT" => "此视频的位深或像素格式暂不支持转码，可尝试转封装。",
        "REMUX_VIDEO" or "REMUX_AUDIO" or "REMUX_SUBTITLE" or "REMUX_SUBTITLE_BITMAP" or "REMUX_DV"
            => "源文件含暂不支持转封装的轨道，请选择其他输出方式。",
        "TRIM_REMUX" => "转封装不支持裁剪，请清除裁剪或选择视频转码。",
        "TRIM_RANGE" => "裁剪范围无效，请检查开始和结束时间。",
        "BAD_CONTAINER" or "CODEC_CONTAINER" => "输出格式与编码不兼容，请重新选择输出格式。",
        _ => issue.Message,
    };
    public static string FailureText(FailureInfo? failure) => failure?.Code switch
    {
        FailureCode.CoreMissing or FailureCode.CoreIntegrityFailed => "媒体核心不可用，请在设置中检查引擎安装。",
        FailureCode.DiskFull => "输出磁盘空间不足，请释放空间或更改输出位置后重试。",
        FailureCode.AccessDenied => "无法访问文件，请检查权限或选择其他输出文件夹。",
        FailureCode.InputChanged => "源文件已发生变化，请重新读取后再转换。",
        FailureCode.ProbeFailed => "无法读取媒体信息，请确认文件完整且格式受支持。",
        FailureCode.UnsupportedMedia or FailureCode.PlanInvalid => "当前参数不适用于源文件，请修改格式或参数后重试。",
        FailureCode.DeviceUnavailable or FailureCode.EncoderInitFailed => "硬件编码未能启动，可改用 CPU 重试。",
        FailureCode.VerifyFailed => "输出未通过检查，未发布结果文件，请重试或修改参数。",
        FailureCode.CommitFailed => "输出保存需要处理，请检查技术详情和目标文件，避免重复转换。",
        FailureCode.OutputConflict => "输出文件名冲突，请更改名称或输出位置。",
        FailureCode.Interrupted => "上次转换被中断，可重试或修改参数后转换。",
        _ => "转换未完成，请查看技术详情后重试。",
    };
}
