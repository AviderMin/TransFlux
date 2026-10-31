using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.Theming;

namespace TransFlux.App.Components.Convert;

/// <summary>Empty drag-drop import state (docs/03 EmptyImport). The caller wraps
/// this content in a border that owns the actual OS drop handlers.</summary>
public static class EmptyImport
{
    public static Element Render(Action onAddFiles, Action onAddFolder, bool importing)
    {
        return VStack(Space.Md,
            Icon(SymbolIcon("Add")).HorizontalAlignment(HorizontalAlignment.Center),
            TextBlock(UiText.EmptyTitle).SemiBold().FontSize(16).HorizontalAlignment(HorizontalAlignment.Center),
            TextBlock(UiText.EmptyHint).Foreground(Theme.SecondaryText).FontSize(12)
                .TextWrapping(TextWrapping.Wrap).HorizontalAlignment(HorizontalAlignment.Center).MaxWidth(420),
            HStack(Space.Sm,
                Button(UiText.AddFiles, onAddFiles).AccentButton().AutomationName(UiText.AddFiles).ToolTip("Ctrl+O"),
                Button(UiText.AddFolder, onAddFolder).AutomationName(UiText.AddFolder).ToolTip("Ctrl+Shift+O"))
                .HorizontalAlignment(HorizontalAlignment.Center),
            importing
                ? HStack(Space.Sm, ProgressRing().Width(16).Height(16), Ui.Dim("正在读取媒体信息…"))
                    .HorizontalAlignment(HorizontalAlignment.Center)
                : Ui.Dim(UiText.EmptySecondary).HorizontalAlignment(HorizontalAlignment.Center));
    }
}
