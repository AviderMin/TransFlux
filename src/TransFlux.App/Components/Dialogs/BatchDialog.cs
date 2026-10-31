using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Presets;

namespace TransFlux.App.Components.Dialogs;

/// <summary>Props for the batch settings dialog.</summary>
public sealed record BatchDialogProps(
    AppServices Services,
    IReadOnlyList<Guid> TargetIds,
    ConversionOptions Current,
    bool Mixed,
    IReadOnlyList<Preset> Presets,
    Action<IReadOnlyList<Guid>, Func<ConversionOptions, ConversionOptions>> Update,
    Action OnClosed);

/// <summary>
/// Batch settings dialog: the same grouped form applied to every selected media
/// item through WorkbenchReducer UpdateOptions. Title is 批处理设置（N 项）.
/// </summary>
public sealed class BatchDialog : Component<BatchDialogProps>
{
    public override Element Render()
    {
        var p = Props;
        var (open, setOpen) = UseState(true);

        void Apply(Func<ConversionOptions, ConversionOptions> mutate) => p.Update(p.TargetIds, mutate);

        async Task SavePresetAsync(string name)
        {
            var preset = new Preset
            {
                PresetId = Guid.NewGuid(),
                Name = name,
                BuiltIn = false,
                Template = ToTemplate(p.Current),
            };
            await p.Services.Presets.SaveAsync(preset, CancellationToken.None);
        }

        var filtered = ConversionForm.FilterPresets(p.Presets, p.Current.Mode, p.Current.Container);
        var body = VStack(Space.Md,
            Ui.Dim(p.Mixed ? UiText.DirtySelectionHint : "修改会应用到所有选中的文件。").FontSize(12).TextWrapping(TextWrapping.Wrap),
            Component<ConversionForm, ConversionFormProps>(
                new ConversionFormProps(p.Current, p.Mixed, filtered, Apply, SavePresetAsync)).WithKey("batch-form"));

        return DialogChrome.Render(
            string.Format(UiText.BatchTitleFormat, p.TargetIds.Count),
            body,
            "完成",
            null,
            _ => { setOpen(false); DialogChrome.DeferClose(p.OnClosed); },
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
