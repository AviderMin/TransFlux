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
    private static readonly string[] LanguageItems = { "跟随系统", "简体中文", "English" };

    public override Element Render()
    {
        var p = Props;
        var (draft, setDraft) = UseState(p.Settings);
        var (open, setOpen) = UseState(true);
        var (notice, setNotice) = UseState<string?>(null);
        var (identity, setIdentity) = UseState<CoreIdentity?>(null);
        var (capabilities, setCapabilities) = UseState<CapabilitySnapshot?>(null);
        var (diagError, setDiagError) = UseState<string?>(null);
        var window = UseWindow()?.NativeWindow;

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    setIdentity(p.Services.Core.GetIdentity());
                    setCapabilities(await p.Services.Capabilities.GetAsync(new CapabilityQuery(), cts.Token));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { setDiagError(ex.Message); }
            });
            return () => cts.Cancel();
        }, p.Services);

        void Update(Func<AppSettings, AppSettings> mutate) => setDraft(mutate(draft));

        async Task PickDefaultOutputAsync()
        {
            try
            {
                var folder = await FilePickerInterop.PickFolderAsync(window);
                if (folder is not null) Update(s => s with { DefaultOutputDirectory = folder });
            }
            catch (Exception ex) { setNotice(ex.Message); }
        }

        async Task CommitAsync(AppSettings final)
        {
            try { await p.Save(final); }
            catch (Exception ex) { setNotice("保存失败：" + ex.Message); }
            p.OnClosed();
        }

        Element Section(string title, params Element[] children)
            => Ui.Card(VStack(Space.Md, new[] { TextBlock(title).SemiBold() }.Concat(children).ToArray()));

        var themeIndex = draft.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        var languageIndex = draft.Language switch { "zh-Hans" => 1, "en" => 2, _ => 0 };

        var appearance = Section(UiText.Appearance,
            ComboBox(ThemeItems, themeIndex, i => Update(s => s with { Theme = i switch { 1 => "Light", 2 => "Dark", _ => "System" } }))
                .Header(UiText.ThemeSystem).HAlign(HorizontalAlignment.Stretch),
            ComboBox(LanguageItems, languageIndex, i => Update(s => s with { Language = i switch { 1 => "zh-Hans", 2 => "en", _ => "system" } }))
                .Header(UiText.Language).HAlign(HorizontalAlignment.Stretch));

        var output = Section(UiText.OutputRules,
            VStack(2, Ui.FieldLabel("默认输出位置（留空则使用源文件旁的 TransFlux Output）"),
                Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
                    TextBlock(string.IsNullOrEmpty(draft.DefaultOutputDirectory) ? UiText.DefaultOutput : draft.DefaultOutputDirectory!)
                        .Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1)
                        .ToolTip(draft.DefaultOutputDirectory ?? UiText.DefaultOutput).VAlign(VerticalAlignment.Center).Grid(column: 0),
                    Button(UiText.ChooseFolder, () => _ = PickDefaultOutputAsync()).AutomationName("选择默认输出位置").Grid(column: 1))),
            CheckBox(draft.PreserveTitleAndChaptersByDefault, v => Update(s => s with { PreserveTitleAndChaptersByDefault = v }),
                "默认保留标题与章节（可能含个人信息）"));

        var resources = Section("转换资源",
            NumberBox(draft.MaxConcurrentTranscodes, v => Update(s => s with { MaxConcurrentTranscodes = Math.Clamp((int)Math.Round(v), 1, 4) }),
                header: UiText.Concurrency + "（1–4）").MinWidth(200),
            TextBlock("降低并发不会终止正在运行的任务，只影响新任务的开始。").Foreground(Theme.SecondaryText).FontSize(12).TextWrapping(TextWrapping.Wrap),
            ToggleSwitch(draft.AllowAutomaticFallback, v => Update(s => s with { AllowAutomaticFallback = v }),
                onContent: "开启", offContent: "关闭", header: UiText.AutoFallback),
            TextBlock(UiText.HardwareCaveat).Foreground(Theme.SecondaryText).FontSize(12).TextWrapping(TextWrapping.Wrap),
            ToggleSwitch(draft.FullVerification, v => Update(s => s with { FullVerification = v }),
                onContent: "全片校验", offContent: "抽样校验", header: "输出验证"));

        var privacy = Section("隐私与日志",
            NumberBox(draft.LogRetentionDays, v => Update(s => s with { LogRetentionDays = Math.Clamp((int)Math.Round(v), 1, 365) }),
                header: UiText.LogCleanup + "（天）").MinWidth(200),
            TextBlock("默认无遥测、无在线检查、无媒体上传；日志清理不会删除输出文件。").Foreground(Theme.SecondaryText).FontSize(12).TextWrapping(TextWrapping.Wrap));

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
        };
        if (capabilities is null)
        {
            if (diagError is null) diagChildren.Add(HStack(Space.Sm, ProgressIndeterminate().Width(120), Ui.Dim("正在检测…")));
        }
        else
        {
            if (capabilities.Adapters.Count == 0) diagChildren.Add(Ui.Dim("未枚举到显示适配器。"));
            foreach (var adapter in capabilities.Adapters)
                diagChildren.Add(Ui.KeyValue(adapter.Name, adapter.Vendor + " · 驱动 " + (adapter.DriverVersion ?? "未知")));
            diagChildren.Add(Ui.Divider());
            foreach (var candidate in capabilities.Candidates)
                diagChildren.Add(Ui.KeyValue(candidate.EncoderName,
                    candidate.Backend + " · " + (candidate.Available ? "可用" : "不可用：" + (candidate.UnavailableReason ?? "未知"))));
        }
        diagChildren.Add(Ui.Divider());
        diagChildren.Add(Ui.KeyValue("并发转码", p.Services.Options.MaxConcurrentTranscodes.ToString()));
        diagChildren.Add(Ui.KeyValue("自动回退", p.Services.Options.AllowAutomaticFallback ? "开启" : "关闭"));
        diagChildren.Add(Ui.KeyValue("完整验证", p.Services.Options.FullVerification ? "开启" : "关闭"));
        if (diagError is not null)
            diagChildren.Add(InfoBar("诊断读取失败", diagError).Warning().IsClosable(false));

        var diagnostics = Section(UiText.Diagnostics, diagChildren.ToArray());

        var body = VStack(Space.Lg,
            Ui.Dim("更改在保存后生效。").FontSize(12).TextWrapping(TextWrapping.Wrap),
            notice is { } msg ? InfoBar("提示", msg).Informational().IsClosable(true).Closed(() => setNotice(null)) : null,
            appearance,
            output,
            resources,
            privacy,
            diagnostics);

        return DialogChrome.Render(
            UiText.SettingsTitle,
            body,
            UiText.SaveSettingsAction,
            "取消",
            result =>
            {
                var final = draft;
                setOpen(false);
                DialogChrome.DeferClose(() =>
                {
                    if (result == ContentDialogResult.Primary) _ = CommitAsync(final);
                    else p.OnClosed();
                });
            },
            contentWidth: 700,
            maxHeight: 580,
            isOpen: open);
    }
}
