using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Interop;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Application.Contracts;

namespace TransFlux.App.Components.Settings;

public sealed record SettingsPageProps(AppServices Services, AppSettings Settings, Func<AppSettings, Task> SaveSettings);

/// <summary>设置 page: appearance, language, output, hardware caveat, resources,
/// privacy/log cleanup. Explanations appear only next to their options (docs/03).</summary>
public sealed class SettingsPage : Component<SettingsPageProps>
{
    private static readonly string[] ThemeItems = { UiText.ThemeSystem, UiText.ThemeLight, UiText.ThemeDark };
    private static readonly string[] LanguageItems = { "跟随系统", "简体中文", "English" };

    public override Element Render()
    {
        var p = Props;
        var window = UseWindow()?.NativeWindow;
        var (draft, setDraft) = UseState(p.Settings);
        var (notice, setNotice) = UseState<string?>(null);
        var (saving, setSaving) = UseState(false);

        UseEffect(() => { setDraft(p.Settings); }, p.Settings);

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

        async Task SaveAsync()
        {
            if (saving) return;
            setSaving(true);
            try
            {
                await p.SaveSettings(draft);
                setNotice("设置已保存。并发与硬件相关变更对新任务生效。");
            }
            catch (Exception ex) { setNotice("保存失败：" + ex.Message); }
            finally { setSaving(false); }
        }

        var themeIndex = draft.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        var languageIndex = draft.Language switch { "zh-Hans" => 1, "en" => 2, _ => 0 };

        Element Section(string title, params Element[] children)
            => Ui.Card(VStack(Space.Md, new[] { TextBlock(title).SemiBold() }.Concat(children).ToArray()));

        var appearance = Section(UiText.Appearance,
            ComboBox(ThemeItems, themeIndex, i => Update(s => s with { Theme = i switch { 1 => "Light", 2 => "Dark", _ => "System" } })).Header(UiText.ThemeSystem),
            ComboBox(LanguageItems, languageIndex, i => Update(s => s with { Language = i switch { 1 => "zh-Hans", 2 => "en", _ => "system" } })).Header(UiText.Language));

        var output = Section(UiText.OutputRules,
            VStack(2, Ui.FieldLabel("默认输出位置（留空则使用源文件旁的 TransFlux Output）"),
                HStack(Space.Sm,
                    TextBlock(string.IsNullOrEmpty(draft.DefaultOutputDirectory) ? "源文件所在文件夹 / TransFlux Output" : draft.DefaultOutputDirectory!)
                        .Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).Flex(grow: 1, basis: 0),
                    Button("选择…", () => _ = PickDefaultOutputAsync()).AutomationName("选择默认输出位置"))),
            CheckBox(draft.PreserveTitleAndChaptersByDefault, v => Update(s => s with { PreserveTitleAndChaptersByDefault = v }), "默认保留标题与章节（可能含个人信息）"));

        var hardware = Section(UiText.Hardware,
            ToggleSwitch(draft.AllowAutomaticFallback, v => Update(s => s with { AllowAutomaticFallback = v }),
                onContent: "开启", offContent: "关闭", header: UiText.AutoFallback),
            TextBlock(UiText.HardwareCaveat).Foreground(Theme.SecondaryText).FontSize(12).TextWrapping(TextWrapping.Wrap),
            TextBlock("自动回退仅在设备初始化、硬件格式/滤镜路径等能力类错误时触发，每个任务最多一次。").Foreground(Theme.SecondaryText).FontSize(12).TextWrapping(TextWrapping.Wrap),
            ToggleSwitch(draft.FullVerification, v => Update(s => s with { FullVerification = v }),
                onContent: "全片校验", offContent: "抽样校验", header: "输出验证"));

        var resources = Section("资源与并发",
            NumberBox(draft.MaxConcurrentTranscodes, v => Update(s => s with { MaxConcurrentTranscodes = Math.Clamp((int)Math.Round(v), 1, 4) }),
                header: UiText.Concurrency + "（1–4）"),
            TextBlock("降低并发不会终止正在运行的任务，只影响新任务的开始。").Foreground(Theme.SecondaryText).FontSize(12).TextWrapping(TextWrapping.Wrap));

        var privacy = Section("隐私与日志",
            NumberBox(draft.LogRetentionDays, v => Update(s => s with { LogRetentionDays = Math.Clamp((int)Math.Round(v), 1, 365) }),
                header: UiText.LogCleanup + "（天）"),
            TextBlock("默认无遥测、无在线检查、无媒体上传；日志清理不会删除输出文件。").Foreground(Theme.SecondaryText).FontSize(12).TextWrapping(TextWrapping.Wrap));

        var noticeBar = notice is { } msg ? InfoBar("提示", msg).Informational().IsClosable(true).Closed(() => setNotice(null)).Margin(0, 0, 0, Space.Sm) : null;

        return FlexColumn(noticeBar,
            HStack(Space.Sm, Heading(UiText.SettingsTitle), Ui.Spacer(),
                Button("保存设置", () => _ = SaveAsync()).AccentButton().IsEnabled(!saving).AutomationName("保存设置")),
            ScrollView(VStack(Space.Lg, appearance, output, hardware, resources, privacy)).Flex(grow: 1, basis: 0))
            .Set(f => f.AlignItems = Microsoft.UI.Reactor.Layout.FlexAlign.Stretch).FlexPadding(Space.PagePadding);
    }
}