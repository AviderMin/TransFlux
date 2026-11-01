using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;
using TransFlux.App.Theming;
using static Microsoft.UI.Reactor.Factories;

namespace TransFlux.App.Components.Dialogs;

/// <summary>
/// Shared ContentDialog chrome: a capped, scrollable body so long grouped forms
/// stay reachable in a typical window. The caller mounts the returned element
/// only while the dialog is open (keyed by a request id), never as a closed child.
/// </summary>
public static class DialogChrome
{
    private sealed class PrimaryAction
    {
        public Func<Task<bool>>? Invoke { get; set; }
        public bool Busy { get; set; }
    }
    private static readonly ConditionalWeakTable<ContentDialog, PrimaryAction> PrimaryActions = new();
    private static readonly ConditionalWeakTable<ContentDialog, PrimaryAction> SecondaryActions = new();
    public static ContentDialogElement Render(
        string title,
        Element body,
        string primaryText,
        string? secondaryText,
        Action<ContentDialogResult> onClosed,
        double contentWidth = 620,
        double maxHeight = 520,
        bool isOpen = true,
        Func<Task<bool>>? beforePrimaryClose = null,
        bool primaryEnabled = true,
        Func<Task<bool>>? beforeSecondaryClose = null)
    {
        var scroll = ScrollViewer(Border(body).Padding(Space.ScrollBarGutter, 0, Space.ScrollBarGutter, Space.Xl))
            .Width(contentWidth)
            .MaxHeight(maxHeight)
            .HorizontalScrollMode(ScrollMode.Disabled)
            .VerticalScrollMode(ScrollMode.Auto)
            .Set(viewer =>
            {
                viewer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            });

        var dialog = ContentDialog(title, scroll, primaryButtonText: primaryText) with
        {
            IsOpen = isOpen,
            SecondaryButtonText = secondaryText ?? string.Empty,
            CloseButtonText = string.Empty,
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = onClosed,
        };

        // Allow the grouped form to use the requested width instead of the
        // ContentDialog default (~548). Indexer assignment overwrites safely.
        return dialog.Set(control =>
        {
            control.Resources["ContentDialogMaxWidth"] = contentWidth + 120;
            control.IsPrimaryButtonEnabled = primaryEnabled;
            var action = PrimaryActions.GetValue(control, c =>
            {
                var holder = new PrimaryAction();
                c.PrimaryButtonClick += async (_, args) =>
                {
                    if (holder.Invoke is null) return;
                    args.Cancel = true;
                    if (holder.Busy) return;
                    var deferral = args.GetDeferral();
                    holder.Busy = true;
                    try { args.Cancel = !await holder.Invoke(); }
                    finally { holder.Busy = false; deferral.Complete(); }
                };
                return holder;
            });
            action.Invoke = beforePrimaryClose;
            SecondaryActions.GetValue(control, c =>
            {
                var holder = new PrimaryAction();
                c.SecondaryButtonClick += async (_, args) =>
                {
                    if (holder.Invoke is null) return;
                    args.Cancel = true;
                    if (holder.Busy) return;
                    var deferral = args.GetDeferral();
                    holder.Busy = true;
                    try { args.Cancel = !await holder.Invoke(); }
                    finally { holder.Busy = false; deferral.Complete(); }
                };
                return holder;
            }).Invoke = beforeSecondaryClose;
        });
    }

    /// <summary>
    /// Runs <paramref name="clear"/> on the next dispatcher turn. Clearing inside the
    /// ContentDialog's own Closed callback unmounts it while WinUI is still tearing it
    /// down, which fails fast on WinUI 3.
    /// </summary>
    public static void DeferClose(Action clear)
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            clear();
            return;
        }
        queue.TryEnqueue(() => clear());
    }
}
