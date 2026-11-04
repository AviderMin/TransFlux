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
using TransFlux.Domain.Media;

namespace TransFlux.App.Components.Dialogs;

/// <summary>Props for the settings dialog.</summary>
public sealed record SettingsDialogProps(
    AppServices Services,
    AppSettings Settings,
    Func<AppSettings, Task> Save,
    Action OnClosed);

/// <summary>
/// Settings + Diagnostics dialog, opened from the top bar (SurgeLink pattern).
/// Appearance / language / default output / concurrency 1-4 / auto-fallback /
/// full verification / log retention, plus a diagnostics section built from
/// <see cref="IEngineCapabilities"/> and <see cref="ICapabilityService"/>.
/// </summary>
public sealed class SettingsDialog : Component<SettingsDialogProps>
{
    private static readonly string[] ThemeItems = { UiText.ThemeSystem, UiText.ThemeLight, UiText.ThemeDark };

    public override Element Render()
    {
        var p = Props;
        var (draft, setDraft) = UseState(p.Settings);
        var (open, setOpen) = UseState(true);
        var (saving, setSaving) = UseState(false);
        var (notice, setNotice) = UseState<Notice?>(null);
        var (confirmDiscard, setConfirmDiscard) = UseState(false);
        var (identity, setIdentity) = UseState<CoreIdentity?>(null);
        var (engineCapabilities, setEngineCapabilities) = UseState<FfmpegCapabilityInfo?>(null);
        var (capabilities, setCapabilities) = UseState<CapabilitySnapshot?>(null);
        var (diagError, setDiagError) = UseState<string?>(null);
        var (probing, setProbing) = UseState(true);
        var (showDiagnostics, setShowDiagnostics) = UseState(false);
        var probeLifetime = UseRef<CancellationTokenSource?>(null);
        var window = UseWindow()?.NativeWindow;

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            probeLifetime.Current = cts;
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
                    var identityValue = p.Services.Core.GetIdentity();
                    var engineValue = await p.Services.Core.QueryAsync(cts.Token);
                    var capabilityValue = await p.Services.Capabilities.GetAsync(new CapabilityQuery(), cts.Token);
                    Post(() =>
                    {
                        setIdentity(identityValue);
                        setEngineCapabilities(engineValue);
                        setCapabilities(capabilityValue);
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Post(() => setDiagError(ex.Message)); }
                finally { if (!cts.IsCancellationRequested) Post(() => setProbing(false)); }
            });
            return () => cts.Cancel();
        }, p.Services);

        async Task RefreshHardwareAsync()
        {
            if (probing) return;
            setProbing(true); setDiagError(null);
            var ct = probeLifetime.Current?.Token ?? CancellationToken.None;
            try { setCapabilities(await p.Services.Capabilities.RefreshAsync(ct)); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { setDiagError(ex.Message); }
            finally { if (!ct.IsCancellationRequested) setProbing(false); }
        }

        void Update(Func<AppSettings, AppSettings> mutate) => setDraft(mutate(draft));

        async Task PickDefaultOutputAsync()
        {
            try
            {
                var folder = await FilePickerInterop.PickFolderAsync(window);
                if (folder is not null) Update(s => s with { DefaultOutputDirectory = folder });
            }
            catch (Exception ex) { setNotice(Notices.OperationFailed(ex.Message)); }
        }

        async Task<bool> CommitAsync(AppSettings final)
        {
            if (saving) return false;
            setSaving(true);
            try { await p.Save(final); return true; }
            catch (Exception ex) { setNotice(Notices.SaveFailed(ex.Message)); return false; }
            finally { setSaving(false); }
        }

        Element Section(string title, params Element[] children)
            => Ui.Card(VStack(Space.Md, new[] { TextBlock(title).SemiBold() }.Concat(children).ToArray()));

        var themeIndex = draft.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };

        var appearance = Section(UiText.Appearance,
            ComboBox(ThemeItems, themeIndex, i => Update(s => s with { Theme = i switch { 1 => "Light", 2 => "Dark", _ => "System" } }))
                .Header("主题").AutomationName("主题").HAlign(HorizontalAlignment.Stretch));

        var output = Section(UiText.OutputRules,
            VStack(2, Ui.FieldLabel("默认输出位置（留空则使用源文件旁的 TransFlux Output）"),
                Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
                    TextBlock(string.IsNullOrEmpty(draft.DefaultOutputDirectory) ? UiText.DefaultOutput : draft.DefaultOutputDirectory!)
                        .Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1)
                        .ToolTip(draft.DefaultOutputDirectory ?? UiText.DefaultOutput).VAlign(VerticalAlignment.Center).Grid(column: 0),
                    Button(UiText.ChooseFolder, () => _ = PickDefaultOutputAsync()).AutomationName("选择默认输出位置").Grid(column: 1))),
            Button("使用源文件旁", () => Update(s => s with { DefaultOutputDirectory = null })).SubtleButton(),
            CheckBox(draft.PreserveTitleAndChaptersByDefault, v => Update(s => s with { PreserveTitleAndChaptersByDefault = v }),
                "默认保留标题与章节（可能含个人信息）"));

        var resources = Section("转换资源",
            NumberBox(draft.MaxConcurrentTranscodes, v => Update(s => s with { MaxConcurrentTranscodes = Math.Clamp((int)Math.Round(v), 1, 4) }),
                header: UiText.Concurrency + "（1–4）").MinWidth(200)
                .Range(1, 4).SpinButtons().Set(n => n.SmallChange = 1),
            Ui.DimCaption("降低并发不会终止正在运行的任务，只影响新任务的开始。").TextWrapping(TextWrapping.Wrap),
            ToggleSwitch(draft.AllowAutomaticFallback, v => Update(s => s with { AllowAutomaticFallback = v }),
                onContent: "开启", offContent: "关闭", header: UiText.AutoFallback),
            Ui.DimCaption(UiText.HardwareCaveat).TextWrapping(TextWrapping.Wrap),
            ToggleSwitch(draft.FullVerification, v => Update(s => s with { FullVerification = v }),
                onContent: "全片校验", offContent: "抽样校验", header: "输出验证"));

        var privacy = Section("本地处理",
            Ui.DimCaption("默认无遥测、无在线检查、无媒体上传。移除任务和清空历史均不删除源文件或输出文件。")
                .TextWrapping(TextWrapping.Wrap));

        var diagChildren = new List<Element>
        {
            TextBlock("引擎与核心").SemiBold(),
            identity is null
                ? Ui.Dim("正在读取核心信息…")
                : VStack(Space.Xs,
                    Ui.KeyValue("核心版本", string.IsNullOrEmpty(identity.FfmpegVersion) ? "未知" : identity.FfmpegVersion),
                    Ui.KeyValue("核心哈希", identity.CoreHash),
                    Ui.KeyValue("FFmpeg", identity.FfmpegPath),
                    Ui.KeyValue("FFprobe", identity.FfprobePath)),
            Ui.Divider(),
            TextBlock("设备与候选路径").SemiBold(),
            Button(probing ? "正在试编码检测…" : "重新检测硬件", () => _ = RefreshHardwareAsync())
                .IsEnabled(!probing).AutomationName("重新检测硬件"),
        };
        if (capabilities is null)
        {
            if (diagError is null) diagChildren.Add(HStack(Space.Sm, ProgressIndeterminate().Width(120), Ui.Dim("正在检测…")));
        }
        else
        {
            if (capabilities.Adapters.Count == 0) diagChildren.Add(Ui.Dim("未枚举到显示适配器。"));
            foreach (var adapter in capabilities.Adapters)
                diagChildren.Add(VStack(Space.Xs,
                    TextBlock(adapter.Name).SemiBold().TextWrapping(TextWrapping.Wrap),
                    Ui.DimCaption($"{adapter.Vendor} · 驱动 {adapter.DriverVersion ?? "未知"} · 显存 {Fmt.Size(adapter.DedicatedMemoryBytes)}").TextWrapping(TextWrapping.Wrap),
                    Ui.DimCaption($"设备 {adapter.DxgiIndex} · {adapter.AdapterId}" + (adapter.IsSoftware ? " · 软件适配器，不用于硬件编码" : ""))
                        .TextWrapping(TextWrapping.Wrap)));
            diagChildren.Add(Ui.Divider());
            foreach (var candidate in capabilities.Candidates)
            {
                var device = capabilities.Adapters.FirstOrDefault(a => a.AdapterId == candidate.DeviceId);
                diagChildren.Add(VStack(Space.Xs,
                    TextBlock(candidate.EncoderName + (device is null ? "" : " · " + device.Name)).SemiBold().TextWrapping(TextWrapping.Wrap),
                    Caption(candidate.Available ? (candidate.TrialCompleted ? "试编码通过，可用" : "编码器可用")
                        : candidate.UnavailableReason ?? "不可用").TextWrapping(TextWrapping.Wrap),
                    showDiagnostics && candidate.DiagnosticDetail is { Length: > 0 } detail
                        ? Caption(detail).TextWrapping(TextWrapping.Wrap).Set(t => t.IsTextSelectionEnabled = true) : null));
            }
            diagChildren.Add(Button(showDiagnostics ? "隐藏技术详情" : "显示技术详情", () => setShowDiagnostics(!showDiagnostics))
                .AutomationName(showDiagnostics ? "隐藏技术详情" : "显示技术详情").SubtleButton());
        }
        diagChildren.Add(Ui.Divider());
        diagChildren.Add(TextBlock("编解码器支持").SemiBold());
        diagChildren.Add(Component<CodecCatalogSection, CodecCatalogSectionProps>(new(engineCapabilities, capabilities)));
        diagChildren.Add(Ui.Divider());
        diagChildren.Add(Ui.KeyValue("并发转码", p.Services.Options.MaxConcurrentTranscodes.ToString()));
        diagChildren.Add(Ui.KeyValue("自动回退", p.Services.Options.AllowAutomaticFallback ? "开启" : "关闭"));
        diagChildren.Add(Ui.KeyValue("完整验证", p.Services.Options.FullVerification ? "开启" : "关闭"));
        if (diagError is not null)
            diagChildren.Add(InfoBar("诊断读取失败", diagError).Warning().IsClosable(false));

        var diagnostics = Section(UiText.Diagnostics, diagChildren.ToArray());

        var body = VStack(Space.Lg,
            Ui.DimCaption("保存后，并发限制影响后续任务；回退与验证策略影响此后开始的任务。默认输出规则用于新增文件。").TextWrapping(TextWrapping.Wrap),
            notice is { } shown ? Ui.NoticeBar(shown, () => setNotice(null)) : null,
            confirmDiscard ? Ui.NoticeBar(Notice.Warning(UiText.PromptTitle, UiText.SettingsDirtyNotice)) : null,
            appearance,
            output,
            resources,
            privacy,
            diagnostics);

        return DialogChrome.Render(
            UiText.SettingsTitle,
            body,
            saving ? "正在保存…" : UiText.SaveSettingsAction,
            confirmDiscard ? UiText.DiscardChanges : UiText.Cancel,
            result =>
            {
                setOpen(false);
                DialogChrome.DeferClose(p.OnClosed);
            },
            contentWidth: 700,
            maxHeight: 580,
            isOpen: open,
            beforePrimaryClose: () => CommitAsync(draft),
            primaryEnabled: !saving,
            beforeSecondaryClose: () =>
            {
                // First cancel on unsaved edits keeps the dialog open and explains the choice.
                if (draft.Equals(p.Settings) || confirmDiscard) return Task.FromResult(true);
                setConfirmDiscard(true);
                return Task.FromResult(false);
            });
    }
}
