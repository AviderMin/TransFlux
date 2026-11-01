using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;
using TransFlux.Domain.Rules;

namespace TransFlux.App.Components.Workbench;

public sealed record OutputSettingsProps(IReadOnlyList<WorkbenchItem> Selection, IReadOnlyList<Preset> Presets,
    Action<Func<ConversionOptions, ConversionOptions>> Apply, Action PickOutput, Action ResetOutput,
    Func<string, Task> SavePreset);

/// <summary>One inspector: every field edits precisely the selected editable cards.</summary>
public sealed class OutputSettingsPanel : Component<OutputSettingsProps>
{
    public override Element Render()
    {
        var p = Props;
        var (advanced, setAdvanced) = UseState(false);
        var (presetName, setPresetName) = UseState(string.Empty);
        var (notice, setNotice) = UseState<string?>(null);
        var selection = p.Selection;
        var options = selection.Select(i => i.Draft.Options).ToList();
        var o = options.FirstOrDefault() ?? ConversionOptions.Default;
        var editable = selection.Count > 0 && selection.All(i => i.CanEdit);
        bool Same<T>(Func<ConversionOptions, T> get) => options.Select(get).Distinct().Count() <= 1;
        int Index<T>(Func<ConversionOptions, T> get, int value) => Same(get) ? value : -1;
        void Apply(Func<ConversionOptions, ConversionOptions> mutate) { if (editable) p.Apply(mutate); }
        Element Combo(string label, string[] items, int index, Action<int> change)
            => ComboBox(items, index, i => { if (i >= 0 && i < items.Length) change(i); })
                .Header(label).PlaceholderText("多种值").AutomationName(label).IsEnabled(editable).HAlign(HorizontalAlignment.Stretch);
        Element Number(string label, Func<ConversionOptions, double?> get, Func<ConversionOptions, double, ConversionOptions> mutate)
        {
            var value = Same(get) ? get(o) : null;
            return NumberBox(value.HasValue ? Optional<double>.Of(value.Value) : default, v => Apply(x => mutate(x, v)), label)
                .PlaceholderText(Same(get) ? "自动" : "多种值").AutomationName(label).IsEnabled(editable).HAlign(HorizontalAlignment.Stretch);
        }
        Element Pair(Element left, Element right)
            => Grid(columns: [GridSize.Star(), GridSize.Star()], rows: [GridSize.Auto],
                left.Grid(column: 0), right.Grid(column: 1)) with { ColumnSpacing = Space.Md };
        Element Group(string title, params Element[] fields)
            => VStack(Space.Sm, new[] { (Element)SubHeading(title) }.Concat(fields).ToArray());

        var body = new List<Element>();
        Element outputFooter = Empty();
        if (selection.Count == 0) body.Add(Ui.Dim("选择文件后，在这里设置输出格式、质量和保存位置。").TextWrapping(TextWrapping.Wrap));
        else if (!editable) body.Add(Ui.Dim("执行参数已锁定。等待中的任务可移出队列编辑；其他任务可复制为新任务。").TextWrapping(TextWrapping.Wrap));
        else body.Add(Ui.Dim("更改立即应用到已选项目；未修改的字段保持原值。").FontSize(12).TextWrapping(TextWrapping.Wrap));

        if (selection.Count > 0)
        {
            body.Add(Combo("媒体形式", ["视频", "音频"], Index(OutputTargets.IsAudio, OutputTargets.IsAudio(o) ? 1 : 0),
                i => Apply(x => OutputTargets.WithMedia(x, i == 1))));
            if (Same(OutputTargets.IsAudio))
            {
                var containers = (OutputTargets.IsAudio(o) ? OutputFormatCatalog.AudioContainers : OutputFormatCatalog.VideoContainers).ToArray();
                body.Add(Combo("容器格式", containers.Select(c => c.DisplayName()).ToArray(),
                    Index(x => x.Container, Array.IndexOf(containers, o.Container)), i => Apply(x => OutputTargets.WithContainer(x, containers[i]))));
                if (Same(x => x.Container))
                {
                    if (!OutputTargets.IsAudio(o))
                    {
                        var codecs = OutputFormatCatalog.VideoCodecs(o.Container).Append(VideoCodec.Copy).ToArray();
                        var currentVideo = o.Mode == ConversionMode.Remux ? VideoCodec.Copy : o.VideoCodec;
                        body.Add(Combo("视频编码", codecs.Select(c => c.Name()).ToArray(),
                            Index(x => x.Mode == ConversionMode.Remux ? VideoCodec.Copy : x.VideoCodec, Array.IndexOf(codecs, currentVideo)), i => Apply(x => OutputTargets.WithVideoCodec(x, codecs[i]))));
                    }
                    if (Same(x => x.Mode) && o.Mode != ConversionMode.Remux)
                    {
                        var codecs = BasicAudioCodecs(o.Container);
                        body.Add(Combo(OutputTargets.IsAudio(o) ? "编码格式" : "音频编码", codecs.Select(AudioLabel).ToArray(),
                            Index(x => OutputTargets.IsPcm(x.AudioCodec) ? AudioCodec.PcmS16le : x.AudioCodec, BasicAudioIndex(o.AudioCodec, codecs)),
                            i => Apply(x => codecs[i] == AudioCodec.PcmS16le && OutputTargets.IsPcm(x.AudioCodec)
                                ? x : OutputTargets.WithAudioCodec(x, codecs[i]))));
                        if (OutputTargets.IsAudio(o) && !o.AudioCodec.HasBitrate())
                            body.Add(Ui.Dim("无损编码不会恢复已损失的音质，输出文件可能更大。").FontSize(12).TextWrapping(TextWrapping.Wrap));
                    }
                    if (o.Mode == ConversionMode.Remux)
                        body.Add(Ui.Dim("转封装复制原始音视频编码；源轨道不兼容时会阻止开始。").FontSize(12).TextWrapping(TextWrapping.Wrap));
                }
                else body.Add(Ui.Dim("容器格式有多种值；统一容器后可设置编码。").FontSize(12).TextWrapping(TextWrapping.Wrap));
            }
            else body.Add(Ui.Dim("媒体形式有多种值；请先选择视频或音频。").FontSize(12).TextWrapping(TextWrapping.Wrap));
            body.Add(Ui.Dim("容器和编码分别选择；切换容器时仅调整不兼容的编码。").FontSize(12).TextWrapping(TextWrapping.Wrap));
            var presets = p.Presets.Where(pr => pr.Template.Mode == o.Mode && pr.Template.Container == o.Container
                && (o.Mode != ConversionMode.VideoTranscode || pr.Template.VideoCodec == o.VideoCodec)).ToArray();
            int PresetIndex(ConversionOptions x) => Array.FindIndex(presets, pr => Matches(pr.Template, x)) + 1;
            body.Add(Combo("使用预设", new[] { "自定义" }.Concat(presets.Select(pr => pr.Name)).ToArray(),
                Index(PresetIndex, PresetIndex(o)), i => { if (i > 0) Apply(x => ApplyPreset(presets[i - 1].Template, x)); }));
            if (!Same(x => x.Mode)) body.Add(Ui.Dim("已选项目使用不同转换方式。请先统一输出格式，或分别选择后编辑。").FontSize(12).TextWrapping(TextWrapping.Wrap));
            if (Same(x => x.Mode) && o.Mode == ConversionMode.VideoTranscode)
            {
                body.Add(Combo("质量", ["较小体积", "均衡", "更高质量"], Index(x => x.Quality, (int)o.Quality),
                    i => Apply(x => x with { Quality = (QualityTier)i, CrfOverride = null, VideoBitrateKbps = null })));
                int ResizeIndex(ConversionOptions x) => x.TargetWidth.HasValue || x.TargetHeight.HasValue ? 2 : x.ResizeMode == ResizeMode.Max1080p ? 1 : 0;
                body.Add(Combo("分辨率", ["保持原始", "最高 1080p", "自定义"], Index(x => (x.ResizeMode, x.TargetWidth, x.TargetHeight), ResizeIndex(o)),
                    i => Apply(x => x with { ResizeMode = i == 1 ? ResizeMode.Max1080p : ResizeMode.KeepDisplaySize,
                        TargetWidth = i == 2 ? x.TargetWidth ?? 1920 : null, TargetHeight = i == 2 ? x.TargetHeight ?? 1080 : null, AllowUpscale = i == 2 })));
                if (ResizeIndex(o) == 2)
                {
                    body.Add(Pair(
                        Number("宽度", x => x.TargetWidth, (x, v) => x with { TargetWidth = v > 0 ? (int)Math.Clamp(v, 2, 16384) : null }),
                        Number("高度", x => x.TargetHeight, (x, v) => x with { TargetHeight = v > 0 ? (int)Math.Clamp(v, 2, 16384) : null })));
                }
            }
            var directory = Same(x => x.OutputDirectory) ? o.OutputDirectory ?? "源文件旁的 TransFlux Output 文件夹" : "多种位置";
            outputFooter = VStack(Space.Sm, Ui.Divider(), Ui.FieldLabel("保存到"),
                TextBlock(directory).FontSize(12).TextWrapping(TextWrapping.Wrap).MaxLines(2).ToolTip(directory),
                HStack(Space.Sm, Button("更改…", p.PickOutput).IsEnabled(editable).AutomationName("更改输出位置"),
                    Button("使用源文件旁", p.ResetOutput).IsEnabled(editable).SubtleButton()));

            var advancedGroups = new List<Element>();
            if (Same(x => x.Mode))
            {
                if (o.Mode == ConversionMode.VideoTranscode)
                {
                    var videoFields = new List<Element>();
                    var maxCrf = o.VideoCodec is VideoCodec.Av1 or VideoCodec.Vp9 ? 63 : 51;
                    videoFields.Add(Number($"CRF（0–{maxCrf}，留空自动）", x => x.CrfOverride,
                        (x, v) => x with { CrfOverride = double.IsNaN(v) ? null : (int)Math.Clamp(v, 0, maxCrf), VideoBitrateKbps = null }));
                    videoFields.Add(Pair(
                        Number("视频码率 kbps", x => x.VideoBitrateKbps, (x, v) => x with { VideoBitrateKbps = v > 0 ? (int)Math.Clamp(v, 16, 100000) : null }),
                        Number("帧率 fps", x => x.FrameRateOverride, (x, v) => x with { FrameRateOverride = v > 0 ? Math.Clamp(v, 1, 240) : null })));
                    videoFields.Add(Combo("编码速度", ["更快", "均衡", "更慢"], Index(x => x.Speed, (int)o.Speed), i => Apply(x => x with { Speed = (SpeedTier)i })));
                    var hardware = Same(x => x.VideoCodec) && o.VideoCodec == VideoCodec.H264
                        ? new[] { "自动", "CPU", "NVIDIA NVENC", "Intel QSV", "AMD AMF" } : new[] { "自动（CPU）", "CPU" };
                    videoFields.Add(Combo("硬件", hardware, Index(x => x.Hardware, (int)o.Hardware < hardware.Length ? (int)o.Hardware : -1), i => Apply(x => x with { Hardware = (HardwareBackend)i })));
                    if (o.VideoCodec != VideoCodec.H264)
                        videoFields.Add(Ui.Dim("H.265、AV1、VP9 当前使用 CPU 编码；高分辨率 AV1 可能较慢。").FontSize(12).TextWrapping(TextWrapping.Wrap));
                    videoFields.Add(Ui.Dim("自动选择可用编码器；硬件编码不保证更小或更快。").FontSize(12).TextWrapping(TextWrapping.Wrap));
                    advancedGroups.Add(Group("视频编码", videoFields.ToArray()));
                }
                if (o.Mode != ConversionMode.Remux)
                {
                    var audioFields = new List<Element>();
                    if (selection.Count == 1 && selection[0].Draft.Snapshot is { } source)
                    {
                        var tracks = source.AudioStreams.ToArray();
                        var selectedTrack = Array.FindIndex(tracks, t => o.SelectedStreams.Contains(t.SourceIndex)) + 1;
                        audioFields.Add(Combo("音轨", new[] { "默认音轨" }.Concat(tracks.Select(t => $"音轨 {t.SourceIndex} · {t.Language ?? "未知语言"} · {t.Codec}")).ToArray(),
                            selectedTrack, i => Apply(x => x with { SelectedStreams = i == 0 ? [] : new[] { tracks[i - 1].SourceIndex } })));
                    }
                    if (o.AudioCodec.HasBitrate())
                        audioFields.Add(Pair(
                            Number("音频码率 kbps", x => x.AudioBitRateKbps, (x, v) => x with { AudioBitRateKbps = v > 0 ? (int)Math.Clamp(v, 16,
                                x.AudioCodec == AudioCodec.Mp3 ? 320 : x.AudioCodec == AudioCodec.Opus ? 510 : 512) : null }),
                            Number("采样率 Hz", x => x.AudioSampleRate, (x, v) => x with { AudioSampleRate = v > 0 ? (int)Math.Clamp(v, 8000, 192000) : null })));
                    else
                        audioFields.Add(Number("采样率 Hz", x => x.AudioSampleRate, (x, v) => x with { AudioSampleRate = v > 0 ? (int)Math.Clamp(v, 8000, 192000) : null }));
                    var channels = Number("声道数", x => x.AudioChannels, (x, v) => x with { AudioChannels = v > 0 ? (int)Math.Clamp(v, 1, 8) : null });
                    if (Same(x => OutputTargets.IsPcm(x.AudioCodec)) && OutputTargets.IsPcm(o.AudioCodec))
                        audioFields.Add(Pair(channels, Combo("PCM 位深", ["16-bit", "24-bit"],
                            Index(x => x.AudioCodec == AudioCodec.PcmS24le, o.AudioCodec == AudioCodec.PcmS24le ? 1 : 0),
                            i => Apply(x => OutputTargets.WithPcmBitDepth(x, i == 1)))));
                    else audioFields.Add(channels);
                    audioFields.Add(Ui.Dim("声道数留空时保留源声道布局；PCM 为无损编码，不会恢复源文件已丢失的信息。").FontSize(12).TextWrapping(TextWrapping.Wrap));
                    advancedGroups.Add(Group("音频", audioFields.ToArray()));
                }
            }
            var outputFields = new List<Element>();
            if (o.Mode != ConversionMode.Remux)
                outputFields.Add(Pair(
                    Number("裁剪开始（秒）", x => x.TrimStart?.TotalSeconds, (x, v) => x with { TrimStart = double.IsNaN(v) ? null : TimeSpan.FromSeconds(Math.Max(0, v)) }),
                    Number("裁剪结束（秒）", x => x.TrimEnd?.TotalSeconds, (x, v) => x with { TrimEnd = double.IsNaN(v) ? null : TimeSpan.FromSeconds(Math.Max(0, v)) })));
            outputFields.Add(Combo("元数据", ["清除个人信息", "保留标题与章节"], Index(x => x.MetadataPolicy, (int)o.MetadataPolicy), i => Apply(x => x with { MetadataPolicy = (MetadataPolicy)i })));
            outputFields.Add(TextBox(Optional<string>.Of(Same(x => x.OutputFileName) ? o.OutputFileName ?? "" : ""),
                text => Apply(x => x with { OutputFileName = string.IsNullOrWhiteSpace(text) ? null : text }),
                placeholderText: Same(x => x.OutputFileName) ? "自动生成，重名时保留两个文件" : "多种值", header: "输出文件名").AutomationName("输出文件名").IsEnabled(editable));
            advancedGroups.Add(Group(o.Mode == ConversionMode.Remux ? "输出" : "裁剪与输出", outputFields.ToArray()));
            if (selection.Count == 1 && editable)
            {
                advancedGroups.Add(Group("预设",
                    Ui.Dim("预设保存编码参数，不包含路径、文件名、裁剪范围和源音轨选择。").FontSize(12).TextWrapping(TextWrapping.Wrap),
                    TextBox(Optional<string>.Of(presetName), setPresetName, header: "预设名称").AutomationName("预设名称"),
                    Button("保存为预设", () => _ = SaveAsync()).SubtleButton()));
            }
            if (advancedGroups.Count > 0)
                body.Add(Expander("高级参数", VStack(Space.Md, advancedGroups.ToArray()), isExpanded: advanced, onIsExpandedChanged: setAdvanced)
                    .AutomationName("高级参数"));
        }
        async Task SaveAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(presetName)) { setNotice("请填写预设名称。"); return; }
                await p.SavePreset(presetName.Trim()); setNotice("已保存预设。"); setPresetName("");
            }
            catch (Exception ex) { setNotice("保存失败：" + ex.Message); }
        }
        if (notice is not null) body.Add(Ui.Dim(notice).FontSize(12).TextWrapping(TextWrapping.Wrap));
        var fields = VStack(Space.Md, body.ToArray());
        var scroll = ScrollViewer(fields).HorizontalScrollMode(ScrollMode.Disabled).VerticalScrollMode(ScrollMode.Auto)
            .Set(s => { s.HorizontalContentAlignment = HorizontalAlignment.Stretch; s.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; });
        return Ui.Card(Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            VStack(Space.Xs, SubHeading("输出设置"),
                Ui.Dim(selection.Count == 0 ? "尚未选择文件" : $"{(editable ? "正在编辑" : "查看参数")} {selection.Count} 项").FontSize(12))
                .Margin(0, 0, 0, Space.Lg).Grid(row: 0),
            scroll.Grid(row: 1), outputFooter.Grid(row: 2)));
    }

    private static IReadOnlyList<AudioCodec> BasicAudioCodecs(ContainerFormat container)
    {
        var all = OutputFormatCatalog.AudioCodecs(container);
        var basic = all.Where(codec => !OutputTargets.IsPcm(codec)).ToList();
        if (all.Any(OutputTargets.IsPcm)) basic.Add(AudioCodec.PcmS16le);
        return basic;
    }
    private static int BasicAudioIndex(AudioCodec codec, IReadOnlyList<AudioCodec> codecs)
    {
        var target = OutputTargets.IsPcm(codec) ? AudioCodec.PcmS16le : codec;
        for (var i = 0; i < codecs.Count; i++) if (codecs[i] == target) return i;
        return -1;
    }
    private static string AudioLabel(AudioCodec codec) => OutputTargets.IsPcm(codec) ? "PCM" : codec.Name();
    private static bool Matches(PresetRequestTemplate t, ConversionOptions o) => t == o.ToTemplate();
    private static ConversionOptions ApplyPreset(PresetRequestTemplate t, ConversionOptions o)
        => ConversionOptions.FromTemplate(t) with { OutputDirectory = o.OutputDirectory, OutputFileName = o.OutputFileName };
}
