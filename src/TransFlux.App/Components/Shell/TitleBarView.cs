using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Resources;
using TransFlux.App.Theming;

namespace TransFlux.App.Components.Shell;

/// <summary>Props for the custom title bar.</summary>
public sealed record TitleBarProps(Action OpenSettings, Action ShowAbout, Action? OpenHistory = null);

/// <summary>
/// Custom title bar (SurgeLink TitleBar.RightHeader pattern): app icon + name on
/// the left, 设置 / 关于 icon buttons on the right. The window enables
/// ExtendsContentIntoTitleBar; the buttons opt out of the drag region so they
/// stay clickable. No NavigationView.
/// </summary>
public static class TitleBarView
{
    public static Element Render(TitleBarProps props)
    {
        var right = HStack(Space.Sm,
            props.OpenHistory is null ? null : TitleIconButton("\uE81C", "历史记录", "历史记录", props.OpenHistory),
            TitleIconButton("\uE713", UiText.SettingsTitle, "设置", props.OpenSettings),
            TitleIconButton("\uE946", UiText.AboutTitle, "关于", props.ShowAbout))
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, Space.Sm, 0);

        var bar = TitleBar(UiText.AppTitle)
            .Subtitle("格式转换工作台")
            .ToolTip(UiText.AppTitle)
            .RightHeader(right);

        var iconUri = SafeIconUri();
        if (iconUri is not null) bar = bar.Icon(iconUri);
        return bar;
    }

    private static Element TitleIconButton(string glyph, string automationName, string toolTip, Action onClick)
        => Button(Icon(FontIcon(glyph, fontSize: 14)), onClick)
            .SubtleButton()
            .Width(32).Height(32).MinWidth(32).MinHeight(32)
            .Padding(0)
            .Set(button =>
            {
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
                button.VerticalContentAlignment = VerticalAlignment.Center;
            })
            .ToolTip(toolTip)
            .AutomationName(automationName)
            .IsDragRegion(false);

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
