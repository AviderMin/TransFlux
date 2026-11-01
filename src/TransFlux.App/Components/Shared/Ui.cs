using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.State;
using TransFlux.App.Theming;

namespace TransFlux.App.Components.Shared;

/// <summary>Small pure render helpers reused across pages (no hooks, no I/O).</summary>
public static class Ui
{
    public static Element Card(Element child, double padding = Space.Lg)
        => Border(child)
            .Background(Theme.CardBackground)
            .CornerRadius(8)
            .WithBorder(Theme.CardStroke, 1)
            .Padding(padding);

    public static Element FieldLabel(string text)
        => Caption(text).Foreground(Theme.SecondaryText);

    public static TextBlockElement Dim(string text) => TextBlock(text).Foreground(Theme.SecondaryText);

    /// <summary>Secondary caption copy: type-ramp Caption (12px), never an arbitrary font size.</summary>
    public static TextBlockElement DimCaption(string text) => Caption(text).Foreground(Theme.SecondaryText);

    public static Element Divider()
        => Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, Space.Sm);

    public static Element Pill(string text, ThemeRef background, ThemeRef foreground)
        => Border(Caption(text).Foreground(foreground))
            .Background(background)
            .CornerRadius(10)
            .Padding(8, 2);

    /// <summary>Icon-only button on a Segoe Fluent Icons glyph; name and tooltip are mandatory.</summary>
    public static ButtonElement IconButton(string glyph, string automationName, string toolTip, Action onClick, bool enabled = true)
        => Button(Icon(FontIcon(glyph, fontSize: 14)), onClick)
            .ToolTip(toolTip)
            .AutomationName(automationName)
            .IsEnabled(enabled);

    /// <summary>Icon + label button on a Segoe Fluent Icons glyph.</summary>
    public static ButtonElement IconTextButton(string glyph, string label, Action onClick, bool enabled = true)
        => Button(HStack(Space.Xs, Icon(FontIcon(glyph, fontSize: 16)), TextBlock(label)), onClick)
            .AutomationName(label)
            .IsEnabled(enabled);

    public static Element KeyValue(string key, string value, double keyWidth = 84)
        => Grid(columns: [GridSize.Px(keyWidth), GridSize.Star()], rows: [GridSize.Auto],
            TextBlock(key).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap).VerticalAlignment(VerticalAlignment.Top).Grid(column: 0),
            TextBlock(value).TextWrapping(TextWrapping.Wrap).Grid(column: 1));

    /// <summary>One banner shape for all four notification severities (success green, error red).</summary>
    public static Element NoticeBar(Notice notice, Action? onClosed = null)
    {
        var bar = InfoBar(notice.Title, notice.Message).IsClosable(onClosed is not null);
        bar = notice.Level switch
        {
            NoticeLevel.Success => bar.Success(),
            NoticeLevel.Warning => bar.Warning(),
            NoticeLevel.Error => bar.Error(),
            _ => bar.Informational(),
        };
        return onClosed is null ? bar : bar.Closed(onClosed);
    }

    /// <summary>Theme brush pair for one semantic status tone (single mapping for every list).</summary>
    public static (ThemeRef Background, ThemeRef Foreground) ToneColors(StatusTone tone) => tone switch
    {
        StatusTone.Success => (Theme.SystemSuccessBackground, Theme.SystemSuccess),
        StatusTone.Critical => (Theme.SystemCriticalBackground, Theme.SystemCritical),
        StatusTone.Caution => (Theme.SystemCautionBackground, Theme.SystemCaution),
        StatusTone.Neutral => (Theme.SystemNeutralBackground, Theme.SystemNeutral),
        _ => (Theme.SystemAttentionBackground, Theme.SystemAttention),
    };

    /// <summary>Attaches a single keyboard accelerator to a button (idempotent across re-renders).</summary>
    public static ButtonElement WithAccelerator(ButtonElement button, VirtualKey key, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
        => button.Set(b =>
        {
            if (b.KeyboardAccelerators.Any(a => a.Key == key && a.Modifiers == modifiers)) return;
            b.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = key, Modifiers = modifiers });
        });
}
