using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;
using TransFlux.Domain.Rules;

namespace TransFlux.App.Components.Workbench;

public sealed record OutputSettingsProps(IReadOnlyList<WorkbenchItem> Selection, IReadOnlyList<Preset> Presets,
    Action<Func<ConversionOptions, ConversionOptions>> Apply, Action PickOutput, Action ResetOutput,
    Func<string, Task> SavePreset, Action ResetOptions);

/// <summary>One inspector: every field edits precisely the selected editable cards.</summary>
public sealed class OutputSettingsPanel : Component<OutputSettingsProps>
{
    public override Element Render()
    {
        var p = Props;
        var wide = UseBreakpoint(1100);
        var (videoOpen, setVideoOpen) = UseState(false);
        var (audioOpen, setAudioOpen) = UseState(false);
        var (subtitleOpen, setSubtitleOpen) = UseState(false);
        var (outputOpen, setOutputOpen) = UseState(false);
        var (presetOpen, setPresetOpen) = UseState(false);
        var (presetName, setPresetName) = UseState(string.Empty);
        var (notice, setNotice) = UseState<Notice?>(null);
        var selection = p.Selection;
        var options = selection.Select(i => i.Draft.Options).ToList();
        var o = options.FirstOrDefault() ?? ConversionOptions.Default;
        var editable = selection.Count > 0 && selection.All(i => i.CanEdit);
        bool Same<T>(Func<ConversionOptions, T> get) => options.Select(get).Distinct().Count() <= 1;
        int Index<T>(Func<ConversionOptions, T> get, int value) => Same(get) ? value : -1;
        void Apply(Func<ConversionOptions, ConversionOptions> mutate) { if (editable) p.Apply(mutate); }
        Element Combo(string label, string[] items, int index, Action<int> change, string placeholder = "多种值")
            => ComboBox(items, index, i => { if (i >= 0 && i < items.Length) change(i); })
                .Header(label).PlaceholderText(placeholder).AutomationName(label).IsEnabled(editable).HAlign(HorizontalAlignment.Stretch);
        // Optional numeric field: empty means auto (null), typed values are clamped by Range + callback.
        Element Number(string label, Func<ConversionOptions, double?> get, Func<ConversionOptions, double, ConversionOptions> mutate,
            double min, double max, double smallChange)
        {
            var value = Same(get) ? get(o) : null;
            return NumberBox(value.HasValue ? Optional<double>.Of(value.Value) : default, v => Apply(x => mutate(x, v)), label)
                .PlaceholderText(Same(get) ? "自动" : "多种值").AutomationName(label).IsEnabled(editable)
                .HAlign(HorizontalAlignment.Stretch)
                .Range(min, max).SpinButtons()
                .Set(n => n.SmallChange = smallChange);
        }
        Element Pair(Element left, Element right)
            => wide ? Grid(columns: [GridSize.Star(), GridSize.Star()], rows: [GridSize.Auto],
                left.Grid(column: 0), right.Grid(column: 1)) with { ColumnSpacing = Space.Md }
                : VStack(Space.Md, left, right);
        Element Section(string title, params Element[] fields)
            => VStack(Space.Md, new[] { (Element)BodyStrong(title) }.Concat(fields).ToArray());
        Element Group(string title, bool open, Action<bool> changed, params Element[] fields)
            => Expander(title, open ? VStack(Space.Md, fields) : Empty(), isExpanded: open, onIsExpandedChanged: changed)
                .AutomationName(title).HAlign(HorizontalAlignment.Stretch)
                .Set(e => e.HorizontalContentAlignment = HorizontalAlignment.Stretch);

        // Stream-selection helpers: v2 semantics label the same intent for every selected card;
        // bare source stream numbers are only shown when exactly one source is selected.
        string AudioTrackLabel(StreamDescriptor track)
            => $"音轨 {track.SourceIndex} · {track.Language ?? "未知语言"} · {track.Codec}";
        string SubtitleTrackLabel(StreamDescriptor track)
            => $"字幕 {track.SourceIndex} · {track.Language ?? "未知语言"} · {track.Codec}";
        int AudioModeIndex(ConversionOptions x, bool allowExplicit) => x.AudioMode switch
        {
            AudioSelectionMode.Auto => 0,
            AudioSelectionMode.None => 1,
            AudioSelectionMode.Explicit when allowExplicit => 2,
            _ => -1,
        };
        int SubtitleModeIndex(ConversionOptions x, bool allowExplicit) => x.SubtitleMode switch
        {
            SubtitleSelectionMode.AutoPreserve => 0,
            SubtitleSelectionMode.None => 1,
            SubtitleSelectionMode.Explicit when allowExplicit => 2,
            _ => -1,
        };
        int ExtractAudioIndex(ConversionOptions x, StreamDescriptor[] tracks)
        {
            if (x.AudioMode != AudioSelectionMode.Explicit || x.AudioStreamIndices.Count != 1) return 0;
            var found = Array.FindIndex(tracks, t => t.SourceIndex == x.AudioStreamIndices[0]);
            return found < 0 ? 0 : found + 1;
        }

        var body = new List<Element>();
        Element outputFooter = Empty();
        if (selection.Count == 0) body.Add(Ui.Dim("选择文件后，在这里设置输出格式、质量和保存位置。").TextWrapping(TextWrapping.Wrap));
        else if (!editable) body.Add(Ui.Dim("执行参数已锁定。等待中的任务可移出队列编辑；其他任务可复制为新任务。").TextWrapping(TextWrapping.Wrap));

        if (selection.Count > 0)
        {
            var formatFields = new List<Element>();
            var mediaField = Combo("媒体形式", ["视频", "音频"], Index(OutputTargets.IsAudio, OutputTargets.IsAudio(o) ? 1 : 0),
                i => Apply(x => OutputTargets.WithMedia(x, i == 1)));
            if (Same(OutputTargets.IsAudio))
            {
                var containers = (OutputTargets.IsAudio(o) ? OutputFormatCatalog.AudioContainers : OutputFormatCatalog.VideoContainers).ToArray();
                formatFields.Add(Pair(mediaField, Combo("容器格式", containers.Select(c => c.DisplayName()).ToArray(),
                    Index(x => x.Container, Array.IndexOf(containers, o.Container)), i => Apply(x => OutputTargets.WithContainer(x, containers[i])))
                    .ToolTip("切换容器仅调整不兼容的编码，其他参数保持不变。")));
                if (Same(x => x.Container))
                {
                    Element? videoCodecField = null;
                    if (!OutputTargets.IsAudio(o))
                    {
                        var codecs = OutputFormatCatalog.VideoCodecs(o.Container).Append(VideoCodec.Copy).ToArray();
                        var currentVideo = o.Mode == ConversionMode.Remux ? VideoCodec.Copy : o.VideoCodec;
                        videoCodecField = Combo("视频编码", codecs.Select(c => c.Name()).ToArray(),
                            Index(x => x.Mode == ConversionMode.Remux ? VideoCodec.Copy : x.VideoCodec, Array.IndexOf(codecs, currentVideo)), i => Apply(x => OutputTargets.WithVideoCodec(x, codecs[i])));
                    }
                    if (Same(x => x.Mode) && o.Mode != ConversionMode.Remux)
                    {
                        var codecs = BasicAudioCodecs(o.Container);
                        var audioCodecField = Combo(OutputTargets.IsAudio(o) ? "编码格式" : "音频编码", codecs.Select(AudioLabel).ToArray(),
                            Index(x => OutputTargets.IsPcm(x.AudioCodec) ? AudioCodec.PcmS16le : x.AudioCodec, BasicAudioIndex(o.AudioCodec, codecs)),
                            i => Apply(x => codecs[i] == AudioCodec.PcmS16le && OutputTargets.IsPcm(x.AudioCodec)
                                ? x : OutputTargets.WithAudioCodec(x, codecs[i])));
                        formatFields.Add(videoCodecField is null ? audioCodecField : Pair(videoCodecField, audioCodecField));
                        if (OutputTargets.IsAudio(o) && !o.AudioCodec.HasBitrate())
                            formatFields.Add(Ui.DimCaption("无损编码不会恢复已损失的音质，输出文件可能更大。").TextWrapping(TextWrapping.Wrap));
                    }
                    else if (videoCodecField is not null) formatFields.Add(videoCodecField);
                    if (o.Mode == ConversionMode.Remux)
                        formatFields.Add(Ui.DimCaption("直接复制音视频，不重新编码；不兼容的轨道会阻止开始。").TextWrapping(TextWrapping.Wrap));
                }
                else formatFields.Add(Ui.DimCaption("容器格式有多种值；统一容器后可设置编码。").TextWrapping(TextWrapping.Wrap));
            }
            else
            {
                formatFields.Add(mediaField);
                formatFields.Add(Ui.DimCaption("媒体形式有多种值；请先选择视频或音频。").TextWrapping(TextWrapping.Wrap));
            }
            body.Add(Section("输出格式", formatFields.ToArray()));
            var commonFields = new List<Element>();
            var presets = p.Presets.Where(pr => pr.Template.Mode == o.Mode && pr.Template.Container == o.Container
                && (o.Mode != ConversionMode.VideoTranscode || pr.Template.VideoCodec == o.VideoCodec)).ToArray();
            int PresetIndex(ConversionOptions x) => Array.FindIndex(presets, pr => Matches(pr.Template, x)) + 1;
            if (presets.Length > 0) commonFields.Add(Combo("使用预设", new[] { "自定义" }.Concat(presets.Select(pr => pr.Name)).ToArray(),
                Index(PresetIndex, PresetIndex(o)), i =>
                {
                    if (i > 0) Apply(x => x.ApplyPresetTemplate(presets[i - 1].Template));
                    else setNotice(Notices.PresetCustom());
                }).ToolTip(UiText.PresetHint));
            if (!Same(x => x.Mode)) body.Add(Ui.DimCaption("已选项目使用不同转换方式。请先统一输出格式，或分别选择后编辑。").TextWrapping(TextWrapping.Wrap));
            if (Same(x => x.Mode) && o.Mode == ConversionMode.VideoTranscode)
            {
                var qualityField = Combo("质量", ["较小体积", "均衡", "更高质量"],
                    Index(x => (x.Quality, x.CrfOverride, x.VideoBitrateKbps), o.CrfOverride.HasValue || o.VideoBitrateKbps.HasValue ? -1 : (int)o.Quality),
                    i => Apply(x => x with { Quality = (QualityTier)i, CrfOverride = null, VideoBitrateKbps = null }),
                    Same(x => (x.Quality, x.CrfOverride, x.VideoBitrateKbps)) ? "自定义参数" : "多种值");
                int ResizeIndex(ConversionOptions x) => x.TargetWidth.HasValue || x.TargetHeight.HasValue ? 2 : x.ResizeMode == ResizeMode.Max1080p ? 1 : 0;
                commonFields.Add(Pair(qualityField, Combo("分辨率", ["保持原始", "最高 1080p", "自定义"], Index(x => (x.ResizeMode, x.TargetWidth, x.TargetHeight), ResizeIndex(o)),
                    i => Apply(x => x with { ResizeMode = i == 1 ? ResizeMode.Max1080p : ResizeMode.KeepDisplaySize,
                        TargetWidth = i == 2 ? x.TargetWidth ?? 1920 : null, TargetHeight = i == 2 ? x.TargetHeight ?? 1080 : null, AllowUpscale = i == 2 }))));
                if (ResizeIndex(o) == 2)
                {
                    commonFields.Add(Pair(
                        Number("宽度", x => x.TargetWidth, (x, v) => x with { TargetWidth = FieldValues.IntOrNull(v, 2, 16384) }, 0, 16384, 2),
                        Number("高度", x => x.TargetHeight, (x, v) => x with { TargetHeight = FieldValues.IntOrNull(v, 2, 16384) }, 0, 16384, 2)));
                }
            }
            if (commonFields.Count > 0) body.Add(Section("常用设置", commonFields.ToArray()));
            var directory = Same(x => x.OutputDirectory) ? o.OutputDirectory ?? "源文件旁的 TransFlux Output 文件夹" : "多种位置";
            outputFooter = VStack(Space.Sm, Ui.Divider(), Ui.FieldLabel("保存到"),
                Caption(directory).TextWrapping(TextWrapping.Wrap).MaxLines(2).ToolTip(directory),
                FlexRow(Button("更改…", p.PickOutput).IsEnabled(editable).AutomationName("更改输出位置"),
                    Button("使用源文件旁", p.ResetOutput).IsEnabled(editable).SubtleButton().AutomationName("使用源文件旁"))
                    with { Wrap = FlexWrap.Wrap, ColumnGap = Space.Sm, RowGap = Space.Xs });

            var advancedGroups = new List<Element>();
            if (Same(x => x.Mode))
            {
                if (o.Mode == ConversionMode.VideoTranscode)
                {
                    var videoFields = new List<Element>();
                    if (Same(x => x.VideoCodec))
                    {
                        var maxCrf = o.VideoCodec is VideoCodec.Av1 or VideoCodec.Vp9 ? 63 : 51;
                        videoFields.Add(Number($"CRF（0–{maxCrf}，留空自动）", x => x.CrfOverride,
                            (x, v) => x with { CrfOverride = FieldValues.CrfOrNull(v, maxCrf), VideoBitrateKbps = null }, 0, maxCrf, 1));
                    }
                    else videoFields.Add(Ui.DimCaption("视频编码有多种值；统一视频编码后可设置 CRF。").TextWrapping(TextWrapping.Wrap));
                    videoFields.Add(Pair(
                        Number("视频码率 kbps", x => x.VideoBitrateKbps, (x, v) => x with { VideoBitrateKbps = FieldValues.IntOrNull(v, 16, 100000) }, 0, 100000, 16),
                        Number("帧率 fps", x => x.FrameRateOverride, (x, v) => x with { FrameRateOverride = FieldValues.DoubleOrNull(v, 1, 240) }, 0, 240, 1)));
                    videoFields.Add(Combo("编码速度", ["更快", "均衡", "更慢"], Index(x => x.Speed, (int)o.Speed), i => Apply(x => x with { Speed = (SpeedTier)i })));
                    var hardware = Same(x => x.VideoCodec) && o.VideoCodec == VideoCodec.H264
                        ? new[] { "自动", "CPU", "NVIDIA NVENC", "Intel QSV", "AMD AMF" } : new[] { "自动（CPU）", "CPU" };
                    videoFields.Add(Combo("硬件", hardware, Index(x => x.Hardware, (int)o.Hardware < hardware.Length ? (int)o.Hardware : -1), i => Apply(x => x with { Hardware = (HardwareBackend)i })));
                    if (o.VideoCodec != VideoCodec.H264)
                        videoFields.Add(Ui.DimCaption("H.265、AV1、VP9 当前使用 CPU 编码；高分辨率 AV1 可能较慢。").TextWrapping(TextWrapping.Wrap));
                    videoFields.Add(Ui.DimCaption("自动选择可用编码器；硬件编码不保证更小或更快。").TextWrapping(TextWrapping.Wrap));
                    advancedGroups.Add(Group("视频参数", videoOpen, setVideoOpen, videoFields.ToArray()));
                }
                var snapshot = selection.Count == 1 ? selection[0].Draft.Snapshot : null;
                var audioTracks = snapshot?.AudioStreams.ToArray() ?? Array.Empty<StreamDescriptor>();
                var subtitleTracks = snapshot?.SubtitleStreams.ToArray() ?? Array.Empty<StreamDescriptor>();

                // ---------------- Audio and audio tracks
                var audioFields = new List<Element>();
                if (o.Mode == ConversionMode.AudioExtract)
                {
                    if (selection.Count == 1 && audioTracks.Length > 0)
                    {
                        var labels = new[] { "自动（默认音轨）" }.Concat(audioTracks.Select(AudioTrackLabel)).ToArray();
                        audioFields.Add(Combo("音轨（单轨）", labels,
                            Index(x => ExtractAudioIndex(x, audioTracks), ExtractAudioIndex(o, audioTracks)),
                            i => Apply(x => i == 0
                                ? OutputTargets.WithAudioAuto(x)
                                : OutputTargets.WithAudioTrack(x, audioTracks[i - 1].SourceIndex))));
                        audioFields.Add(Ui.DimCaption("音频提取一次只输出一条音轨；批量时使用每个文件的默认音轨。").TextWrapping(TextWrapping.Wrap));
                    }
                    else if (selection.Count == 1)
                        audioFields.Add(Ui.DimCaption("源文件没有可提取的音轨。").TextWrapping(TextWrapping.Wrap));
                    else
                        audioFields.Add(Ui.DimCaption("音频提取需逐个文件选择音轨；批量时使用每个文件的默认音轨。").TextWrapping(TextWrapping.Wrap));
                }
                else
                {
                    var allowExplicitAudio = selection.Count == 1 && audioTracks.Length > 0;
                    var audioModeLabels = allowExplicitAudio
                        ? new[] { "保留全部音轨", "不输出音轨", "指定音轨" }
                        : new[] { "保留全部音轨", "不输出音轨" };
                    audioFields.Add(Combo("音轨", audioModeLabels,
                        Index(x => AudioModeIndex(x, allowExplicitAudio), AudioModeIndex(o, allowExplicitAudio)),
                        i => Apply(x => OutputTargets.WithAudioMode(x, (AudioSelectionMode)i))));
                    if (allowExplicitAudio && Same(x => x.AudioMode) && o.AudioMode == AudioSelectionMode.Explicit)
                    {
                        audioFields.Add(Ui.FieldLabel("指定音轨（可多选）"));
                        foreach (var track in audioTracks)
                        {
                            var trackIndex = track.SourceIndex;
                            audioFields.Add(CheckBox(o.AudioStreamIndices.Contains(trackIndex), checkedOn =>
                                Apply(x =>
                                {
                                    var selected = x.AudioStreamIndices.ToHashSet();
                                    if (checkedOn) selected.Add(trackIndex); else selected.Remove(trackIndex);
                                    return OutputTargets.WithAudioTrackSelection(x, selected.OrderBy(i => i).ToArray());
                                }), AudioTrackLabel(track))
                                .AutomationName($"音轨 {track.SourceIndex} {track.Language ?? "未知语言"}")
                                .IsEnabled(editable));
                        }
                    }
                    else if (o.AudioMode == AudioSelectionMode.None)
                        audioFields.Add(Ui.DimCaption("输出不包含音轨。").TextWrapping(TextWrapping.Wrap));
                    if (o.Mode == ConversionMode.Remux)
                        audioFields.Add(Ui.DimCaption("转封装按源流直接复制所选音轨，不重新编码。").TextWrapping(TextWrapping.Wrap));
                }
                if (o.Mode != ConversionMode.Remux)
                {
                    if (o.AudioCodec.HasBitrate())
                        audioFields.Add(Pair(
                            Number("音频码率 kbps", x => x.AudioBitRateKbps,
                                (x, v) => x with { AudioBitRateKbps = FieldValues.IntOrNull(v, 16,
                                    x.AudioCodec == AudioCodec.Mp3 ? 320 : x.AudioCodec == AudioCodec.Opus ? 510 : 512) }, 0, 512, 16),
                            Number("采样率 Hz", x => x.AudioSampleRate, (x, v) => x with { AudioSampleRate = FieldValues.IntOrNull(v, 8000, 192000) }, 0, 192000, 100)));
                    else
                        audioFields.Add(Number("采样率 Hz", x => x.AudioSampleRate, (x, v) => x with { AudioSampleRate = FieldValues.IntOrNull(v, 8000, 192000) }, 0, 192000, 100));
                    var channels = Number("声道数", x => x.AudioChannels, (x, v) => x with { AudioChannels = FieldValues.IntOrNull(v, 1, 8) }, 0, 8, 1);
                    if (Same(x => OutputTargets.IsPcm(x.AudioCodec)) && OutputTargets.IsPcm(o.AudioCodec))
                        audioFields.Add(Pair(channels, Combo("PCM 位深", ["16-bit", "24-bit"],
                            Index(x => x.AudioCodec == AudioCodec.PcmS24le, o.AudioCodec == AudioCodec.PcmS24le ? 1 : 0),
                            i => Apply(x => OutputTargets.WithPcmBitDepth(x, i == 1)))));
                    else audioFields.Add(channels);
                    audioFields.Add(Ui.DimCaption("声道数留空时保留源声道布局；PCM 为无损编码，不会恢复源文件已丢失的信息。").TextWrapping(TextWrapping.Wrap));
                }
                advancedGroups.Add(Group("音频与音轨", audioOpen, setAudioOpen, audioFields.ToArray()));

                // ---------------- Subtitles (soft only; burn-in is a later task)
                if (o.Mode != ConversionMode.AudioExtract)
                {
                    var subtitleFields = new List<Element>();
                    var allowExplicitSubtitles = selection.Count == 1 && subtitleTracks.Length > 0;
                    var subtitleModeLabels = allowExplicitSubtitles
                        ? new[] { "保留全部字幕（默认）", "不输出字幕", "指定字幕" }
                        : new[] { "保留全部字幕（默认）", "不输出字幕" };
                    subtitleFields.Add(Combo("字幕", subtitleModeLabels,
                        Index(x => SubtitleModeIndex(x, allowExplicitSubtitles), SubtitleModeIndex(o, allowExplicitSubtitles)),
                        i => Apply(x => OutputTargets.WithSubtitleMode(x, (SubtitleSelectionMode)i))));
                    if (allowExplicitSubtitles && Same(x => x.SubtitleMode) && o.SubtitleMode == SubtitleSelectionMode.Explicit)
                    {
                        subtitleFields.Add(Ui.FieldLabel("指定字幕（可多选；暂不支持烧录）"));
                        foreach (var track in subtitleTracks)
                        {
                            var trackIndex = track.SourceIndex;
                            subtitleFields.Add(CheckBox(o.SubtitleStreamIndices.Contains(trackIndex), checkedOn =>
                                Apply(x =>
                                {
                                    var selected = x.SubtitleStreamIndices.ToHashSet();
                                    if (checkedOn) selected.Add(trackIndex); else selected.Remove(trackIndex);
                                    return OutputTargets.WithSubtitleTracks(x, selected.OrderBy(i => i).ToArray());
                                }), SubtitleTrackLabel(track))
                                .AutomationName($"字幕 {track.SourceIndex} {track.Language ?? "未知语言"}")
                                .IsEnabled(editable));
                        }
                    }
                    else if (selection.Count == 1 && subtitleTracks.Length == 0)
                        subtitleFields.Add(Ui.DimCaption("源文件没有字幕流。").TextWrapping(TextWrapping.Wrap));
                    advancedGroups.Add(Group("字幕", subtitleOpen, setSubtitleOpen, subtitleFields.ToArray()));
                }
            }
            var outputFields = new List<Element>();
            if (Same(x => x.Mode) && o.Mode != ConversionMode.Remux)
                outputFields.Add(Pair(
                    Number("裁剪开始（秒）", x => x.TrimStart?.TotalSeconds, (x, v) => x with { TrimStart = FieldValues.SecondsOrNull(v) }, 0, 86400, 1),
                    Number("裁剪结束（秒）", x => x.TrimEnd?.TotalSeconds, (x, v) => x with { TrimEnd = FieldValues.SecondsOrNull(v) }, 0, 86400, 1)));
            else if (!Same(x => x.Mode))
                outputFields.Add(Ui.DimCaption("转换方式有多种值；统一转换方式后可设置裁剪。").TextWrapping(TextWrapping.Wrap));
            outputFields.Add(Combo("元数据", ["清除个人信息", "保留标题与章节"], Index(x => x.MetadataPolicy, (int)o.MetadataPolicy), i => Apply(x => x with { MetadataPolicy = (MetadataPolicy)i })));
            outputFields.Add(TextBox(Optional<string>.Of(Same(x => x.OutputFileName) ? o.OutputFileName ?? "" : ""),
                text => Apply(x => x with { OutputFileName = string.IsNullOrWhiteSpace(text) ? null : text }),
                placeholderText: Same(x => x.OutputFileName) ? "自动生成，重名时保留两个文件" : "多种值", header: "输出文件名").AutomationName("输出文件名").IsEnabled(editable));
            advancedGroups.Add(Group(o.Mode == ConversionMode.Remux ? "文件与元数据" : "裁剪、文件与元数据", outputOpen, setOutputOpen, outputFields.ToArray()));
            if (selection.Count == 1 && editable)
            {
                advancedGroups.Add(Group("保存为预设", presetOpen, setPresetOpen,
                    Ui.DimCaption("预设保存编码参数，不包含路径、文件名、裁剪范围和源音轨/字幕选择。").TextWrapping(TextWrapping.Wrap),
                    TextBox(Optional<string>.Of(presetName), setPresetName, header: "预设名称").AutomationName("预设名称"),
                    Button("保存为预设", () => _ = SaveAsync()).SubtleButton()));
            }
            if (advancedGroups.Count > 0) body.Add(Section("更多设置", advancedGroups.ToArray()));
        }
        async Task SaveAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(presetName)) { setNotice(Notices.PresetNameRequired()); return; }
                await p.SavePreset(presetName.Trim()); setNotice(Notices.PresetSaved()); setPresetName("");
            }
            catch (Exception ex) { setNotice(Notices.SaveFailed(ex.Message)); }
        }
        if (notice is { } shown) body.Add(Ui.NoticeBar(shown, () => setNotice(null)));
        var fields = VStack(Space.Md, body.ToArray()).Margin(Space.ScrollBarGutter, 0, Space.ScrollBarGutter, 0);
        var scroll = ScrollViewer(fields).HorizontalScrollMode(ScrollMode.Disabled).VerticalScrollMode(ScrollMode.Auto)
            .Set(s => { s.HorizontalContentAlignment = HorizontalAlignment.Stretch; s.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; });
        return Ui.Card(Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            VStack(Space.Xs,
                Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
                    SubHeading("输出设置").Grid(column: 0),
                    Button("重置", p.ResetOptions).SubtleButton().IsEnabled(editable)
                        .AutomationName("重置所选参数").ToolTip("重置所选项目的格式、裁剪和输出参数").Grid(column: 1)),
                Ui.DimCaption(selection.Count == 0 ? "尚未选择文件" : editable ? $"已选 {selection.Count} 项 · 更改即时生效" : $"查看 {selection.Count} 项 · 参数已锁定"))
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
}
