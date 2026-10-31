using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;

namespace TransFlux.App.Components.Workbench;

/// <summary>
/// Left target-format catalogue (Format-Factory style), grouped 视频 / 音频 / 图片.
/// Clicking a format highlights it and selects it as the target for new files.
/// Image formats are shown but disabled with an explanatory tooltip (docs/03).
/// </summary>
public static class FormatCatalog
{
    public static Element Render(string selectedKey, Action<string> onSelect, Action onAddFiles, Action onAddFolder)
    {
        var children = new List<Element>
        {
            TextBlock(UiText.TargetFormat).SemiBold().FontSize(16),
            Ui.Dim(UiText.TargetFormatHint).FontSize(12).TextWrapping(TextWrapping.Wrap),
        };

        foreach (var group in new[] { TargetFormatGroup.Video, TargetFormatGroup.Audio, TargetFormatGroup.Image })
        {
            var items = TargetFormats.ForGroup(group);
            if (items.Count == 0) continue;

            var entries = items.Select(f =>
            {
                var selected = f.Key == selectedKey && f.Enabled;
                var title = TextBlock(f.Title).SemiBold().Foreground(f.Enabled ? Theme.PrimaryText : Theme.DisabledText);
                var detail = TextBlock(f.Detail).FontSize(12).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap);
                var row = Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
                    VStack(2, title, detail).Grid(column: 0),
                    (selected ? TextBlock("✓").Foreground(Theme.Accent).SemiBold().VAlign(VerticalAlignment.Center) : Empty()).Grid(column: 1));

                BorderElement entry = Border(row)
                    .Padding(Space.Md, Space.Sm)
                    .CornerRadius(6)
                    .Background(selected ? Theme.SubtleFill : Theme.CardBackground)
                    .WithBorder(selected ? Theme.Accent : Theme.CardStroke, 1)
                    .IsTabStop(true)
                    .OnTapped((_, _) => { if (f.Enabled) onSelect(f.Key); })
                    .AutomationName("目标格式 " + f.Title);
                if (!f.Enabled)
                    entry = entry.ToolTip(f.DisabledReason ?? "暂不可用");
                return entry.WithKey(f.Key);
            }).ToArray();

            var groupChildren = new List<Element>
            {
                TextBlock(TargetFormats.GroupTitle(group)).Foreground(Theme.SecondaryText).FontSize(12).Margin(0, Space.Sm, 0, 0),
            };
            groupChildren.AddRange(entries);
            children.Add(VStack(Space.Xs, groupChildren.ToArray()));
        }

        children.Add(Ui.Divider());
        children.Add(Button(UiText.AddFiles, onAddFiles).AccentButton().HAlign(HorizontalAlignment.Stretch).AutomationName(UiText.AddFiles));
        children.Add(Button(UiText.AddFolder, onAddFolder).HAlign(HorizontalAlignment.Stretch).AutomationName(UiText.AddFolder));

        return Ui.Card(VStack(Space.Md, children.ToArray()));
    }
}
