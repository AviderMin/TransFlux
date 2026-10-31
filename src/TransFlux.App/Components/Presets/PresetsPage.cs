using System.Linq;
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
using TransFlux.Domain.Plans;

namespace TransFlux.App.Components.Presets;

public sealed record PresetsPageProps(AppServices Services);

/// <summary>预设 page: built-in and user presets, save-as and delete (docs/03 PresetEditor).</summary>
public sealed class PresetsPage : Component<PresetsPageProps>
{
    public override Element Render()
    {
        var services = Props.Services;
        var (presets, setPresets) = UseState<IReadOnlyList<Preset>>(Array.Empty<Preset>());
        var (reload, setReload) = UseState(0);
        var (notice, setNotice) = UseState<string?>(null);
        var (editing, setEditing) = UseState<EditPresetModel?>(null);
        var (deleteTarget, setDeleteTarget) = UseState<Preset?>(null);

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try { setPresets(await services.Presets.GetAllAsync(cts.Token)); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { setNotice("读取预设失败：" + ex.Message); }
            });
            return () => cts.Cancel();
        }, reload, services);

        void OpenSaveAs(Preset source)
        {
            setEditing(new EditPresetModel(null, source.Name + " 副本", source.Template, source.PresetId));
        }

        void OpenNew()
        {
            var basePreset = presets.FirstOrDefault(p => p.BuiltIn) ?? presets.FirstOrDefault();
            setEditing(new EditPresetModel(null, "我的预设", basePreset?.Template ?? new PresetRequestTemplate(), basePreset?.PresetId ?? Guid.Empty));
        }

        async Task SaveEditingAsync(EditPresetModel model)
        {
            setEditing(null);
            try
            {
                var preset = new Preset
                {
                    PresetId = model.PresetId ?? Guid.NewGuid(),
                    Name = string.IsNullOrWhiteSpace(model.Name) ? "未命名预设" : model.Name.Trim(),
                    BuiltIn = false,
                    Template = model.Template,
                };
                await services.Presets.SaveAsync(preset, CancellationToken.None);
                setNotice($"已保存预设“{preset.Name}”。");
                setReload(reload + 1);
            }
            catch (Exception ex) { setNotice("保存失败：" + ex.Message); }
        }

        async Task DeleteAsync(Preset preset)
        {
            setDeleteTarget(null);
            try { await services.Presets.DeleteAsync(preset.PresetId, CancellationToken.None); setNotice($"已删除“{preset.Name}”。"); setReload(reload + 1); }
            catch (Exception ex) { setNotice("删除失败：" + ex.Message); }
        }

        Element Card(Preset preset)
        {
            var actions = new List<Element> { Button(UiText.SaveAs, () => OpenSaveAs(preset)).AutomationName($"另存为 {preset.Name}") };
            if (!preset.BuiltIn) actions.Add(Button(UiText.Delete, () => setDeleteTarget(preset)).AutomationName($"删除 {preset.Name}"));
            return Ui.Card(HStack(Space.Md,
                VStack(2,
                    HStack(Space.Sm, TextBlock(preset.Name).SemiBold(),
                        preset.BuiltIn ? Ui.Pill(UiText.BuiltIn, Theme.ControlFillSecondary, Theme.SecondaryText) : Ui.Pill(UiText.Custom, Theme.SystemAttentionBackground, Theme.SystemAttention)),
                    Ui.Dim(DescribeTemplate(preset.Template))).Flex(grow: 1, basis: 0),
                HStack(Space.Xs, actions.ToArray())))
                .WithKey(preset.PresetId.ToString());
        }

        var builtIns = presets.Where(p => p.BuiltIn).ToList();
        var customs = presets.Where(p => !p.BuiltIn).ToList();

        var header = HStack(Space.Sm, Heading(UiText.PresetsTitle), Ui.Dim("内置预设不可直接覆盖，可另存为自建预设。"), Ui.Spacer(),
            Button("新建预设", OpenNew).AccentButton().AutomationName("新建预设"));

        var noticeBar = notice is { } msg ? InfoBar("提示", msg).Informational().IsClosable(true).Closed(() => setNotice(null)).Margin(0, 0, 0, Space.Sm) : null;

        var editDialog = ContentDialog("保存预设",
                VStack(Space.Sm,
                    TextBox(editing?.Name ?? string.Empty, n => { if (editing is not null) setEditing(editing with { Name = n }); }, placeholderText: "预设名称", header: "名称"),
                    Ui.Dim(editing is null ? string.Empty : "基于：" + DescribeTemplate(editing.Template))),
                primaryButtonText: "保存") with
        {
            IsOpen = editing is not null,
            SecondaryButtonText = "取消",
            OnClosed = result =>
            {
                var model = editing;
                setEditing(null);
                if (result == ContentDialogResult.Primary && model is not null) _ = SaveEditingAsync(model);
            },
        };

        var deleteDialog = ContentDialog("删除预设", TextBlock(deleteTarget is null ? string.Empty : $"确定删除“{deleteTarget.Name}”？"), primaryButtonText: "删除") with
        {
            IsOpen = deleteTarget is not null,
            SecondaryButtonText = "取消",
            OnClosed = result =>
            {
                var target = deleteTarget;
                setDeleteTarget(null);
                if (result == ContentDialogResult.Primary && target is not null) _ = DeleteAsync(target);
            },
        };

        var builtInChildren = new List<Element> { TextBlock(UiText.BuiltIn).SemiBold() };
        builtInChildren.AddRange(builtIns.Select(Card));
        var customChildren = new List<Element> { TextBlock(UiText.Custom).SemiBold() };
        if (customs.Count == 0) customChildren.Add(Ui.Dim("还没有自建预设，可从任一预设“另存为”。"));
        customChildren.AddRange(customs.Select(Card));

        return FlexColumn(noticeBar, header,
            ScrollView(VStack(Space.Lg,
                VStack(Space.Sm, builtInChildren.ToArray()),
                VStack(Space.Sm, customChildren.ToArray()))
                .Flex(grow: 1, basis: 0)),
            editDialog, deleteDialog).Set(f => f.AlignItems = Microsoft.UI.Reactor.Layout.FlexAlign.Stretch).FlexPadding(Space.PagePadding);
    }

    private static string DescribeTemplate(PresetRequestTemplate t)
    {
        var mode = t.Mode switch
        {
            ConversionMode.AudioExtract => "提取音频",
            ConversionMode.Remux => "只换封装",
            _ => "视频转码",
        };
        var quality = t.Quality switch
        {
            QualityTier.SmallerSize => "较小体积",
            QualityTier.HigherQuality => "更高画质",
            _ => "均衡",
        };
        return $"{mode} · {t.Container.DisplayName()} · {quality}";
    }

    private sealed record EditPresetModel(Guid? PresetId, string Name, PresetRequestTemplate Template, Guid BasePresetId);
}