# UI/UX 缺陷修复轮 · 2026-11-01（task-1）

本记录对应 ui-reviewer 审查报告（3 Major + 17 Minor + 7 Nit）的全量修复。写入范围：`src/TransFlux.App/**`、`tests/TransFlux.Application.Tests/**`、`docs/evidence/**`；未触碰 `src/TransFlux.Engine.FFmpeg` 与 `src/TransFlux.Domain`。

## 修复对照清单

| 编号 | 状态 | 修复要点（文件） |
|---|---|---|
| Major1 Mica 失效 | ✅ | 根 Grid 不再遮盖 Mica；且仅"跟随系统"主题透出 Mica（显式浅/深主题用主题实底，避免 Mica 跟随系统主题造成白字压浅底的对比度事故）；Win10 回退实底。`AppRoot.cs` 根组合 + `AppTheme.cs` 新增 `AppChrome.MicaEnabled`，`App.cs` 复用 |
| Major2 通知分级 | ✅ | 新增 `Notice(NoticeLevel)` 四级模型与 `Notices` 语义分类（`State/NoticeModel.cs`），`WorkbenchStore.SetError→SetNotice`、快照 `Error→Notice`；`Ui.NoticeBar` 按 Info/Success/Warning/Error 映射 InfoBar；AppRoot/OutputSettingsPanel/HistoryDialog 全部复用，成功=绿、错误=红 |
| Major3 取消确认 | ✅ | 取消任务走确认对话框（启用 `UiText.CancelConfirmTitle/Body`），确认后给"已请求取消"信息反馈。`AppRoot.cs` cancelId 对话框 + `TaskRow` 回调改道 |
| Minor4 +N 提示 | ✅ | `Issues.Take(2)` 超出部分显示"另有 N 条提示，请打开技术详情"，且技术详情对话框新增"兼容性提示"全文（`TaskRow.cs`、`AppRoot.cs`） |
| Minor5 源信息 ToolTip | ✅ | 源信息行截断路径补 ToolTip（`TaskRow.cs`） |
| Minor6 文件名省略号 | ✅ | `Wrap + MaxLines(2) + CharacterEllipsis`（`TaskRow.cs`） |
| Minor7 状态色收敛 | ✅ | 新增 `StatusTones.For(...)`（`State/StatusTone.cs`）单点映射 + `Ui.ToneColors`；TaskRow/HistoryDialog 两份复制删除，Ui.StateColor 死代码一并移除 |
| Minor8 文案集中 | ✅ | 新增文案全部入 `UiText`（约 45 条常量）；通知文案经 `Notices` 引用 UiText；HistoryDialog 原内联标题迁入 |
| Minor9 死代码 | ✅ | `Ui.SectionHeader/PageHeader/ProgressBar/StatusDot/StateColor` 删除；`IconButton/IconTextButton` 改为 FontIcon 图标实现并投入实际使用 |
| Minor10 常量接线 | ✅ | `App.cs` MinWidth/MinHeight ← `Space.WindowMinWidth/Height`；`AppRoot` 面板宽度 ← `Space.SettingsPanelMin`；`Space` 拆到纯文件 `Theme/Space.cs` 可测 |
| Minor11 触控目标 | ✅ | 任务卡复选框 40×40、标题栏按钮 40×40（`TaskRow.cs`、`TitleBarView.cs`） |
| Minor12 拖放反馈 | ✅ | 拖入显示居中"松开以添加文件和文件夹"覆盖层（LayerFill+Accent 描边、不拦截命中）；drop 区域扩大到整个窗口；模态对话框打开时忽略拖放（`AppRoot.cs`） |
| Minor13 NumberBox 边界 | ✅ | 全部数值字段 `Range(min,max) + SpinButtons + SmallChange`；空值仍=自动（NaN 哨兵保留）；口径统一走 `FieldValues` 纯函数（`State/FieldValues.cs`、`OutputSettingsPanel.cs`、`SettingsDialog.cs`） |
| Minor14 字号归位 | ✅ | 硬编码 11/12/24 全部改 TypeStyles（`Caption`/`Title`/`Ui.DimCaption`），最小 12px；约 30 处 |
| Minor15 加速键可见 | ✅ | 添加文件/文件夹 ToolTip 显示 Ctrl+O / Ctrl+Shift+O（`UiText.AddFilesTip/AddFolderTip`） |
| Minor16 退出超时 | ✅ | 退出忙等改为 30s 有界等待（`WaitUntilAsync`），超时报"停止任务超时"并留在窗口（`AppRoot.cs`） |
| Minor17 落盘去抖 | ✅ | 新增 `DebouncedAction`（`State/DebouncedAction.cs`）；参数编辑的设置落盘 400ms 合并，退出/保存前 `FlushAsync` 保证最后修改不丢 |
| Minor18 取消语义 | ✅ | `DialogChrome` 支持 `beforeSecondaryClose`；设置有未保存更改时首次"取消"→警告条+按钮变"放弃更改"，二次点击才丢弃（`DialogChrome.cs`、`SettingsDialog.cs`） |
| Minor19 预设"自定义" | ✅ | 显式选择"自定义"给信息提示；预设下拉下常驻说明"预设只填入参数；手动改动后自动显示为自定义"（`OutputSettingsPanel.cs`） |
| Minor20 重置确认 | ✅ | "重置所选参数"先弹确认（说明会清除文件名/裁剪/音轨选择）再执行（`AppRoot.cs`） |
| Nit21 副标题 | ✅ | `UiText.AppSubtitle`（`TitleBarView.cs`） |
| Nit22 状态图标 | ⏸ 暂缓 | 未列入本轮任务清单；状态已有文字+颜色+LiveRegion 冗余，后续可加 Segoe 状态图标 |
| Nit23 魔法数 | ✅ | `DialogChrome` 内边距归 `Space.Lg/Space.Xl` |
| Nit24 参数收敛 | ✅ | `TaskRow.Render` 15 个 Action 收敛为 `TaskRowActions` record（`TaskRow.cs`） |
| Nit25 过渡动画 | ◐ 部分 | 横幅区与任务列表容器加 `LayoutAnimation()` 隐式过渡；`Animations.Animate(Spring)` 列表编排**暂缓**：工作台列表经 `UseExternalStore` 提交、脱离动画扼流点，需先引入变更提交封装并配套 `UseReducedMotion`（WCAG 2.3.3）后再上 |
| Nit26 Fluent 图标 | ✅ | 工具栏 添加文件(``)/添加文件夹(``) 图标+文字；队列上移/下移改图标按钮(``/``，Tooltip+无障碍名) |
| Nit27 证据漂移 | ✅ | `M4-ui-runtime.md` P0 段改写为现行任务卡片实现并标注修正 |

## 自动化验证

- `dotnet build TransFlux.slnx -c Debug -p:WindowsPackageType=None` → **0 警告 0 错误**（与 task-2 联合复验）。
- `dotnet test TransFlux.slnx -c Debug` → **366 项全绿**（Domain 120 / Application 96 / Infrastructure 32 / Engine.Integration 118）。
- 本轮新增测试 32 项（tests/TransFlux.Application.Tests）：`UiNoticeTests` 7（四级严重度语义 + WorkbenchStore 通知通道）、`UiStatusToneTests` 13、`UiFieldValuesTests` 6、`UiDebouncedActionTests` 4、`UiLayoutConstantsTests` 2。

## 原生窗口实测（computer-use 级 UIA + 截图）

- Mica（跟随系统）：[ux-fix-mica-empty.png](screenshots/ux-fix-mica-empty.png)、[ux-fix-mica-system.png](screenshots/ux-fix-mica-system.png) — 材质透出桌面色调，文字清晰。
- 设置对话框 + NumberBox 微调按钮：[ux-fix-settings-dialog.png](screenshots/ux-fix-settings-dialog.png)。
- 未保存更改两段确认：[ux-fix-cancel-dirty.png](screenshots/ux-fix-cancel-dirty.png) — 黄色警告条 + 「放弃更改」按钮；UIA 四阶段断言全过（干净取消直接关 / 脏取消保留 / 放弃更改关闭）。
- 深色主题：[ux-fix-dark-theme.png](screenshots/ux-fix-dark-theme.png) — 修复 Mica 跟随系统主题的对比度事故后，深色实底白字完全可读。
- UIA 无障碍树：[ux-fix-ui-tree.txt](screenshots/ux-fix-ui-tree.txt) — 标题栏/工具栏/筛选/队列控制/开始转换均有可读名称。
- 实测中发现并修复了两处实现缺陷：设置取消判定逻辑反转、显式深色主题下 Mica 对比度不足（详见上表 Major1/Minor18）。

## 已知遗留

- Nit22（状态胶囊图标）暂缓；Nit25 弹簧列表动画暂缓（理由见上表）。
- Narrator、高对比度、200% 文本缩放、拖放高亮的原生验收仍未覆盖（UIA 无法模拟拖放）；与 UX-workbench-redesign.md 的未覆盖清单一致。
- 单测未覆盖 WinUI 视觉层（TaskRow/对话框布局），以本轮截图实测作为回归基线。
