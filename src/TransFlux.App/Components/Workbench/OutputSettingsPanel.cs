using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Domain.Plans;

namespace TransFlux.App.Components.Workbench;

/// <summary>Props for the left "输出设置" panel.</summary>
public sealed record OutputSettingsProps(
    string SelectedKey,
    QualityTier Quality,
    int SelectedCount,
    string OutputDirectory,
    Action<string> OnSelectFormat,
    Action<QualityTier> OnQuality,
    Action OnApplyToSelected,
    Action OnPickOutput,
    Action OnOpenOutput);

/// <summary>
/// Left settings panel (Format-Factory style): target-format catalogue grouped
/// 视频 / 音频, a one-click quality tier, and the persistent output location.
/// Format changes apply to newly added files; the panel offers an explicit
/// "应用到已选" action so it never silently rewrites queued tasks.
/// </summary>
public static class OutputSettingsPanel
{
    public static Element Render(OutputSettingsProps p)
    {
        var header = VStack(Space.Xs,
            TextBlock(UiText.TargetFormat).SemiBold().FontSize(16),
            Ui.Dim(UiText.TargetFormatHint).FontSize(12).TextWrapping(TextWrapping.Wrap));

        var formatChildren = new List<Element>();
        foreach (var group in new[] { TargetFormatGroup.Video, TargetFormatGroup.Audio })
        {
            formatChildren.Add(TextBlock(TargetFormats.GroupTitle(group)).Foreground(Theme.SecondaryText)
                .FontSize(12).Margin(0, Space.Sm, 0, 0));
            foreach (var f in TargetFormats.ForGroup(group))
            {
                formatChildren.Add(FormatRow(f, p.SelectedKey, p.OnSelectFormat));
            }
        }
        formatChildren.Add(TextBlock("图片").Foreground(Theme.SecondaryText).FontSize(12).Margin(0, Space.Sm, 0, 0));
        formatChildren.Add(Ui.Dim("PNG / JPEG · 即将支持").FontSize(12));

        var formatScroll = ScrollViewer(VStack(Space.Xs, formatChildren.ToArray()))
            .VerticalScrollMode(ScrollMode.Auto)
            .HorizontalScrollMode(ScrollMode.Disabled)
            .Set(s => { s.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; s.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; });

        var footer = new List<Element>
        {
            Ui.Divider(),
            Ui.FieldLabel("质量"),
            HStack(Space.Xs,
                QualityChip("较小体积", QualityTier.SmallerSize, p),
                QualityChip("均衡", QualityTier.Balanced, p),
                QualityChip("更高画质", QualityTier.HigherQuality, p)),
        };
        if (p.SelectedCount > 0)
        {
            footer.Add(Button($"应用到已选 {p.SelectedCount} 项", p.OnApplyToSelected)
                .SubtleButton().HAlign(HorizontalAlignment.Stretch).AutomationName("应用质量到已选项"));
        }
        footer.Add(Ui.Divider());
        footer.Add(Ui.FieldLabel("输出位置"));
        footer.Add(TextBlock(p.OutputDirectory).FontSize(12).Foreground(Theme.SecondaryText)
            .TextWrapping(TextWrapping.Wrap).MaxLines(3).ToolTip(p.OutputDirectory));
        footer.Add(HStack(Space.Xs,
            Button("更改…", p.OnPickOutput).AutomationName("更改输出位置"),
            Button("打开", p.OnOpenOutput).AutomationName("打开输出位置")));

        return Ui.Card(Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            header.Grid(row: 0),
            formatScroll.Grid(row: 1).Margin(0, Space.Sm, 0, 0),
            VStack(Space.Sm, footer.ToArray()).Grid(row: 2)));

    }

    private static Element QualityChip(string label, QualityTier tier, OutputSettingsProps p)
        => (p.Quality == tier
                ? Button(label, () => p.OnQuality(tier)).AccentButton()
                : Button(label, () => p.OnQuality(tier)).SubtleButton())
            .AutomationName("质量 " + label);

    private static Element FormatRow(TargetFormat f, string selectedKey, Action<string> onSelect)
    {
        var selected = f.Key == selectedKey;
        var title = TextBlock(f.Title).SemiBold().Foreground(Theme.PrimaryText);
        var detail = TextBlock(f.Detail).FontSize(12).Foreground(Theme.SecondaryText)
            .TextWrapping(TextWrapping.Wrap);
        var row = Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
            VStack(2, title, detail).Grid(column: 0),
            (selected ? TextBlock("✓").Foreground(Theme.Accent).SemiBold()
                .VAlign(VerticalAlignment.Center) : Empty()).Grid(column: 1));

        return Border(row)
            .Padding(Space.Md, Space.Sm)
            .CornerRadius(6)
            .Background(selected ? Theme.SubtleFill : Theme.CardBackground)
            .WithBorder(selected ? Theme.Accent : Theme.CardStroke, 1)
            .IsTabStop(true)
            .OnTapped((_, _) => onSelect(f.Key))
            .AutomationName("目标格式 " + f.Title)
            .WithKey(f.Key);
    }
}