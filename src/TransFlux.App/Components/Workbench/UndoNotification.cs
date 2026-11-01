using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
using TransFlux.App.Theming;

namespace TransFlux.App.Components.Workbench;

public sealed record UndoNotificationProps(int Count, Action Undo, Action Dismiss);

/// <summary>WinUI InfoBar for the most recent list removal; the message and action are explicit so
/// the operation is discoverable without relying on a transient custom banner.</summary>
public sealed class UndoNotification : Component<UndoNotificationProps>
{
    public override Element Render()
    {
        var p = Props;
        var message = p.Count == 1
            ? "已移除 1 个项目；源文件和输出文件均未删除。"
            : $"已移除 {p.Count} 个项目；源文件和输出文件均未删除。";
        return InfoBar("已移除项目", message)
            .Informational()
            .IsClosable(true)
            .Closed(p.Dismiss)
            .Content(HStack(Space.Sm,
                Ui.Dim("需要恢复列表吗？").FontSize(12).VAlign(VerticalAlignment.Center),
                Button("撤销移除", p.Undo).SubtleButton().AutomationName("撤销移除")
                    .MinWidth(0).MinHeight(24).Padding(8, 0).VAlign(VerticalAlignment.Center)
                    .Set(b => b.HorizontalContentAlignment = HorizontalAlignment.Left))
                .VAlign(VerticalAlignment.Center))
            .AutomationName("撤销移除提醒");
    }
}
