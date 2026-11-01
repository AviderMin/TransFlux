# 13 · 全项目审查与修复优先级

审查日期：2026-11-01（Asia/Shanghai）。代码基线 `bf04a00efb37a0616ce10f38480ff09b9624314b` 加当前未提交的 UI/滚动布局及文档变更，应用版本 1.0.0.8。本轮只交付审查、目标和实现路径，不修复功能代码、不发布安装包。

配套阅读：[高级媒体路线](14-advanced-media-roadmap.md)、[编码 AI 任务单](15-advanced-media-handoff.md)。本轮发现优先于旧文档中“已修复/已完成”的描述；历史测试通过不代表下面边界已覆盖。

## 结论

项目适合在当前分层上继续扩展，不建议推倒重写 UI 或更换框架。Domain → Application → Engine/Infrastructure/Hardware → Reactor UI 的职责基本明确，参数用 ArgumentList、进程用 Job Object、输出有临时文件/提交日志/不覆盖移动，这些基础值得保留。

但现在不能直接放开 HDR/Dolby/烧录开关。优先修复恢复中的假成功、提交失败时的文件保全、选流语义及输出验证。高级功能需要“请求 → 解析 → 计划 → 执行 → 验证 → 恢复”的完整链，不是多加编码器枚举或拼几条 FFmpeg 参数。

## 审查范围与证据

| 范围 | 已检查 | 边界 |
|---|---|---|
| Domain | 请求/计划、输出矩阵、色彩约束、字幕策略、状态机 | 未证明所有任意构造请求都合法 |
| Application | 入队、选流、回退、提交恢复、工作台持久化 | 重点复现见下文；未做长时间并发压力测试 |
| Engine | ffprobe、颜色分类、计划、进程取消、输出验证、核心定位 | 未执行真实 HDR/DV/Atmos 内容验收 |
| Infrastructure | SQLite/WAL/迁移、输出预留、原子移动、提交身份、预设/工作区 | 未做拔盘/断电/网络盘故障矩阵 |
| Hardware | DXGI、逐设备试跑、缓存、H.264 硬编候选 | 不把既往 NVIDIA 验证推广到 Intel/AMD/Main10 |
| Reactor UI | 选择范围、输出字段、警告、技术详情、虚拟列表/滚动规范 | 本轮未重新做全部 DPI/读屏验收 |
| 发布与测试 | 核心 manifest、Inno 脚本、样片和回归 | 构建身份/依赖许可材料仍需发布审查；不是渗透测试报告 |

本轮 `dotnet test TransFlux.slnx -c Release -p:WindowsPackageType=None --no-build`：**366 项通过**，Domain 120 / Application 96 / Infrastructure 32 / Engine 118。使用此前已成功编译的 Release 产物，日志在忽略的 `artifacts/review-20261101/tests.log`。测试不是 Dolby/HDR/烧录验收。

额外建立忽略目录内的最小 C# 复现程序，调用真实 Domain、Application、SQLite 和 OutputCommitter，未修改生产代码或用户数据。`artifacts/review-20261101/repro.log`：

```text
Published recovery: finalExists=False state=Succeeded outputs=1
Release deletes verified temp=True
Subtitles: default=1, select audio only=0
DV profile 7 compat 1 allowed=True
Canceled capture: returned exit=1 TimedOut=False (no cancellation result)
```

复现程序和日志是本机附加证据，未纳入源码；下面给出独立可重建的场景和验收要求，交接不能依赖忽略目录存在。

## 必须优先处理的问题

### R01 · P1 · Published 恢复跳过文件核验，可能报告假成功（已复现）

位置：[RecoveryService.cs](../src/TransFlux.Application/Services/RecoveryService.cs) 第 33–37 行及后续 Committing → Succeeded 分支。

构造一个 Committing job、对应 attempt 和 Published journal，但不创建 FinalPath。启动恢复直接 EnsureOutputAsync、删 journal，随后把任务设为 Succeeded。实际复现结果如上。OutputCommitter.ReconcileAsync 本来有 Published 文件存在/大小/哈希检查，但该分支绕过它。现有 `RecoveryServiceTests.PublishedJournal_IsClosed_AndOutputRecorded` 只用内存存储和虚拟路径，反而固化了这个错误。

修复：所有未确认为已验证的 Published/Prepared 都经过同一 reconciler；缺失、被替换或无法确认身份时转 NeedsAttention 并保留恢复证据。数据库已有 OutputRecord 的历史记录可以保留“曾发布”事实，但不得据此证明文件当前存在。不要为了修复恢复而删除用户文件。

验收：真实 SQLite + 文件系统覆盖缺失、同长度替换、错误哈希、正常 Published、旧 journal 无哈希、重复恢复；缺失文件绝不能新建成功结果。

### R02 · P1 · 提交失败后清理掉可恢复临时输出（调用链确认，删除行为已复现）

位置：[JobScheduler.cs](../src/TransFlux.Application/Services/JobScheduler.cs) 第 918–928 行；[OutputPathService.cs](../src/TransFlux.Infrastructure/OutputPathService.cs) 第 81 行起。

commit 返回失败后，任务进入 NeedsAttention，却立即调用 ReleaseQuietly。Release 同时删预留锁和整个 WorkDirectory，可能包含已验证的唯一输出和 Prepared journal 引用的 TempPath。对 Release 的最小复现确认文件会消失。完整提交故障注入尚待补测，不能把此复现说成已覆盖全部故障点。

修复：拆分“释放调度/命名预留”与“删除工作文件”；跨过提交边界的失败保留 staging、journal、身份信息，恢复明确关闭后再回收。Prepared journal 写入失败、rename 失败、rename 成功但 DB 失败分别处理。不能靠 catch 后一律清理。

验收：在上述每个点注入异常/取消/进程退出，至少保留 temp 或已确认 final；重启可恢复或明确需处理，无假成功、无唯一成果丢失。

### R03 · P1 · 选择音轨会隐式移除字幕（已复现）

位置：[SubtitlePolicy.cs](../src/TransFlux.Domain/Rules/SubtitlePolicy.cs) 第 48 行；[OutputSettingsPanel.cs](../src/TransFlux.App/Components/Workbench/OutputSettingsPanel.cs) 音轨复选框；[FfmpegPlanner.cs](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs) SelectAudioStreams。

SelectedStreamIndices 同时代表视频、音频、字幕。默认空集合保留字幕；UI 只勾选音轨 #1 后变为 `[1]`，字幕策略据此排除全部字幕。复现含视频 #0、音轨 #1、SRT #2：默认字幕数 1 → 选择音轨后 0。添加烧录之前必须解决。

修复：拆成视频选择、音轨列表、字幕策略；区分 Auto / None / Explicit，不能继续让空集合同时表示“默认”和“不要”。旧请求迁移保持旧执行语义并提示不确定项，不能直接把所有旧索引当字幕索引。

验收：只改音轨不改变字幕；字幕关闭/保留/烧录互不混淆；多选批处理不得跨文件复用裸流编号。

### R04 · P1 · Dolby Vision 准入过宽且缺少保留验证（准入反例已复现，真实 RPU 丢失未复现）

位置：[MediaCompatibility.cs](../src/TransFlux.Domain/Rules/MediaCompatibility.cs) 第 131–163 行；[ValidatedPlan.cs](../src/TransFlux.Domain/Plans/ValidatedPlan.cs) ExpectedOutput；[FfmpegVerifier.cs](../src/TransFlux.Engine.FFmpeg/Verifier/FfmpegVerifier.cs)。

`DvProfile == 8 || DvBlSignalCompatibilityId > 0` 会接受不一致/未支持的 profile 组合，例如复现的 profile 7 + compatibility 1。注释把 profile 7 一概说成“无兼容基底”也不准确；不同 profile/layer/compatibility 必须按规范分开。[Dolby profile 表](https://ott.dolby.com/OnDelKits/Dolby_Vision_Online_Delivery_Kit/v1/Documentation/Specs/Visio_Profiles/help_files/topics/c_dovi_profiles.html) 区分 BL/EL、兼容标识及 metadata carriage，不支持用一个 >0 谓词替代。

当前 ExpectedOutput 没有 RPU/EL、DV 配置、HDR 静态/动态 metadata 断言；Remux 甚至不声明色彩/像素格式期望。退出 0、流数相同不能证明 DV/Atmos 保留。应将“已识别”“允许复制”“经过保留验证”分开，收紧到有真实样片证据的组合；未验证时明确阻断或降级为不保证保留，不能宣称已支持。

## 重要正确性与一致性问题

| ID / 优先级 | 代码证据与触发场景 | 建议及验收 |
|---|---|---|
| R05 / P2 | WorkbenchRules.cs:27–28 无条件警告转码会丢字幕；实际 SubtitlePolicy 已保留/转换软字幕。FfmpegPlanner.BuildWarnings 在字幕因选流被全排除时还可能说“字幕随输出保留” | UI、计划、验证共用解析后的 StreamPlan/LossReport；逐流列出复制/转换/烧录/排除及原因，零条字幕不能显示全部保留 |
| R06 / P2 | FfmpegPlanner.BuildVideoCandidate 不映射字体附件，BuildVideoExpectedOutput 将附件数写死 0；MKV ASS/SSA 软字幕可复制但附件字体丢失。现有附件测试仅覆盖 Remux | 对保留 ASS 的转码定义字体依赖保留策略；真正字体样片验证，不能用任意文本伪造 font 文件充当渲染验收 |
| R07 / P2 | OutputSettingsPanel 音轨支持多选；BuildAudioExtract 只映射 SelectPrimaryAudio 的单条流 | 提取音频明确“单轨输出”并限制 UI，或引入显式多输出计划；不能悄悄只输出第一条 |
| R08 / P2 | FfmpegVerifier.SampleDecodeAsync 没有 `-map`，FFmpeg 默认只选择部分轨道；全量模式同样如此。Compare 也不核验复制前后逐轨 codec/profile、声道布局、forced/default 或内容身份 | 定义每条输出轨道期望并显式逐轨解码；第二音轨损坏、默认字幕标志反转、同流数错误 codec 都应被检测。烧录要验证画面，不是只数字幕轨 |
| R09 / P2 | ProcessCapture.cs:39 捕获取消后只返回普通 CaptureResult，没有 Canceled 字段也不抛出；已复现 ct 取消但 TimedOut=false/exit 非零。FfmpegProbe 只能把它当 probe exit failure；试编码也可能误记失败 | 调用方取消统一抛 OCE 或明确 Canceled；超时与取消不同，不更新不兼容缓存；在已启动进程和启动前取消分别测 |
| R10 / P2 | DiagnosticReport.cs:18、AppRoot.cs:398 始终取 PlanSnapshot.CandidatePaths.FirstOrDefault；自动回退后的 CPU attempt 可能被展示为首个 NVIDIA 候选 | Attempt 持久化 ExecutedCandidateId/快照，详情与报告按实际路径读取；GPU 失败→CPU 成功报告不能仍写 GPU |
| R11 / P2 | WorkbenchStore.StartAsync:211、RetryAsync:319 把 PersistAsync 放在 `!wasPaused && ...` 条件里，原来暂停时跳过绑定落盘；watch 只更新内存。崩溃后的恢复路径受 job 状态和快照先后影响 | 无论暂停与否，先落盘卡片→job 绑定，再决定是否恢复队列；补“暂停入队→继续→崩溃→恢复”以及重复源文件测试。本轮为调用链风险，未做完整崩溃复现 |
| R12 / P2 | MediaCompatibility.ValidateVideoTranscode 用 SourceSnapshot.ColorClassification，parser 只按 PrimaryVideo 赋值；用户选定其他视频流时判断仍依据主轨。DV Remux 规则也用 PrimaryVideo | 按解析后选定视频轨逐轨分类/约束；多视频文件混合 SDR/HDR/DV 不能沿用一份全局分类。当前 UI 未完整暴露多视频选择，但请求接口已允许 |

## 扩展之前要补的架构与发布约束

这些是能力缺口/风险，不一概视为当前用户路径必然报错。

| ID | 风险与位置 | 实施要求 |
|---|---|---|
| A01 | JobScheduler 只按全局 in-flight 数量限制，未消费完整 ResourceNeeds；并发可设到 4，缺少逐 GPU、验证、CPU/Vulkan/字体预处理预算 | 建立资源租约；全局/CPU/GPU/验证槽固定获取顺序，失败回滚，取消释放。先不承诺 4 路 Main10 或 libplacebo 吞吐 |
| A02 | CapabilityService 固定 libx264/h264_*、8-bit 初始试跑；-filters/-encoders 名单不能证明 HDR 或字幕渲染链可运行 | 能力 key 包含 codec/profile/bitDepth/pixel format/filter graph family/设备/驱动/core；试跑应覆盖完整目标链，失败保留可读原因 |
| A03 | CoreLocator 可以在 manifest 缺失时接收裸 ffmpeg/ffprobe 并现场算 hash，还读取环境变量/CWD 搜索根 | 发布模式明确可信核心策略；固定安装包缺 manifest/缺 DLL 应失败关闭。开发 override 明示隔离，不能把“计算了 hash”当作“与可信清单匹配” |
| A04 | RequestVersion=1、PlanSchemaVersion=2 基本是标记，未见对应未知版本拒绝/逐版本迁移；workspace.v1 与 presets/journal 也要演进 | 新模型须提供 migration fixtures、future-version fail-closed、旧任务/预设/工作区兼容规则，不能改变枚举已有数值 |
| A05 | OutputPathService 只检查 free space >0；未知空间与确实无空间混合；Main10/中间 ASS/字体/RPU/哈希增加 IO。Release 注释说后续 sweep，但未见完整回收服务 | 分离 Unknown/Insufficient，估算 staging 预算；增加有所有权和 journal 保护的 orphan cleanup，禁止清理未解决提交或用户文件 |
| A06 | FfmpegProbe 的 `%+#1` 是一个 packet 预算，不保证得到目标轨首个解码帧，也不证明全片没有动态 HDR metadata | 分层、有预算的扩展 probe，多位置/目标轨/metadata 证据，保留 unknown 与 conflict，不把单次未发现当作不存在 |
| A07 | 单次提交可能整文件 SHA-256，UI 再探测，完整验证固定 10 分钟；4K 长片与网络盘会放大耗时，进度阶段不够解释 | 将验证/提交哈希作为可取消、有进度的阶段；预算按时长/路径适配并有上限，不使用无限等待；进度不能假到 100% |
| A08 | Inno 的 SOURCE.txt 只写 base revision，脏工作区构建难追溯；第三方许可复制允许 SilentlyContinue | 发布证据记录 commit + dirty diff hash + core bundle hash + 工具清单；缺必要许可/来源材料应打包失败；新增工具逐项记录版本、归档及依赖许可 |
| A09 | docs/12 等保留旧 M0 启动指令和 HDR/HEVC 不在首发的历史描述；旧性能/支持结论与新版代码有交叠 | 下一位 AI 从 13→14→15 启动；旧文字是历史，不能重新生成空壳、重新执行首次搭建，或据旧文档撤掉已有 HEVC/AV1 软件功能 |

## 建议处理顺序

1. R01/R02：结果真实性和提交失败保全。
2. R03/R05/R07/R09/R10/R11：请求选流、UI 事实、取消与持久化。
3. R04/R06/R08/R12 + A02/A04：结构化媒体证据、解析计划和验证契约。
4. 按 14 的 G0–G7 开放高级功能；每一步先小矩阵真样片，不一次宣称“全面 Dolby”。

本报告不声称穷尽全部缺陷，也不把未复现的攻击/断电场景写成已发生。下一位 AI 应先将高优先级复现转成失败测试，再修实现，并把本页 ID 与 PR/证据逐项关联。
