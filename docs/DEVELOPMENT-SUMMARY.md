# TransFlux 已完成开发汇总

更新日期：2026-11-04（Asia/Shanghai）  
源码版本：1.0.0.9（`Directory.Build.props`）  
代码基线：`master` @ `3c517c4` + 当前未提交开发工作树  
最近一次安装包：1.0.0.8（1.0.0.9 尚未生成安装包）

## 1. 产品定位

TransFlux 是一款本地媒体转换桌面工具，界面核心为 **WinUI 3 + Microsoft.UI.Reactor**，媒体处理使用受控的 FFmpeg/ffprobe 独立进程。产品主张是“拖入文件，选择用途，放心转换”：操作路径尽量简单，同时对输出正确性、源文件安全和失败恢复保持可解释、可验证。

核心原则：

- 源文件永远只读，不覆盖、不删除、不隐式替换。
- 输出先写入同卷 staging，验证后通过 no-replace 提交；不确定时进入 NeedsAttention，保留证据。
- UI 与实际执行使用同一份解析后的选流/参数事实，不用界面状态冒充执行事实。
- 默认无遥测、无在线检查、无媒体上传，不引入网络媒体协议。
- 没有真实样片/实机证据的高级能力不宣称已支持。

## 2. 技术架构

| 层 | 职责 | 主要项目 |
|---|---|---|
| App | WinUI 3/Reactor 组件、工作台投影、组合根、窗口与选择器互操作 | `src/TransFlux.App` |
| Application | 用例服务、队列调度、恢复编排、端口契约 | `src/TransFlux.Application` |
| Domain | 纯领域模型、请求/计划/状态机、兼容性与选流规则 | `src/TransFlux.Domain` |
| Engine.FFmpeg | probe、计划编译、进程运行、验证、核心定位 | `src/TransFlux.Engine.FFmpeg` |
| Hardware.Windows | DXGI 适配器枚举、能力试跑 | `src/TransFlux.Hardware.Windows` |
| Infrastructure | SQLite、文件系统、提交日志、输出路径、workspace 持久化 | `src/TransFlux.Infrastructure` |

关键设计：

- `ConversionRequest` 支持 v1 旧语义与 v2 独立音频/字幕选择；新请求默认 v2，旧 JSON 缺少 `requestVersion` 时按 v1 解析。
- `ValidatedPlan` 冻结执行参数与 `ExpectedOutput`（流数、逐轨签名、语言、色彩、DV 期望）。
- `commit_journal` 记录 Prepared/Published/Failed；启动恢复通过身份/大小/内容哈希核验，绝不把数据库记录当作文件存在的证明。
- `FfmpegRunner` 使用 `-progress` 心跳 + 总运行/停滞 watchdog，超时杀进程树并返回 `run.timeout`/`run.stalled`；取消仍走 Canceled 结果。
- `FfmpegCoreLocator` 只接受带 `core-manifest.json` 且哈希匹配的固定核心；显式目录/环境变量为权威，不回退 PATH/CWD。
- SQLite 使用 WAL、单写者串行化、迁移版本与 checksum 校验；损坏库可移旁并恢复 `.bak`，锁占用不会误当损坏移库。

## 3. 已完成能力

### 3.1 媒体转换

- **视频转码**：CPU libx264 / libx265 / libaom-av1 / libvpx-vp9；H.264 的 NVENC/QSV/AMF 硬件候选；自动回退一次并记录实际执行候选。
- **音频提取**：M4A/AAC、MP3、FLAC、WAV、Ogg、Opus 等目标；明确单轨提取，禁止多选静默丢轨。
- **Remux 转封装**：MKV/MP4/MOV/WebM 的容器级复制；字幕与附件按容器能力处理；MKV 保留字体附件。
- **音频复制**：2026-11-04 起支持视频转码 + MKV 原样复制源音频（`-c:a copy`），AC3/EAC3/TrueHD 等按 MKV 白名单准入；真实 AC3 6ch 样片已验证。
- **字幕**：软字幕复制/转换；SRT/ASS/SSA/WebVTT/mov_text 等文本字幕与 PGS 等位图字幕按容器分流；字幕语言与默认/强制 disposition 尽量保留。
- **元数据**：StripPersonalTags / PreserveTitleAndChapters 两种策略；章节在裁剪时显式移除并提示；字体附件在 MKV 路径保留。
- **输出矩阵**：4 种视频容器 + 7 种音频容器，历史证据覆盖 49 种视频组合与 10 种音频组合（59 种）。

### 3.2 工作台与 UI

- 文件优先的任务卡片工作台：卡片有稳定 MediaId，入队后绑定 JobId，运行中参数只读。
- 右侧输出设置面板：媒体形式/容器/编码分层；视频、音频、字幕、输出分组；即时生效；预设保存/应用。
- 队列操作：选择、批量开始、暂停/继续、取消、重试、上移/下移、移出队列并编辑、复制卡片、移除/撤销。
- 历史、设置、诊断报告、关于对话框；诊断默认脱敏路径与用户名。
- NeedsAttention 卡片提供显式“放弃并清理暂存”，与普通“移除”分离；普通移除不破坏保留的暂存证据。
- 后台事件经 DispatcherQueue 投影；进度按 250ms 合并；窗口退出前等待安全终态并持久化。

### 3.3 队列、状态与恢复

- 状态机：Queued → Preflighting → Running → Verifying → Committing → Succeeded；支持取消、失败、NeedsAttention、Interrupted、Failed、Canceled。
- 单写者调度、并发上限、一次自动回退；取消在提交边界之后拒绝，提交边界之前可取消。
- 工作区持久化：草稿、选流、参数与 JobId 绑定；旧 workspace 损坏时备份原值并恢复可用空工作台，不永久禁用后续保存。
- 启动恢复：
  - journal 逐条隔离对账；失败不阻断其他 journal。
  - Committing 任务逐 job 核验输出；成功补记 Succeeded，失败转 NeedsAttention。
  - 终态孤儿 staging 通过 `OrphanCleanupService` 在 24h 后清理；NeedsAttention 保留。
  - 数据库损坏/锁占用有降级路径；备用数据库使用唯一临时路径。

### 3.4 正确性验证

- 逐轨流校验：输出流数、视频/音频/附件/字幕数、逐轨 codec、声道、默认/强制 disposition、语言。
- 视频尺寸/像素格式/时长/容器；MP4 faststart 检查。
- 全量/采样解码按全局流索引显式 `-map`，避免第二音轨或封面图错位。
- Dolby Vision：解析 DOVI profile、BL compatibility、RPU/EL/BL present flags；MKV 转封装要求 Profile 8 + BL compat > 0，并验证 RPU/EL/BL 不丢失。
- 输入格式拒绝 playlist/concat/image sequence（`INPUT_FORMAT`）。
- 输出路径防源文件同名、硬链接/大小写/重解析点等同文件风险；提交 no-replace。

### 3.5 安全与发布

- 进程使用 `ProcessStartInfo.ArgumentList`、`UseShellExecute=false`、Job Object、`-nostdin`、协议白名单 `file`。
- 核心/运行库/归档 SHA-256 固定于 `tools/ffmpeg/core-manifest.json`；缺失许可材料拒绝打包。
- `tools/package-exe.ps1` 发布自包含 x64/ARM64 安装包，记录核心清单哈希、源码 base revision、工作树脏状态和工具链；旧 `scripts/build-installer.ps1` 已弃用并显式报错。
- 当前应用版本 1.0.0.9；最近有安装/卸载/E2E 证据的安装包为 1.0.0.8。

## 4. 数据与接口要点

- SQLite 表：sources、jobs、attempts、outputs、commit_journal、presets、capability_cache、settings、schema_migrations。
- 迁移 schema 版本 1–4；拒绝高于当前构建的版本，已应用迁移 checksum 漂移会拒绝打开。
- 请求/工作区/预设带版本；旧 v1 请求按旧选流语义解析，不把旧音频索引误读为字幕索引。
- 输出记录包含 `VerificationSummary`；恢复路径写入明确来源说明。
- Capability cache key 包含 core hash、OS build、probe schema、适配器身份与目标编码器；能力枚举失败不写入空缓存。

## 5. 当前验证状态（2026-11-04）

```powershell
dotnet build TransFlux.slnx -c Release
# 0 warnings / 0 errors

dotnet test TransFlux.slnx -c Release --no-build
# 542 passed / 0 failed
# Domain 170 / Application 137 / Infrastructure 65 / Engine 170

dotnet run --project tools/TransFlux.E2E/TransFlux.E2E.csproj -c Release --no-build
# E2E_RESULT=PASS（3.48s，源文件 SHA 不变，CPU libx264）
```

核心：Gyan FFmpeg 8.0.1 full_build-shared（GPLv3），core manifest 校验通过。

最近安装包证据：**1.0.0.8**（Inno Setup x64）曾完成静默安装/启动/安装目录核心 E2E/卸载验证；1.0.0.9 尚未生成安装包，发布前需重新执行安装目录验证。

## 6. 已知限制

- **Dolby / HDR / DV 高级能力**：G2 目前只完成 MKV 音频复制切片；EAC3/TrueHD 仅规则验证；Atmos 未做对象/Atmos 证据检测；HDR10/HLG、DV profile 5/7、HDR10+ 动态 metadata、HDR 重编码均未实现。
- **字幕烧录**：尚未实现 SDR 文本字幕烧录（G3），当前只有软字幕复制/转换。
- **音频编码**：未开放普通 AC3/EAC3 编码、downmix、Atmos 创作。
- **硬件**：H.264 GPU 路径已有基础验证；HEVC/AV1 Main10 GPU、libplacebo/HDR 合成、完整资源租约未完成。
- **发布**：1.0.0.9 尚无安装包证据；安装目录验证需发布时重新执行。
- **测试边界**：真实样本主要覆盖合成 AC3/H264；Dolby Vision/RPU、TrueHD/EAC3、HDR 真样片仍需合法素材与专项测试。
- 少量 UI 文案/布局没有像素级自动化证据；旧工作区/预设的极端组合依赖打开后归一化。

## 7. 代码与命令入口

- 组合根：`src/TransFlux.App/Hosting/TransFluxAppHost.cs`
- 工作台：`src/TransFlux.App/State/WorkbenchStore.cs`、`Components/Workbench/`
- 调度与恢复：`src/TransFlux.Application/Services/JobScheduler.cs`、`RecoveryService.cs`、`OrphanCleanupService.cs`
- 计划与验证：`src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs`、`Verifier/FfmpegVerifier.cs`
- 域规则：`src/TransFlux.Domain/Rules/MediaCompatibility.cs`、`OutputFormatCatalog.cs`
- 持久化：`src/TransFlux.Infrastructure/SqliteJobStore.cs`、`OutputCommitter.cs`
- 发布：`tools/package-exe.ps1`

## 8. 文档说明

本文件取代此前的编号开发文档（01–16）、旧交接/审查/实施状态与证据目录，作为**唯一已完成开发汇总**。后续计划见 [PROJECT-OUTLOOK.md](PROJECT-OUTLOOK.md)。历史截图与逐阶段原始证据已清理；关键命令、测试数字和限制保留在本汇总中。
