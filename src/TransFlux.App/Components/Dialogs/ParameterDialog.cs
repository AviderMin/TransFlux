using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Presets;

namespace TransFlux.App.Components.Dialogs;

/// <summary>Props for the per-item parameter dialog.</summary>
public sealed record ParameterDialogProps(
    AppServices Services,
    MediaDraft Draft,
    IReadOnlyList<Preset> Presets,
    Action<ConversionOptions, bool> OnSave,
    Action OnClosed);

/// <summary>
/// Per-item parameter dialog (ContentDialog overlay). Presets live INSIDE this
/// dialog (there is no separate Presets page). 基本 shows presets / quality /
/// resolution / output; 高级 adds CRF, bitrate, frame rate, audio sampling,
/// trim, metadata and hardware. Edits commit on 保存; optionally the same options
/// are applied to every task with the same target format.
/// </summary>
public sealed class ParameterDialog : Component<ParameterDialogProps>
{
    public override Element Render()
    {
        var p = Props;
        var (options, setOptions) = UseState(p.Draft.Options);
        var (open, setOpen) = UseState(true);
        var (advanced, setAdvanced) = UseState(false);
        var (applyToSame, setApplyToSame) = UseState(false);

        void Apply(Func<ConversionOptions, ConversionOptions> mutate) => setOptions(mutate(options));

        async Task SavePresetAsync(string name)
        {
            var preset = new Preset
            {
                PresetId = Guid.NewGuid(),
                Name = name,
                BuiltIn = false,
                Template = ToTemplate(options),
            };
            await p.Services.Presets.SaveAsync(preset, CancellationToken.None);
        }

        var filtered = ConversionForm.FilterPresets(p.Presets, options.Mode, options.Container);

        Element Tab(string label, bool active, Action onClick) =>
            (active
                ? Button(label, onClick).AccentButton()
                : Button(label, onClick).SubtleButton())
            .AutomationName(label);

        var body = VStack(Space.Md,
            TextBlock(p.Draft.FileName).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1),
            Ui.Dim("输出格式：" + Fmt.TargetSummary(options)).FontSize(12),
            HStack(Space.Sm, Tab("基本", !advanced, () => setAdvanced(false)), Tab("高级", advanced, () => setAdvanced(true))),
            Component<ConversionForm, ConversionFormProps>(
                new ConversionFormProps(options, false, filtered, Apply, SavePresetAsync, Advanced: advanced)).WithKey("param-form"),
            CheckBox(applyToSame, v => setApplyToSame(v), "同时应用到所有相同目标格式的任务"));

        return DialogChrome.Render(
            "转换参数",
            body,
            "保存",
            "取消",
            result =>
            {
                var final = options;
                var apply = applyToSame;
                setOpen(false);
                DialogChrome.DeferClose(() =>
                {
                    if (result == ContentDialogResult.Primary) p.OnSave(final, apply);
                    p.OnClosed();
                });
            },
            contentWidth: 640,
            maxHeight: 560,
            isOpen: open);
    }

    private static PresetRequestTemplate ToTemplate(ConversionOptions o) => new()
    {
        Mode = o.Mode,
        Container = o.Container,
        Quality = o.Quality,
        Speed = o.Speed,
        ResizeMode = o.ResizeMode,
        AllowUpscale = o.AllowUpscale,
        AudioCodec = o.AudioCodec,
        AudioBitRateKbps = o.AudioBitRateKbps,
        MetadataPolicy = o.MetadataPolicy,
    };
}
