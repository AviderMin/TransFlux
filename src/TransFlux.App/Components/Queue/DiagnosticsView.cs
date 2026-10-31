using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Media;

namespace TransFlux.App.Components.Queue;

public sealed record DiagnosticsViewProps(AppServices Services);

/// <summary>诊断 tab: core identity, adapters, verified candidates, resource limits.</summary>
public sealed class DiagnosticsView : Component<DiagnosticsViewProps>
{
    public override Element Render()
    {
        var services = Props.Services;
        var (identity, setIdentity) = UseState<CoreIdentity?>(null);
        var (capabilities, setCapabilities) = UseState<CapabilitySnapshot?>(null);
        var (error, setError) = UseState<string?>(null);

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    setIdentity(services.Core.GetIdentity());
                    setCapabilities(await services.Capabilities.GetAsync(new CapabilityQuery { ForceRefresh = false }, cts.Token));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { setError(ex.Message); }
            });
            return () => cts.Cancel();
        }, services);

        var children = new List<Element>
        {
            TextBlock("引擎与核心").SemiBold(),
            identity is null
                ? Ui.Dim("正在读取核心信息…")
                : VStack(Space.Xs,
                    Ui.KeyValue("核心版本", string.IsNullOrEmpty(identity.FfmpegVersion) ? "未知" : identity.FfmpegVersion),
                    Ui.KeyValue("核心哈希", identity.CoreHash),
                    Ui.KeyValue("FFmpeg", identity.FfmpegPath),
                    Ui.KeyValue("FFprobe", identity.FfprobePath),
                    Ui.KeyValue("构建配置", string.IsNullOrEmpty(identity.BuildConfiguration) ? "未知" : identity.BuildConfiguration)),
            Ui.Divider(),
            TextBlock("设备与候选路径").SemiBold(),
        };

        if (capabilities is null)
        {
            children.Add(HStack(Space.Sm, ProgressIndeterminate().Width(120), Ui.Dim("正在检测…")));
        }
        else
        {
            if (capabilities.Adapters.Count == 0)
                children.Add(Ui.Dim("未枚举到显示适配器。"));
            foreach (var adapter in capabilities.Adapters)
                children.Add(Ui.KeyValue(adapter.Name, $"{adapter.Vendor} · 驱动 {adapter.DriverVersion ?? "未知"}{(adapter.IsSoftware ? " · 软件" : string.Empty)}"));

            children.Add(Ui.Divider());
            foreach (var candidate in capabilities.Candidates)
            {
                var state = candidate.Available ? "可用" : "不可用：" + (candidate.UnavailableReason ?? "未知");
                children.Add(Ui.KeyValue(candidate.EncoderName, $"{candidate.Backend} · {state}"));
            }
        }

        children.Add(Ui.Divider());
        children.Add(TextBlock("资源限制").SemiBold());
        children.Add(Ui.KeyValue("并发转码", services.Options.MaxConcurrentTranscodes.ToString()));
        children.Add(Ui.KeyValue("自动回退", services.Options.AllowAutomaticFallback ? "开启" : "关闭"));
        children.Add(Ui.KeyValue("完整验证", services.Options.FullVerification ? "开启" : "关闭"));

        if (error is not null) children.Add(InfoBar("诊断读取失败", error).Warning().IsClosable(false));

        return Ui.Card(VStack(Space.Sm, children.ToArray()));
    }
}
