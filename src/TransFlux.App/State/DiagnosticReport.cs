using System.Text;
using System.Text.RegularExpressions;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;

namespace TransFlux.App.State;

/// <summary>Builds a user-exportable diagnostic report. Paths and file names are redacted by default.</summary>
public static partial class DiagnosticReport
{
    public static string Build(WorkbenchItem item, DateTimeOffset? generatedUtc = null)
    {
        var d = item.Draft;
        var job = item.Execution;
        var attempt = job?.Attempt;
        var plan = attempt?.PlanSnapshot;
        var candidate = ResolveExecutedCandidate(attempt);
        var expected = plan?.ExpectedOutput;
        var failure = job?.Failure ?? d.Failure;

        var sb = new StringBuilder();
        sb.AppendLine("TransFlux 诊断报告");
        sb.AppendLine("生成时间: " + (generatedUtc ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz"));
        sb.AppendLine("任务 ID: " + (job?.Job.JobId.ToString("D") ?? d.MediaId.ToString("D")));
        sb.AppendLine("状态: " + (job is null ? DraftStateText(d.Status) : Fmt.JobStateText(job.State)));
        sb.AppendLine("源文件: " + Redact(d.Path));
        sb.AppendLine("输出文件: " + Redact(job?.Output?.FinalPath ?? "尚未生成"));
        sb.AppendLine();
        sb.AppendLine("-- 执行信息 --");
        sb.AppendLine("核心哈希: " + (attempt?.CoreHash ?? "—"));
        sb.AppendLine("执行候选: " + (attempt?.ExecutedCandidateId ?? "未记录"));
        sb.AppendLine("执行编码器: " + (candidate?.EncoderName ?? "未知"));
        sb.AppendLine("执行后端: " + (candidate?.Backend.ToString() ?? "—"));
        sb.AppendLine("设备: " + (candidate?.DeviceId ?? attempt?.DeviceIdentity ?? "—"));
        sb.AppendLine("回退原因: " + (attempt?.FallbackReason ?? "无"));
        sb.AppendLine("尝试序号: " + (attempt?.Ordinal.ToString() ?? "—"));
        if (expected is not null)
        {
            sb.AppendLine("流映射: " + expected.VideoStreamCount + " 视频 / " + expected.AudioStreamCount + " 音频 / " + expected.StreamCount + " 总流");
            sb.AppendLine("输出容器: " + expected.Container.DisplayName());
            sb.AppendLine("视频编码: " + (expected.VideoCodec?.Name() ?? "复制/无"));
            sb.AppendLine("音频编码: " + (expected.AudioCodec?.Name() ?? "复制/无"));
        }
        sb.AppendLine("只读参数: " + (candidate is null ? "—" : string.Join(" ", candidate.Arguments)));
        sb.AppendLine("计划参数: " + (plan is null ? "—" : string.Join(" ", plan.ParameterPlan)));
        sb.AppendLine("计划说明: " + (candidate?.Rationale ?? "—"));
        sb.AppendLine("验证摘要: " + (job?.Output?.VerificationSummary ?? "—"));
        sb.AppendLine();
        sb.AppendLine("-- 错误信息 --");
        sb.AppendLine("错误码: " + (failure?.Code.ToString() ?? "无"));
        sb.AppendLine("错误消息键: " + (failure?.UserMessageKey ?? "无"));
        sb.AppendLine("技术详情: " + (failure?.TechnicalDetail ?? "无"));
        sb.AppendLine("FFmpeg 尾部: " + (attempt?.StderrTail ?? "无"));
        return Redact(sb.ToString());
    }

    /// <summary>
    /// Resolves the candidate an attempt actually executed. Historical rows may lack the id;
    /// a single-candidate plan is unambiguous, otherwise the value is unknown and must not be
    /// replaced by a guess such as CandidatePaths[0] (GPU failure followed by CPU success).
    /// </summary>
    public static ExecutionCandidate? ResolveExecutedCandidate(Attempt? attempt)
    {
        var plan = attempt?.PlanSnapshot;
        if (plan is null) return null;
        if (!string.IsNullOrEmpty(attempt!.ExecutedCandidateId))
            return plan.CandidatePaths.FirstOrDefault(c => c.CandidateId == attempt.ExecutedCandidateId);
        return plan.CandidatePaths.Count == 1 ? plan.CandidatePaths[0] : null;
    }

    private static string DraftStateText(DraftStatus status) => status switch
    {
        DraftStatus.Probing => "正在读取媒体信息",
        DraftStatus.Invalid => "媒体不可用",
        _ => "未开始",
    };

    /// <summary>Default privacy: absolute Windows/UNC paths and the current user profile are removed.</summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            value = value.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        value = AbsolutePathRegex().Replace(value, "[path]");
        value = UncPathRegex().Replace(value, "[path]");
        return value;
    }

    [GeneratedRegex(@"(?<![\w])([A-Za-z]:\\[^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AbsolutePathRegex();

    [GeneratedRegex(@"\\\\[^\\\s]+\\[^\r\n]+")]
    private static partial Regex UncPathRegex();
}
