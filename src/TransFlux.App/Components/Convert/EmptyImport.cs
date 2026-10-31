using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Windows.System;
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
        return VStack(Space.Lg,
            Border(Icon(SymbolIcon("Video")).HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Center))
                .Width(56).Height(56).CornerRadius(16).Background(Theme.ControlFillSecondary).HorizontalAlignment(HorizontalAlignment.Center),
            VStack(Space.Sm,
                TextBlock("添加文件，开始转换").SemiBold().FontSize(20).HorizontalAlignment(HorizontalAlignment.Center),
                TextBlock("将视频或音频拖放到这里，或选择本地文件。")
                    .Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap).TextAlignment(TextAlignment.Center)),
            HStack(Space.Sm,
                Ui.WithAccelerator((ButtonElement)Button(UiText.AddFiles, onAddFiles).AccentButton().IsEnabled(!importing), VirtualKey.O, VirtualKeyModifiers.Control)
                    .AutomationName(UiText.AddFiles).ToolTip("Ctrl+O"),
                Ui.WithAccelerator((ButtonElement)Button(UiText.AddFolder, onAddFolder).IsEnabled(!importing), VirtualKey.O, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)
                    .AutomationName(UiText.AddFolder).ToolTip("Ctrl+Shift+O"))
                .HorizontalAlignment(HorizontalAlignment.Center),
            importing
                ? HStack(Space.Sm, ProgressRing().Width(16).Height(16), Ui.Dim("正在读取媒体信息…"))
                    .HorizontalAlignment(HorizontalAlignment.Center)
                : Ui.Dim("MP4 · MKV · MOV · M4A · FLAC · WAV").FontSize(12).HorizontalAlignment(HorizontalAlignment.Center))
            .VerticalAlignment(VerticalAlignment.Center);
    }
}
