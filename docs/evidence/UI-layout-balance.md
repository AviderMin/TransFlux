# 主界面比例与编解码器诊断布局审查

2026-11-01，基于当前工作区 WinUI 3 / Microsoft.UI.Reactor 0.1.0-preview.16。

## 审查发现及修复

- 主内容原为 `* + 320 DIP`，放大窗口只增加任务区宽度，输出设置长期拥挤。改为类型化 Grid `3* : 2*`，默认窗口右侧约 490 DIP，随窗口同时扩展。页边距、列间距仍遵循 Space；保留设置标题、独立滚动正文和固定保存位置，不改选择/批量编辑语义。
- 编解码器目录原为纵向多个 Expander，展开数百条会把后续分组推远。按用户确认，“视频 / 音频 / 字幕”改为三个等宽并排 Expander；每组独立展开、独立 320 DIP 滚动视口，LazyVStack 按需构建条目。其他类型有内容时另起一行。
- 三列内条目改为名称在上、标记在下，名称和标记均可换行。统计和图例采用可换行 FlexRow，去除任意 13px 字号，使用 BodyStrong / Caption；展开器提供明确无障碍名称。
- “硬编可用”必须同时满足 Available 和 TrialCompleted，防止只读核心目录或未试跑候选被误认为本机验证通过。目录展示核心能力，不等同工作台开放的全部输出格式。
- 删除已无调用的固定面板最小/最大宽度常量，现有布局常量测试移除旧 320 DIP 断言。已有未提交媒体管线和 UX 修改保留。

## 规范依据

使用当前依赖对应的 [Reactor preview.16 Layout](https://microsoft.github.io/microsoft-ui-reactor/0.1.0-preview.16/layout/)：Grid 明确分配列，类型化 Star 权重，Expander 组织可折叠内容，ScrollViewer 处理溢出，使用命名字阶和主题资源。3:2、三列及 320 DIP 视口是本项目的产品布局选择，不是微软强制比例。

## 验证

- `dotnet build TransFlux.slnx -c Debug -p:WindowsPackageType=None`：0 警告 / 0 错误。
- `dotnet test TransFlux.slnx -c Debug -p:WindowsPackageType=None --no-build`：366 项通过（Domain 120、Application 96、Infrastructure 32、Engine 118）。
- 使用 computer-use 技能操作隔离 Debug 数据目录 `artifacts/ui-balance/state`；默认尺寸主界面、三个分组同时展开与独立滚动均已实际检查，截图 `artifacts/ui-balance/codecs-three-columns.png`。
- 本次不覆盖当前已安装版本；未执行 Narrator、全部 DPI 或主题组合验收，不据此宣称全部无障碍验收完成。
- 自动化无法可靠定位 PickerHost 的路径输入窗口，导入样片检查未完成；拖动边界也未稳定改变窗口尺寸，因此本轮不将最小窗口实测记为通过。默认尺寸和诊断三列展开有实际截图证据。
