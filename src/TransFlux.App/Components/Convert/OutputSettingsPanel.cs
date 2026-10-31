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
    private static readonly string[] ContainerItems = { "MP4", "MKV", "M4A", "FLAC", "WAV" };
    private static readonly string[] QualityItems = { "较小体积", "均衡", "更高画质" };
    private static readonly string[] SpeedItems = { "更快", "均衡", "更慢" };
    private static readonly string[] HardwareItems = { "自动（推荐）", "CPU", "NVIDIA NVENC", "Intel QSV", "AMD AMF" };

    public static Element Render(
        ConversionOptions current,
        bool mixed,
        IReadOnlyList<Preset> presets,
        Guid selectedPresetId,
        Action<Guid> onPreset,
        Action<Func<ConversionOptions, ConversionOptions>> apply,
        string outputLocationText,
        Action onPickOutput)
    {
        var presetNames = presets.Select(p => p.Name).ToArray();
        var presetIndex = Math.Max(0, presets.ToList().FindIndex(p => p.PresetId == selectedPresetId));

        Element MixHint() => mixed ? TextBlock("（混合）").Foreground(Theme.SecondaryText).FontSize(12) : Empty();

        var presetCombo = ComboBox(presetNames, presetIndex, i =>
        {
            if (i >= 0 && i < presets.Count) onPreset(presets[i].PresetId);
        }).Header(UiText.Purpose);

        var modeCombo = ComboBox(ModeItems, (int)current.Mode, i =>
        {
            var mode = (ConversionMode)i;
            apply(o => o with { Mode = mode, Container = CoerceContainer(mode, o.Container) });
        }).Header("模式");

        var containerCombo = ComboBox(ContainerItems, (int)current.Container, i =>
            apply(o => o with { Container = (ContainerFormat)i })).Header(UiText.Container);

        var qualityCombo = ComboBox(QualityItems, (int)current.Quality, i =>
            apply(o => o with { Quality = (QualityTier)i })).Header(UiText.Quality);

        var speedCombo = ComboBox(SpeedItems, (int)current.Speed, i =>
            apply(o => o with { Speed = (SpeedTier)i })).Header(UiText.SpeedLabel);

        var hardwareCombo = ComboBox(HardwareItems, (int)current.Hardware, i =>
            apply(o => o with { Hardware = (HardwareBackend)i })).Header(UiText.Hardware);

        var outputRow = VStack(2,
            Ui.FieldLabel(UiText.OutputLocation),
            HStack(Space.Sm,
                TextBlock(outputLocationText).Foreground(Theme.PrimaryText).TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxLines(1).Flex(grow: 1, basis: 0),
                Button("选择…", onPickOutput).AutomationName("选择输出位置")));

        return VStack(Space.Md,
            HStack(Space.Sm, Ui.SectionHeader(UiText.OutputSettings), Ui.Spacer(), MixHint()),
            presetCombo,
            modeCombo,
            containerCombo,
            qualityCombo,
            speedCombo,
            hardwareCombo,
            TextBlock(QualityHint(current.Quality)).Foreground(Theme.SecondaryText).FontSize(12)
                .TextWrapping(TextWrapping.Wrap),
            TextBlock(UiText.HardwareCaveat).Foreground(Theme.SecondaryText).FontSize(12)
                .TextWrapping(TextWrapping.Wrap),
            Ui.Divider(),
            outputRow);
    }

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
