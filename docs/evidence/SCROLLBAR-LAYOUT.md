# 滚动条遮挡修复

2026-11-01。WinUI 的原生滚动条会覆盖视口边缘，展开时占用内容区域；[微软滚动控件文档](https://learn.microsoft.com/windows/apps/develop/ui/controls/scroll-controls) 建议预留 16px 边缘空间。此前部分区域没有统一避让，不能仅靠自动隐藏解决。

- 按用户后续要求，Space.ScrollBarGutter 调整为 **12 DIP**；输出设置正文、主任务条目、编解码器条目及对话框正文在左右两侧分别预留相同宽度，保持内容居中。取代此前仅右侧 16 DIP 的实现。
- 主任务虚拟列表与编解码器虚拟列表逐条预留，保留原生滚动宿主和虚拟化。
- 共用 DialogChrome 使用同一对称间距常量；设置、历史及技术详情沿用它，底部间距不变。
- 修正此前编解码器目录的重复包装：LazyVStack 自带 ScrollViewer，删除外层 ScrollViewer，保留 320 DIP 高度。参考 [Reactor LazyStack 实现](https://github.com/microsoft/microsoft-ui-reactor/blob/main/src/Reactor/Core/V1Protocol/Handlers/LazyStackHandler.cs)。
- 不修改系统滚动条设置，不隐藏滚动条，不替换系统模板。

Debug 解决方案构建 0 警告/0 错误，96 项应用测试通过。本次为源码修复，未重新生成安装包。
