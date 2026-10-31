using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;

namespace TransFlux.App.Components.Convert;

/// <summary>Output settings: 用途/容器/质量/速度/硬件/输出位置 (docs/03 OutputSettings).</summary>
public static class OutputSettingsPanel
{
    private static readonly string[] ModeItems = { "视频转码", "提取音频", "只换封装" };
    private static readonly string[] QualityItems = { "较小体积", "均衡", "更高画质" };
    private static readonly string[] SpeedItems = { "更快", "均衡", "更慢" };
    private static readonly string[] HardwareItems = { "自动（推荐）", "CPU", "NVIDIA NVENC", "Intel QSV", "AMD AMF" };

    public static Element Render(
        ConversionOptions current,
        bool mixed,
        IReadOnlyList<Preset> presets,
        Guid selectedPresetId,
        Action<Guid> onPreset,
        Action<Func<ConversionOptions, ConversionOptions>> apply)
    {
        var presetNames = presets.Select(p => p.Name).ToArray();
        var presetIndex = presets.ToList().FindIndex(p => p.PresetId == selectedPresetId && Matches(p.Template, current));
        var containers = current.Mode == ConversionMode.AudioExtract
            ? new[] { ContainerFormat.M4a, ContainerFormat.Flac, ContainerFormat.Wav }
            : new[] { ContainerFormat.Mp4, ContainerFormat.Mkv };

        var presetCombo = ComboBox(presetNames, presetIndex, i =>
        {
            if (i >= 0 && i < presets.Count) onPreset(presets[i].PresetId);
        }).Header("用途预设").PlaceholderText("自定义设置").HorizontalAlignment(HorizontalAlignment.Stretch);

        var modeCombo = ComboBox(ModeItems, (int)current.Mode, i =>
        {
            if (i < 0 || i >= ModeItems.Length) return;
            var mode = (ConversionMode)i;
            apply(o => o with { Mode = mode, Container = CoerceContainer(mode, o.Container) });
        }).Header("转换模式").HorizontalAlignment(HorizontalAlignment.Stretch);

        var containerCombo = ComboBox(containers.Select(c => c.ToString().ToUpperInvariant()).ToArray(),
            Array.IndexOf(containers, current.Container), i =>
            {
                if (i < 0 || i >= containers.Length) return;
                var container = containers[i];
                apply(o => o with
                {
                    Container = container,
                    AudioCodec = container switch
                    {
                        ContainerFormat.Flac => AudioCodec.Flac,
                        ContainerFormat.Wav => AudioCodec.PcmS16le,
                        _ => AudioCodec.Aac,
                    },
                });
            }).Header("输出格式").HorizontalAlignment(HorizontalAlignment.Stretch);

        var qualityCombo = ComboBox(QualityItems, (int)current.Quality, i =>
            apply(o => o with { Quality = (QualityTier)i })).Header(UiText.Quality).HorizontalAlignment(HorizontalAlignment.Stretch);

        var speedCombo = ComboBox(SpeedItems, (int)current.Speed, i =>
            apply(o => o with { Speed = (SpeedTier)i })).Header("编码速度").HorizontalAlignment(HorizontalAlignment.Stretch);

        var hardwareCombo = ComboBox(HardwareItems, (int)current.Hardware, i =>
            apply(o => o with { Hardware = (HardwareBackend)i })).Header("编码设备").HorizontalAlignment(HorizontalAlignment.Stretch);

        return VStack(Space.Md,
            TextBlock("转换设置").SemiBold().FontSize(18),
            mixed ? Ui.Dim("所选文件的设置不同，修改仅应用于对应选项。").FontSize(12).TextWrapping(TextWrapping.Wrap) : null,
            presetCombo,
            containerCombo,
            current.Mode == ConversionMode.VideoTranscode ? qualityCombo : null,
            current.Mode == ConversionMode.VideoTranscode
                ? Ui.Dim(QualityHint(current.Quality)).FontSize(12).TextWrapping(TextWrapping.Wrap)
                : Ui.Dim(current.Mode == ConversionMode.Remux ? "复制兼容轨道，不重新编码。" : "仅导出音轨，保留原有声道布局。")
                    .FontSize(12).TextWrapping(TextWrapping.Wrap),
            Expander("编码选项", VStack(Space.Md,
                modeCombo,
                current.Mode == ConversionMode.VideoTranscode ? speedCombo : null,
                current.Mode == ConversionMode.VideoTranscode ? hardwareCombo : null,
                current.Mode == ConversionMode.VideoTranscode ? Ui.Dim(UiText.HardwareCaveat).FontSize(12).TextWrapping(TextWrapping.Wrap) : null)));
    }

    private static bool Matches(PresetRequestTemplate template, ConversionOptions options)
        => template.Mode == options.Mode && template.Container == options.Container
           && template.Quality == options.Quality && template.Speed == options.Speed
           && template.ResizeMode == options.ResizeMode && template.AllowUpscale == options.AllowUpscale
           && template.AudioCodec == options.AudioCodec && template.AudioBitRateKbps == options.AudioBitRateKbps
           && template.MetadataPolicy == options.MetadataPolicy;

    private static ContainerFormat CoerceContainer(ConversionMode mode, ContainerFormat container)
    {
        bool audio = container is ContainerFormat.M4a or ContainerFormat.Flac or ContainerFormat.Wav;
        if (mode == ConversionMode.AudioExtract)
            return container == ContainerFormat.Flac || container == ContainerFormat.Wav ? container : ContainerFormat.M4a;
        return audio ? ContainerFormat.Mp4 : container;
    }

    private static string QualityHint(QualityTier q) => q switch
    {
        QualityTier.SmallerSize => UiText.QualitySmallerHint,
        QualityTier.HigherQuality => UiText.QualityHigherHint,
        _ => UiText.QualityBalancedHint,
    };
}
