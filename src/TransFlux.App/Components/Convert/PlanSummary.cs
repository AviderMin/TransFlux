using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Rules;
using TransFlux.App.Theming;

namespace TransFlux.App.Components.Convert;

/// <summary>Read-only plan summary with blocking-issue and track-drop hints (docs/03 PlanSummary).</summary>
public static class PlanSummary
{
    public static Element Render(PlanPreview? preview, IReadOnlyList<CompatibilityIssue> issues, bool hasReadyDraft, Action onCopyDetails)
    {
        var children = new List<Element> { Ui.SectionHeader(UiText.PlanSummary) };

        foreach (var issue in issues.Where(i => i.Blocking))
            children.Add(InfoBar("阻断项", issue.Message).Error().IsClosable(false).Margin(0, 2));
        foreach (var issue in issues.Where(i => !i.Blocking))
            children.Add(InfoBar("提示", issue.Message).Warning().IsClosable(false).Margin(0, 2));

        if (!hasReadyDraft)
        {
            children.Add(Ui.Dim("导入并读取媒体信息后，这里会显示编码器、设备与输出说明。"));
            return VStack(Space.Sm, children.ToArray());
        }

        if (preview is null)
        {
            children.Add(HStack(Space.Sm, ProgressIndeterminate().Width(120), Ui.Dim("正在生成转换计划…")));
            return VStack(Space.Sm, children.ToArray());
        }

        if (preview.Plan is { } plan)
        {
            var candidate = plan.CandidatePaths.FirstOrDefault();
            children.Add(Ui.KeyValue("编码器", $"{candidate?.EncoderName ?? "libx264"}（{BackendText(candidate?.Backend)}）"));
            children.Add(Ui.KeyValue("设备", string.IsNullOrEmpty(candidate?.DeviceId) ? "自动选择" : candidate!.DeviceId!));
            children.Add(Ui.KeyValue("输出", $"{plan.ExpectedOutput.Container.DisplayName()} · {plan.ExpectedOutput.Width ?? 0}×{plan.ExpectedOutput.Height ?? 0} · {plan.ExpectedOutput.StreamCount} 条轨道"));
            children.Add(Ui.KeyValue("预计体积", UiText.NoEstimate));
            if (plan.Warnings.Count > 0)
                children.Add(VStack(2, plan.Warnings.Select((w, i) => Ui.Dim("· " + w).WithKey(i.ToString())).ToArray()));
            children.Add(Button("复制技术详情", onCopyDetails).SubtleButton().AutomationName("复制计划技术详情"));
        }
        else if (preview.Failure is { } failure)
        {
            children.Add(InfoBar("无法生成计划", failure.UserMessageKey == "NON_SDR" ? UiText.HdrBlocked : failure.TechnicalDetail)
                .Error().IsClosable(false));
        }
        else if (!string.IsNullOrEmpty(preview.Error))
        {
            children.Add(InfoBar("无法生成计划", preview.Error!).Error().IsClosable(false));
        }

        return VStack(Space.Sm, children.ToArray());
    }

    private static string BackendText(HardwareBackend? backend) => backend switch
    {
        HardwareBackend.Cpu => "CPU",
        HardwareBackend.NvidiaNvenc => "NVIDIA NVENC",
        HardwareBackend.IntelQsv => "Intel QSV",
        HardwareBackend.AmdAmf => "AMD AMF",
        _ => "自动",
    };
}
