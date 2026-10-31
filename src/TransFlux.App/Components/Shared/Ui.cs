using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;
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

    public static Element SectionHeader(string text) => SubHeading(text);

    public static Element FieldLabel(string text)
        => TextBlock(text).Foreground(Theme.SecondaryText).FontSize(12);

    public static TextBlockElement Dim(string text) => TextBlock(text).Foreground(Theme.SecondaryText);

    public static Element Divider()
        => Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, Space.Sm);

    public static Element Pill(string text, ThemeRef background, ThemeRef foreground)
        => Border(TextBlock(text).Foreground(foreground).FontSize(12))
            .Background(background)
            .CornerRadius(10)
            .Padding(8, 2);

    public static Element IconButton(string symbol, string automationName, string toolTip, Action onClick, bool enabled = true)
        => Button(Icon(SymbolIcon(symbol)), onClick)
            .ToolTip(toolTip)
            .AutomationName(automationName)
            .IsEnabled(enabled);

    public static Element IconTextButton(string symbol, string label, Action onClick, bool enabled = true)
        => Button(HStack(Space.Xs, Icon(SymbolIcon(symbol)), TextBlock(label)), onClick)
            .AutomationName(label)
            .IsEnabled(enabled);

    public static Element KeyValue(string key, string value, double keyWidth = 84)
        => Grid(columns: [GridSize.Px(keyWidth), GridSize.Star()], rows: [GridSize.Auto],
            TextBlock(key).Foreground(Theme.SecondaryText).VerticalAlignment(VerticalAlignment.Top).Grid(column: 0),
            TextBlock(value).TextWrapping(TextWrapping.Wrap).Grid(column: 1));

    public static Element PageHeader(string title, string subtitle, Element? actions = null)
        => Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
            VStack(Space.Xs, Heading(title), Dim(subtitle).TextWrapping(TextWrapping.Wrap)).Grid(column: 0),
            (actions ?? Empty()).VerticalAlignment(VerticalAlignment.Center).Grid(column: 1))
            .Margin(0, 0, 0, Space.Lg);

    public static Element ProgressBar(double fraction)
        => Progress(Math.Clamp(fraction, 0, 0.99) * 100).Height(6);

    public static Element Spacer() => Empty().Flex(grow: 1, basis: 0);

    public static Element StatusDot(ThemeRef color) => Border(Empty()).Width(8).Height(8).CornerRadius(4).Background(color);

    /// <summary>Attaches a single keyboard accelerator to a button (idempotent across re-renders).</summary>
    public static ButtonElement WithAccelerator(ButtonElement button, VirtualKey key, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None)
        => button.Set(b =>
        {
            if (b.KeyboardAccelerators.Any(a => a.Key == key && a.Modifiers == modifiers)) return;
            b.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = key, Modifiers = modifiers });
        });

    public static ThemeRef StateColor(TransFlux.Domain.Jobs.JobState state) => state switch
    {
        TransFlux.Domain.Jobs.JobState.Succeeded => Theme.SystemSuccess,
        TransFlux.Domain.Jobs.JobState.Failed => Theme.SystemCritical,
        TransFlux.Domain.Jobs.JobState.Canceled => Theme.SystemNeutral,
        TransFlux.Domain.Jobs.JobState.Interrupted => Theme.SystemCaution,
        TransFlux.Domain.Jobs.JobState.NeedsAttention => Theme.SystemCaution,
        _ => Theme.SystemAttention,
    };
}
