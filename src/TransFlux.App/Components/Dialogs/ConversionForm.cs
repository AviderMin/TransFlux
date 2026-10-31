using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Interop;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;

namespace TransFlux.App.Components.Dialogs;

/// <summary>Props for the shared grouped conversion form (presets-first, numeric editable).</summary>
public sealed record ConversionFormProps(
    ConversionOptions Current,
    bool Mixed,
    IReadOnlyList<Preset> Presets,
    Action<Func<ConversionOptions, ConversionOptions>> Apply,
    Func<string, Task> SavePreset);

/// <summary>
/// Grouped conversion form used by both the per-item ParameterDialog and the
/// BatchDialog: 预设 → 视频 → 音频 → 裁剪 → 元数据 → 硬件 → 输出. Presets come
/// first but every numeric value stays editable; all edits flow through Apply.
/// </summary>
public sealed class ConversionForm : Component<ConversionFormProps>
{
    private static readonly string[] QualityItems = { "较小体积", "均衡", "更高画质" };
    private static readonly string[] SpeedItems = { "更快", "均衡", "更慢" };
    private static readonly string[] ResizeItems = { "保持原始", "1080p 上限", "自定义" };
    private static readonly string[] HardwareItems = { "自动", "CPU", "NVIDIA NVENC", "Intel QSV", "AMD AMF" };
    private static readonly string[] MetadataItems = { "清除个人信息", "保留标题与章节" };

    public override Element Render()
    {
        var p = Props;
        var current = p.Current;
        var (presetName, setPresetName) = UseState(string.Empty);
        var (notice, setNotice) = UseState<string?>(null);
        var window = UseWindow()?.NativeWindow;

        void Apply(Func<ConversionOptions, ConversionOptions> mutate) => p.Apply(mutate);

        async Task SavePresetAsync()
        {
            var name = presetName;
            if (string.IsNullOrWhiteSpace(name)) { setNotice("请先填写预设名称。"); return; }
            try
            {
                await p.SavePreset(name.Trim());
                setPresetName(string.Empty);
                setNotice("已保存预设。");
            }
            catch (Exception ex) { setNotice("保存失败：" + ex.Message); }
        }

        async Task PickOutputAsync()
        {
            try
            {
                var folder = await FilePickerInterop.PickFolderAsync(window);
                if (folder is not null) Apply(o => o with { OutputDirectory = folder });
            }
            catch (Exception ex) { setNotice("选择输出目录失败：" + ex.Message); }
        }

        var children = new List<Element>();

        // ---- 预设 (presets first) ----
        children.Add(SectionTitle("预设"));
        if (p.Presets.Count == 0)
        {
            children.Add(Ui.Dim(UiText.NoPresets).FontSize(12));
        }
        else
        {
            var presetButtons = p.Presets.Select(pr =>
                Button(pr.Name, () => Apply(o => ApplyTemplate(pr, o)))
                    .SubtleButton()
                    .AutomationName(pr.Name)
                    .ToolTip(DescribeTemplate(pr.Template))
                    .WithKey(pr.PresetId.ToString())).ToArray();
            children.Add(FlexRow(presetButtons) with { Wrap = FlexWrap.Wrap, ColumnGap = Space.Sm, RowGap = Space.Sm });
        }
        children.Add(Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
            TextBox(Optional<string>.Of(presetName), setPresetName, placeholderText: UiText.PresetNamePlaceholder).AutomationName(UiText.PresetNamePlaceholder).Grid(column: 0),
            Button(UiText.SavePreset, () => _ = SavePresetAsync()).AutomationName(UiText.SavePreset).Margin(Space.Sm, 0, 0, 0)
                .VerticalAlignment(VerticalAlignment.Bottom).Grid(column: 1)));
        if (notice is { } msg)
            children.Add(InfoBar("提示", msg).Informational().IsClosable(true).Closed(() => setNotice(null)));

        // ---- 视频 ----
        if (current.Mode == ConversionMode.VideoTranscode)
        {
            children.Add(Ui.Divider());
            children.Add(SectionTitle("视频"));
            children.Add(Ui.FieldLabel("质量挡位"));
            children.Add(RadioButtons(QualityItems, (int)current.Quality, i => Apply(o => o with { Quality = (QualityTier)i })));
            children.Add(NumField("具体 CRF（留空按挡位）", (double?)current.CrfOverride, v => Apply(o => o with { CrfOverride = v > 0 ? (int)Math.Round(v) : null })));
            children.Add(NumField("目标码率 kbps（留空自动）", (double?)current.VideoBitrateKbps, v => Apply(o => o with { VideoBitrateKbps = v > 0 ? (int)Math.Round(v) : null })));
            children.Add(Ui.FieldLabel("分辨率"));
            var resizeIndex = current.TargetWidth.HasValue || current.TargetHeight.HasValue ? 2
                : current.ResizeMode == ResizeMode.Max1080p ? 1 : 0;
            children.Add(RadioButtons(ResizeItems, resizeIndex, i => Apply(o => i switch
            {
                1 => o with { ResizeMode = ResizeMode.Max1080p, TargetWidth = null, TargetHeight = null },
                2 => o with { ResizeMode = ResizeMode.KeepDisplaySize, TargetWidth = o.TargetWidth ?? 1920, TargetHeight = o.TargetHeight ?? 1080 },
                _ => o with { ResizeMode = ResizeMode.KeepDisplaySize, TargetWidth = null, TargetHeight = null },
            })));
            if (resizeIndex == 2)
            {
                children.Add(Grid(columns: [GridSize.Star(), GridSize.Star()], rows: [GridSize.Auto],
                    NumField("宽", (double?)current.TargetWidth, v => Apply(o => o with { TargetWidth = v > 0 ? (int)Math.Round(v) : null })).Margin(0, 0, Space.Sm, 0).Grid(column: 0),
                    NumField("高", (double?)current.TargetHeight, v => Apply(o => o with { TargetHeight = v > 0 ? (int)Math.Round(v) : null })).Grid(column: 1)));
            }
            children.Add(NumField("帧率 fps（留空保持原始）", current.FrameRateOverride, v => Apply(o => o with { FrameRateOverride = v > 0 ? v : null })));
            children.Add(Ui.FieldLabel("速度挡位"));
            children.Add(RadioButtons(SpeedItems, (int)current.Speed, i => Apply(o => o with { Speed = (SpeedTier)i })));
        }

        // ---- 音频 ----
        if (current.Mode != ConversionMode.Remux)
        {
            children.Add(Ui.Divider());
            children.Add(SectionTitle("音频"));
            var audioItems = current.Mode == ConversionMode.AudioExtract
                ? new[] { "AAC", "FLAC", "PCM 16-bit" }
                : new[] { "AAC", "FLAC" };
            var codecIndex = current.AudioCodec switch { AudioCodec.Flac => 1, AudioCodec.PcmS16le => 2, _ => 0 };
            if (codecIndex >= audioItems.Length) codecIndex = 0;
            children.Add(ComboBox(audioItems, codecIndex, i =>
            {
                var codec = i switch { 1 => AudioCodec.Flac, 2 => AudioCodec.PcmS16le, _ => AudioCodec.Aac };
                Apply(o =>
                {
                    var next = o with { AudioCodec = codec };
                    if (o.Mode == ConversionMode.AudioExtract)
                    {
                        next = codec switch
                        {
                            AudioCodec.Flac => next with { Container = ContainerFormat.Flac },
                            AudioCodec.PcmS16le => next with { Container = ContainerFormat.Wav },
                            _ => next with { Container = ContainerFormat.M4a },
                        };
                    }
                    return next;
                });
            }).Header("编码").HAlign(HorizontalAlignment.Stretch));
            children.Add(NumField("码率 kbps（留空自动）", (double?)current.AudioBitRateKbps, v => Apply(o => o with { AudioBitRateKbps = v > 0 ? (int)Math.Round(v) : null })));
            children.Add(NumField("采样率 Hz（留空保持）", (double?)current.AudioSampleRate, v => Apply(o => o with { AudioSampleRate = v > 0 ? (int)Math.Round(v) : null })));
            children.Add(NumField("声道数（留空保持）", (double?)current.AudioChannels, v => Apply(o => o with { AudioChannels = v > 0 ? (int)Math.Round(v) : null })));
        }

        // ---- 裁剪 ----
        if (current.Mode != ConversionMode.Remux)
        {
            children.Add(Ui.Divider());
            children.Add(SectionTitle("裁剪（时:分:秒.毫秒）"));
            children.Add(Grid(columns: [GridSize.Star(), GridSize.Star()], rows: [GridSize.Auto],
                VStack(4, Ui.FieldLabel("开始"),
                    TextBox(Optional<string>.Of(Fmt.FormatTimecode(current.TrimStart)), t => Apply(o => o with { TrimStart = Fmt.ParseTimecode(t) }), placeholderText: "00:00:00.000").AutomationName("裁剪开始时间"))
                    .Margin(0, 0, Space.Sm, 0).Grid(column: 0),
                VStack(4, Ui.FieldLabel("结束"),
                    TextBox(Optional<string>.Of(Fmt.FormatTimecode(current.TrimEnd)), t => Apply(o => o with { TrimEnd = Fmt.ParseTimecode(t) }), placeholderText: "00:00:10.000").AutomationName("裁剪结束时间"))
                    .Grid(column: 1)));
        }

        // ---- 元数据 ----
        children.Add(Ui.Divider());
        children.Add(SectionTitle("元数据"));
        children.Add(RadioButtons(MetadataItems, (int)current.MetadataPolicy, i => Apply(o => o with
        {
            MetadataPolicy = i == 1 ? MetadataPolicy.PreserveTitleAndChapters : MetadataPolicy.StripPersonalTags,
        })));

        // ---- 硬件 ----
        children.Add(Ui.Divider());
        children.Add(SectionTitle("硬件"));
        children.Add(ComboBox(HardwareItems, (int)current.Hardware, i => Apply(o => o with { Hardware = (HardwareBackend)i }))
            .HAlign(HorizontalAlignment.Stretch));
        children.Add(Ui.Dim(UiText.HardwareCaveat).FontSize(12).TextWrapping(TextWrapping.Wrap));

        // ---- 输出 ----
        children.Add(Ui.Divider());
        children.Add(SectionTitle("输出"));
        var dirText = string.IsNullOrEmpty(current.OutputDirectory) ? UiText.DefaultOutput : current.OutputDirectory!;
        children.Add(Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
            TextBlock(dirText).Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1)
                .ToolTip(dirText).VAlign(VerticalAlignment.Center).Grid(column: 0),
            Button(UiText.ChooseFolder, () => _ = PickOutputAsync()).AutomationName("选择输出目录").Grid(column: 1)));
        children.Add(TextBox(Optional<string>.Of(current.OutputFileName ?? string.Empty),
            t => Apply(o => o with { OutputFileName = string.IsNullOrWhiteSpace(t) ? null : t }),
            placeholderText: UiText.LeaveAuto, header: UiText.OutputFileName));

        if (p.Mixed) children.Insert(0, InfoBar("提示", UiText.DirtySelectionHint).Informational().IsClosable(false));

        return VStack(Space.Md, children.ToArray());
    }

    private static Element SectionTitle(string text) => TextBlock(text).SemiBold().FontSize(14);

    private static Element NumField(string header, double? value, Action<double> onChanged)
        => NumberBox(value.HasValue ? Optional<double>.Of(value.Value) : default(Optional<double>), onChanged, header)
            .AutomationName(header)
            .PlaceholderText(UiText.LeaveAuto)
            .MinWidth(140)
            .SpinButtons(NumberBoxSpinButtonPlacementMode.Compact)
            .HAlign(HorizontalAlignment.Stretch);

    public static IReadOnlyList<Preset> FilterPresets(IReadOnlyList<Preset> all, ConversionMode mode, ContainerFormat container)
    {
        var matched = all.Where(pr => pr.Template.Mode == mode && pr.Template.Container == container).ToList();
        if (matched.Count > 0) return matched;
        return all.Where(pr => pr.Template.Mode == mode).ToList();
    }

    public static ConversionOptions ApplyTemplate(Preset preset, ConversionOptions o)
    {
        var t = preset.Template;
        return o with
        {
            Mode = t.Mode,
            Container = t.Container,
            Quality = t.Quality,
            Speed = t.Speed,
            ResizeMode = t.ResizeMode,
            AllowUpscale = t.AllowUpscale,
            AudioCodec = t.AudioCodec,
            AudioBitRateKbps = t.AudioBitRateKbps,
            MetadataPolicy = t.MetadataPolicy,
            TargetWidth = null,
            TargetHeight = null,
        };
    }

    private static string DescribeTemplate(PresetRequestTemplate t)
    {
        var mode = t.Mode switch
        {
            ConversionMode.AudioExtract => "提取音频",
            ConversionMode.Remux => "只换封装",
            _ => "视频转码",
        };
        return mode + " · " + t.Container.DisplayName();
    }
}
