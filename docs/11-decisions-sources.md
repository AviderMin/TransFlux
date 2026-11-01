# 11 · 决策记录、资料与风险

## ADR · 1.0.0.5 硬件兼容基线

采纳：固定 Gyan FFmpeg 8.0.1 full_build-shared（GPLv3），替代需要 NVENC API 13.1 / 驱动 610.00 的 nightly。本机 RTX 4060 / 591.86 已通过真实 NVENC 调度、输出验证和解码；不自动升级用户驱动。归档、exe、DLL 全部固定哈希，升级必须重跑格式及硬件回归。GPL 分发路线不变。使用 DXGI ordinal 创建 D3D11 设备，LUID 仅为身份；H.264 Auto 选择已验证 GPU，再有限回退 CPU。来源、失败对照及 Intel/AMD 实机限制见 [证据](evidence/HARDWARE-COMPATIBILITY.md)。

## ADR · 2026-11-01 输出目标解耦

按用户要求拆为媒体形式、容器、编码；取消界面固定组合及执行层固定 H.264/AAC 的限制。
共享矩阵同时驱动界面与兼容性验证，所有 59 种开放转码组合经过固定核心实际编码/解码测试。
新增编码有独立质量参数映射，不承诺跨编码器等效；原 H.264 默认保持不变。
H.265/AV1/VP9 当前仅软件路径，HDR/10-bit 限制另行管理，不将编码器数量等同于完整输入支持。
详见 [格式扩展与证据](evidence/OUTPUT-FORMATS.md)。

## ADR · 2026-11-01 文件优先的任务工作台

用户审查确认：左侧格式目录、新增默认值、行内参数、批处理窗口及后台队列的作用范围不一致，需整体调整。
决定改为导入后选择文件、右侧统一编辑、开始后冻结参数的卡片工作台；取消/移除/重试/再次转换明确分离。
由独立 WorkbenchStore 协调操作，持久化可见卡片并显式关联 JobId；启动时暂停等待用户继续。
替代早期 Format Factory 式目录与参数模态窗口，不改变 Reactor/WinUI、模块依赖、默认质量、输出保护或许可证路线。
验证与剩余限制见 [UX 重构记录](evidence/UX-workbench-redesign.md)。

基线日期：2026-10-31。网页是可变化的上游资料；实际实施时记录对应 commit/tag 与包锁定结果。下面的“已核验”表示查阅官方资料，不表示在本仓库运行验证。

## 架构决策记录

| ADR | 决策与理由 | 状态 / 复议条件 |
|---|---|---|
| 001 | Microsoft.UI.Reactor 是核心；满足用户硬要求，以 C# 组件生成原生 WinUI | 强约束；变更需要用户明确要求 |
| 002 | .NET 10 / Windows 11 22621+ x64 起步；与所查 Reactor 入门基线一致 | 待 M0 锁定完整工具链，不能盲填 Windows App SDK 版本 |
| 003 | FFmpeg/ffprobe 独立进程；降低 native crash 对 App 影响，便于核心溯源 | 采纳；进程启动成本可测，不引入 P/Invoke libav 起步 |
| 004 | 首发 CPU libx264，默认候选 GPL 核心包；避免缺少可靠 CPU 基线 | 工程提案；实际分发许可材料发布前核验 |
| 005 | SQLite+提交 journal；文件系统和数据库非同一原子事务 | 采纳；不以“事务成功”替代输出验证 |
| 006 | 保守并发、主动试跑、可解释回退；性能必须实测 | 采纳；实测后才能提高默认并发 |
| 007 | 首发 SDR 转码、HDR 受限转封装；防止错误色彩处理 | 采纳；HDR 色彩链验证完成后扩范围 |
| 008 | 无运行暂停/编码续传；队列暂停仅停止新启动 | 采纳；可靠分段方案另立 ADR |
| 009 | MSIX、离线核心、无自动遥测与更新器 | 待 M0/M5 打包验证；便携版和联网能力另议 |
| 010 | 原生外壳与 progressive disclosure；高级能力不污染默认操作 | 采纳；需真实可访问性验收 |

## 官方资料与已核验事实

### S1 · 框架身份与成熟度

[微软 Reactor 官方仓库](https://github.com/microsoft/microsoft-ui-reactor) 将其定义为构建 WinUI 3 应用的声明式 C# 框架，仍为实验性质。本文“不可替换”来自用户要求，不能解释为上游已有稳定性或支持保证。

[微软 2026-10-21 Reactor 介绍](https://devblogs.microsoft.com/ifdef-windows/microsoft-ui-reactor-a-c-first-way-to-build-winui-apps/) 明确实验状态、缺少 API 保证。设计据此提出 M0 门禁、精确版本锁定和本地适配层，这是本项目工程判断。

### S2 · 入门与基线

[官方 getting-started](https://github.com/microsoft/microsoft-ui-reactor/blob/main/docs/guide/getting-started.md) 核验时列有 `Microsoft.UI.Reactor 0.1.0-preview.16`、.NET 10+ 和 `net10.0-windows10.0.22621.0` 示例，并给出官方模板包。这里只记录观察值，不宣布该版本为最终锁定版本，也不推断所有包组合都可运行。

[Windows App SDK 官方模板说明](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/Templates/Dotnet/README.md) 包含 Reactor 模板并说明实验性。M0 应先查询实际可用模板与版本；文档中的 `<version>` 不可原样执行。

### S3 · Hooks 与生命周期

[官方 hooks 说明](https://github.com/microsoft/microsoft-ui-reactor/blob/main/docs/guide/hooks.md) 可核验状态、effect cleanup、外部 store 等机制。`UsePersisted` 的进程内保留与真正跨进程落盘应区别；本项目长期任务由 SQLite 保存。04 的纯渲染、订阅清理和 Dispatcher 边界是应用实现约束。

### S4 · 发布

[官方 packaging 说明](https://github.com/microsoft/microsoft-ui-reactor/blob/main/docs/guide/packaging.md) 描述其 WinUI/Windows App SDK 应用发布方式。MSIX、自包含、核心随包分发的组合是本项目方案，必须在干净机器验证，不直接将文档示例当成安装保证。

### S5 · 媒体命令与硬件

[FFmpeg 命令行文档](https://ffmpeg.org/ffmpeg.html)、[完整文档](https://www.ffmpeg.org/ffmpeg-all.html) 用于核验进度、映射、设备等基础语义。编译列出 hwaccel 不代表本机可用；上传/下载成本可能抵消硬件收益。因此 06 要求完整路径试跑与同参数对照，这是项目设计选择。

[NVIDIA 官方 FFmpeg 页面](https://developer.nvidia.com/ffmpeg) 提供编码/解码与 GPU 路径资料，支持能力随硬件变化。该页不用于推断 Intel/AMD 或所有 NVIDIA 产品均支持同一功能。

### S6 · 进程生命周期

[Microsoft Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) 说明进程组与 KILL_ON_JOB_CLOSE。具体启动器、继承和恢复设计由本项目实现与测试。

### S7 · 第三方许可

[FFmpeg 官方许可说明镜像](https://svn.ffmpeg.org/legal.html) 用于了解 LGPL/GPL 与分发要求。此次主站 legal/probe/filter 的独立页面读取部分超时；未据此声称逐项核验了所有 filter/ffprobe 字段和具体二进制许可。M0 必须补录实际核心的 LICENSE、buildconf、完整依赖与许可文件。

[FFmpeg 官方文档目录](https://ffmpeg.org/documentation.html) 是后续 probe/filter 专项核验入口。本文的兼容白名单、质量映射、测试预算、UI 尺寸、接口和状态机都是产品/工程设计，而非官方已保证行为。

## 风险登记

| 风险 | 影响 | 缓解 / 关闭证据 |
|---|---|---|
| R01 Reactor 预览 API 变动 | 编译/行为破坏 | M0 固定包；升级隔离分支；组件回归 |
| R02 wrapper/虚拟化不成熟 | 卡顿、内存、焦点丢失 | 1000 行原型，局部原生适配与生命周期测试 |
| R03 GPU 型号/驱动/会话差异 | 初始化失败、性能退化 | 能力分层、试跑、缓存失效、有限回退 |
| R04 HDR/旋转/SAR/VFR 错误 | 画质或同步损失 | 首发白名单、计划语义验证、专门样片 |
| R05 GPL/依赖材料不足 | 无法合规分发核心 | 发布前核对实际构建、源码与声明；未满足不发布 |
| R06 rename 与 DB 崩溃窗口 | 假成功、重复文件 | commit journal、文件身份校验、故障注入 |
| R07 网络盘/长路径/权限 | 卡死或提交失败 | 有界异步、取消、平台测试与限制提示 |
| R08 进程隔离被误当安全沙箱 | 恶意媒体风险 | 输入范围、禁网络、最小权限、维护核心更新 |
| R09 质量参数跨编码器误映射 | “均衡”质量不一致 | 独立标定、人工抽检、不宣称数字等效 |
| R10 没有 GPU 实机 | 支持宣称失真 | 报未执行，关闭相应后端，交接保留限制 |

## M0 待验证清单

精确 SDK/模板/Windows App SDK/Reactor 配对；是否需要额外 Windows SDK/签名工具；核心可信来源与完整许可；MSIX 核心进程与自包含依赖；虚拟列表/key/订阅桥；GPU 设备映射与质量档；SDR 色彩白名单；网络输出提交语义。以上都有默认方案，不需要在文档阶段先向用户索要硬件型号才能推进。

## ADR 追加（2026-11-01）

| ADR | 决策与理由 | 状态 |
|---|---|---|
| 011 | 转换工作台采用“格式工厂”式布局（左目标格式目录 / 右任务进度列表，开始/暂停），参数按任务项在独立窗口分组编辑（预设优先 + 允许具体数值），多选走批处理窗口；预设内嵌于参数窗口，不设独立预设页；设置(含诊断)与关于移入顶栏右侧（参照 SurgeLink）。理由：贴合用户第一直觉、降低导航层级。 | 已被 2026-11-01 文件优先工作台 ADR 取代（见上方“ADR · 2026-11-01 文件优先的任务工作台”；仅作历史追溯） |
| 012 | 为满足“输入具体参数值”，在 ConversionRequest 增加可选显式覆盖（CrfOverride、VideoBitrateKbps、ResizeSpec.TargetWidth/Height、FrameRateOverride、AudioSpec.SampleRate/Channels），由 Engine 计划编译器在挡位之上优先采用；null 表示按挡位/源推导。 | 采纳；随 UI 落地 |
