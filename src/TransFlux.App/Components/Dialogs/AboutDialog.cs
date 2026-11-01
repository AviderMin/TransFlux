using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Resources;
using TransFlux.App.Theming;

namespace TransFlux.App.Components.Dialogs;

/// <summary>Props for the about dialog.</summary>
public sealed record AboutDialogProps(Action OnClosed);

/// <summary>
/// About dialog (top-bar 关于): app icon, name, assembly version (1.0.0.2),
/// MIT + FFmpeg(GPL) notice, and Close.
/// </summary>
public sealed class AboutDialog : Component<AboutDialogProps>
{
    public override Element Render()
    {
        var (open, setOpen) = UseState(true);
        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(4) ?? "1.0.0.2";
        var iconUri = SafeIconUri();

        Element icon = iconUri is not null
            ? Image(iconUri).Width(64).Height(64).HAlign(HorizontalAlignment.Left).AutomationName(UiText.AppTitle)
            : Border(Icon(SymbolIcon("Video"))).Width(64).Height(64).CornerRadius(12)
                .Background(Theme.ControlFillSecondary).HAlign(HorizontalAlignment.Left);

        var body = VStack(Space.Lg,
            icon,
            VStack(Space.Xs,
                Subtitle(UiText.AppTitle),
                Ui.DimCaption("版本 " + version)),
            Ui.Card(Body("TransFlux 是一个本地视频与音频转换工具，基于 WinUI 3 与 Reactor 构建，所有转换都在本机完成。")
                .TextWrapping(TextWrapping.Wrap)),
            Ui.Card(VStack(Space.Xs,
                TextBlock("开源许可").SemiBold(),
                Ui.DimCaption("MIT License"),
                Ui.DimCaption("内置 FFmpeg（GPL 许可）。FFmpeg 作为独立组件分发，其许可不改变本应用的 MIT 授权。")
                    .TextWrapping(TextWrapping.Wrap))));

        return DialogChrome.Render(
            UiText.AboutTitle,
            body,
            "关闭",
            null,
            _ => { setOpen(false); DialogChrome.DeferClose(Props.OnClosed); },
            contentWidth: 520,
            maxHeight: 480,
            isOpen: open);
    }

    private static string? SafeIconUri()
    {
        try
        {
            // Pure path computation (no I/O): the icon ships next to the app.
            return new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")).AbsoluteUri;
        }
        catch
        {
            return null;
        }
    }
}