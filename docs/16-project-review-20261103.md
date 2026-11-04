# 16 · 项目复核与待办（2026-11-03）

复核日期：2026-11-03（Asia/Shanghai）。代码基线：`master` @ `3c517c4`（工作区干净），应用版本 **1.0.0.9**。
本轮为**只读静态复核 + Release 测试实跑**，不是渗透测试/完整安全审计，也未做拔盘/断电/长时间并发等故障注入。所有结论带代码位置（file:line），凡未验证处均标注“待验证”。

配套：此前的 [13 全项目审查](13-project-review.md)（R01–R12 / A01–A09）与 [G0 修复证据](evidence/G0-RELIABILITY.md)。本轮重点核对 13 的关闭状态并补充新发现。

## 结论

- 分层与关键安全/输出保护设计扎实，适合在当前架构上继续演进，**不建议重写**。
- 13 中标注已修的 **R01/R02/R03/R05/R07/R09/R10/R11 在当前代码中确认已落实**（逐条代码核对，见下）。
- **仍未关闭**：R04、R06、R08、R12，以及 A01–A09 中的多数（A03 安全项最值得先处理）。
- **本轮新增（已核实）**：**数据库损坏/被占用会导致启动崩溃、无降级（X01，P1）**；视频转码“指定音轨”全不选会静默输出无音轨（X02）；Remux 无效视频选择静默丢视频（D1）；恢复流程单条异常会中断整轮恢复（D3）；能力枚举失败被长期缓存（E1/E2）；能力探测身份进程未纳入 Job Object（E3）；遗留失效安装脚本（N02）；发布/许可证据偏弱；若干文档漂移。

测试实跑（Release，`dotnet test TransFlux.slnx -c Release --no-build`）：**438 通过 / 0 失败**（Domain 130、Application 119、Infrastructure 51、Engine 138）。

## 一、13 中“已修”项的复核（确认关闭）

| ID | 复核证据 | 结论 |
|---|---|---|
| R01 Published/Prepared 恢复不得假成功 | [RecoveryService.cs:23-44](../src/TransFlux.Application/Services/RecoveryService.cs#L23-L44) 两类 journal 都走 reconciler；[OutputCommitter.cs:121-202](../src/TransFlux.Infrastructure/OutputCommitter.cs#L121-L202) 校验存在/大小/内容哈希，缺失或身份不符转 NeedsAttention 并保留 journal | ✅ 已修 |
| R02 提交失败后不得删除唯一临时成果 | [JobScheduler.cs:926-936](../src/TransFlux.Application/Services/JobScheduler.cs#L926-L936) 提交失败直接保留 reservation/workdir/temp/journal 并转 NeedsAttention；`preserveAttemptArtifacts` 阻止 catch 清理（[:964-982](../src/TransFlux.Application/Services/JobScheduler.cs#L964-L982)） | ✅ 已修 |
| R03 选音轨不得隐式移除字幕 | [StreamSelectionResolver.cs](../src/TransFlux.Domain/Rules/StreamSelectionResolver.cs) v2 视频/音频/字幕三路独立，v1 保留旧语义；[SubtitlePolicy.cs:46-47](../src/TransFlux.Domain/Rules/SubtitlePolicy.cs#L46-L47) 统一走 resolver | ✅ 已修 |
| R05 字幕警告须按解析结果 | [WorkbenchRules.cs:28-49](../src/TransFlux.App/State/WorkbenchRules.cs#L28-L49)、[FfmpegPlanner.cs:665-720](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs#L665-L720) 按解析后字幕逐轨说明，去除无条件“字幕丢失” | ✅ 已修 |
| R07 音频提取单轨 | [FfmpegPlanner.cs:228-235](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs#L228-L235) 第二道守卫阻断多选；UI 单选 + `AUDIO_EXTRACT_SINGLE` | ✅ 已修 |
| R09 取消与超时区分 | [ProcessCapture.cs:52-85](../src/TransFlux.Engine.FFmpeg/Processes/ProcessCapture.cs#L52-L85) `CaptureResult.Canceled` 独立，调用方取消不记失败 | ✅ 已修 |
| R10 诊断报告按实际执行候选 | [DiagnosticReport.cs:63-70](../src/TransFlux.App/State/DiagnosticReport.cs#L63-L70) 用 `ExecutedCandidateId`，不再退化为 `CandidatePaths[0]` | ✅ 已修 |
| R11 暂停时入队/重试也先落盘绑定 | [WorkbenchStore.cs:194-216](../src/TransFlux.App/State/WorkbenchStore.cs#L194-L216)、[:311-325](../src/TransFlux.App/State/WorkbenchStore.cs#L311-L325) 先 `PauseQueue`→`PersistAsync`→再视 `wasPaused` 恢复 | ✅ 已修 |

## 二、仍未关闭（延续 13）

| ID / 级别 | 证据（file:line） | 问题与最小修复 |
|---|---|---|
| **R04 / P1** | [MediaCompatibility.cs:183](../src/TransFlux.Domain/Rules/MediaCompatibility.cs#L183) `DvProfile == 8 \|\| DvBlSignalCompatibilityId > 0` | DV **转封装**已实现（MKV stream copy），但准入过宽：profile 7+compat、未知组合可能被放行；[ExpectedOutput](../src/TransFlux.Domain/Plans/ValidatedPlan.cs) 无 RPU/EL/DV/HDR 断言，Verifier 无 DV 断言，退出 0/流数相同不能证明 RPU 保留 → 可能“假成功”。建议按 profile/layer/compat 分表并加真实 RPU 保留验证；未验证组合 fail-closed。 |
| **R06 / P2** | [FfmpegPlanner.cs:134-221](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs#L134-L221) 视频转码未 `-map` 附件；[:642-654](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs#L642-L654) `AttachmentStreamCount = 0` | MKV ASS/SSA 软字幕可复制但字体附件在**视频转码**路径丢失（Remux 路径 [AddAttachmentIdentity](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs#L424-L441) 已处理）。建议定义字体依赖保留策略并让 Expected 反映真实附件数。 |
| **R08 / P1** | [FfmpegVerifier.cs:263-302](../src/TransFlux.Engine.FFmpeg/Verifier/FfmpegVerifier.cs#L263-L302) 采样/全量解码均无 `-map`；[:79-178](../src/TransFlux.Engine.FFmpeg/Verifier/FfmpegVerifier.cs#L79-L178) Compare 只对主视频 + 音频 codec 做部分校验 | FFmpeg 默认只解码“最佳”视频+音频，**第二音轨/字幕轨损坏、同流数错误 codec、forced/default 反转可能不被发现**，存在“验证通过但输出错误”的假置信。建议逐条输出轨显式 `-map` 解码，并核对复制前后逐轨 codec/profile/声道/disposition。 |
| **R12 / P2** | [FfprobeJsonParser.cs:83](../src/TransFlux.Engine.FFmpeg/Probe/FfprobeJsonParser.cs#L83) 仅按 `PrimaryVideo` 计算 `ColorClassification`；[MediaCompatibility.cs:63](../src/TransFlux.Domain/Rules/MediaCompatibility.cs#L63)/[:161](../src/TransFlux.Domain/Rules/MediaCompatibility.cs#L161) 用它做全局判定 | 用户选定非主视频轨（请求接口允许，UI 尚未完整暴露）时，SDR/HDR/DV 判定仍按主轨，可能误转码/误放行。建议按“解析后选定视频轨”逐轨分类。 |
| **A01 / P2** | JobScheduler 仅按在途数量限流 | 未消费完整 `ResourceNeeds`（逐 GPU/验证/CPU/Vulkan/字体预算），并发上限偏乐观。建议资源租约，固定获取顺序 + 回滚。 |
| **A02 / P2** | [FfmpegEngineCapabilities.cs](../src/TransFlux.Engine.FFmpeg/Capabilities/FfmpegEngineCapabilities.cs) 能力 key（encoders/filters/pix_fmts）不区分 profile/bitDepth/filter-graph/驱动/核心版本 | 名单存在不等于目标链路可跑。建议能力 key 含 profile/位深/驱动/core，试跑覆盖完整目标链。 |
| **A03 / P1（安全）** | [FfmpegCoreLocator.cs:105-114](../src/TransFlux.Engine.FFmpeg/Core/FfmpegCoreLocator.cs#L105-L114) 无 manifest 时对任意 `ffmpeg.exe` 现场算 hash 即返回；[:183-185](../src/TransFlux.Engine.FFmpeg/Core/FfmpegCoreLocator.cs#L183-L185) manifest 里 sha 为空时 `VerifySha` 直接放行；[:52-73](../src/TransFlux.Engine.FFmpeg/Core/FfmpegCoreLocator.cs#L52-L73) 搜索根含 `TRANSFLUX_FFMPEG_DIR` 与 CWD 上溯 | 发布模式应将“计算了 hash”与“匹配可信清单”区分：缺 manifest/缺 hash 必须 fail-closed；开发 override 明确隔离。**（已有改进：`ResolveRelative` 的目录包含校验关闭了 manifest 指向子目录外二进制的问题，[119-181 行](../src/TransFlux.Engine.FFmpeg/Core/FfmpegCoreLocator.cs#L119-L181)）** |
| **A04 / P2** | [SqliteJobStore.cs:99-130](../src/TransFlux.Infrastructure/SqliteJobStore.cs#L99-L130)、[:1083-1088](../src/TransFlux.Infrastructure/SqliteJobStore.cs#L1083-L1088) | 有 schema_migrations + checksum + 迁移前备份，但**无未来版本拒绝**、也不校验已应用迁移的 checksum；workspace/preset/journal 版本演进同样缺 fixture。建议未来版本 fail-closed + 迁移 fixture。 |
| **A05 / P2** | [OutputPathService.cs:40-45](../src/TransFlux.Infrastructure/OutputPathService.cs#L40-L45) 仅 `free <= 0`；[:95-106](../src/TransFlux.Infrastructure/OutputPathService.cs#L95-L106) 工作目录删除为 best-effort | 无法区分“未知空间”和“确实无空间”；**无孤儿工作目录/预留清理服务**（全仓无 sweep/orphan 实现，grep 0 命中），注释所称“later sweep”未落地。建议分离 Unknown/Insufficient，并加受 journal 保护的 orphan cleanup。 |
| **A06 / P2** | FfmpegProbe 单 packet 预算 | 单次探测不能证明全片无动态 HDR metadata。建议多位置/目标轨预算化探测，保留 unknown。 |
| **A07 / P2** | [FfmpegVerifier.cs:265-273](../src/TransFlux.Engine.FFmpeg/Verifier/FfmpegVerifier.cs#L265-L273) 全解码固定 10 分钟；[OutputCommitter.cs:339-346](../src/TransFlux.Infrastructure/OutputCommitter.cs#L339-L346) 整文件 SHA | 4K 长片/网络盘耗时放大且进度不可取消；建议分阶段可取消 + 按时长/路径预算化。 |
| **A08 / P2** | [tools/package-exe.ps1](../tools/package-exe.ps1) `SOURCE.txt` 仅 `git rev-parse HEAD`；FFmpeg 许可 `Copy-Item -ErrorAction SilentlyContinue` | 脏工作区构建难追溯；缺许可/来源材料不 fatal。建议记录 commit + dirty diff hash + core bundle hash + 工具清单，缺材料则打包失败。 |
| **A09 / P3** | docs/12 等保留旧 M0/HDR 历史描述 | 文档历史漂移，需以 13→14→15 为准，避免据旧文重搭骨架。 |

## 三、本轮新增发现

| ID / 级别 | 证据 | 说明与建议 |
|---|---|---|
| **N01 / P2** | [FfmpegEngineCapabilities.cs:35-52](../src/TransFlux.Engine.FFmpeg/Capabilities/FfmpegEngineCapabilities.cs#L35-L52) `GetIdentity` 用裸 `Process.Start` + 同步 `ReadToEnd()`，未走 `ManagedProcess`/Job Object | 与 docs/08“所有子进程纳入 Job Object”不符：崩溃时 `-version` 进程可能成孤儿；输出无上限，理论上有管道死锁风险（`-version` 输出很小，实际风险低）。建议改用 `ProcessCapture.RunAsync`。 |
| **N02 / P2** | [scripts/build-installer.ps1](../scripts/build-installer.ps1) 引用不存在的 `artifacts/installer/transflux.iss` 与 `tools/ffmpeg/ffmpeg-master-latest-win64-gpl/bin`，默认 `Version 1.0.0.1`；[.gitignore](../.gitignore) 安装包注释仍指向它 | 与唯一可用的 [tools/package-exe.ps1](../tools/package-exe.ps1) + [packaging/TransFlux.iss](../packaging/TransFlux.iss) 分叉，**误用会产出错误/失败安装包或旧版本号**。建议删除或显著标注弃用。 |
| **N03 / P3** | 全仓无日志写入、无 thumbnail、无保留清理实现（grep 0）；[ServicesContracts.cs:16](../src/TransFlux.Application/Contracts/ServicesContracts.cs#L16) `LogRetentionDays=14` 为死配置 | docs/08 的“日志 14 天/100MB、历史 1000 项/90 天、缩略图 256MB”均未实现。建议实现或从文档移除，避免安全/隐私承诺落空。 |
| **N04 / P3** | [TransFlux.App.csproj](../src/TransFlux.App/TransFlux.App.csproj) Release 默认 `PublishTrimmed=true`/`PublishReadyToRun=true` | 仅 `package-exe.ps1` 显式 `-p:PublishTrimmed=false` 覆盖；直接 `dotnet publish -c Release` 可能因 WinUI 不兼容裁剪而失败。建议在 csproj 固定。 |
| **N05 / P3** | [docs/evidence/INSTALLER.md](evidence/INSTALLER.md)、[IMPLEMENTATION-STATUS.md](evidence/IMPLEMENTATION-STATUS.md) 仍写“当前 1.0.0.8” | 实际已发布 1.0.0.9，发布时需同步版本与证据。 |
| **N06 / P3** | [04-architecture.md](04-architecture.md) 提到 `Directory.Packages.props` 精确版本，仓库无此文件 | 包版本内联在各 csproj；文档与实现不符。 |
| **N07 / P3** | [FfmpegPlanner.cs:369-373](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs#L369-L373) 仅 `-protocol_whitelist file` | docs/08 声称拒绝 playlist/concat 等输入，但只限协议、未显式禁用相关 demuxer。本地自选文件风险有限，建议按首发范围显式拒绝并加测试。 |

## 三·补 · 并行深查补充（Domain/Application/Engine 复核确认）

对 Domain/Application 与 Engine/Hardware 做了第二轮逐文件深查。以下条目均已对照源码确认（除标注“待验证”）。

### Domain / Application
| ID / 级别 | 证据 | 说明与最小修复 |
|---|---|---|
| **D1 / P2** | [MediaCompatibility.cs:119-163](../src/TransFlux.Domain/Rules/MediaCompatibility.cs#L119-L163) ValidateRemux 无 `VIDEO_SELECTION`/空选择阻断；[SelectRemuxStreams:204-212](../src/TransFlux.Domain/Rules/MediaCompatibility.cs#L204-L212) 仅当 `selection.Video` 非空才收视频 | v2 Remux 若视频索引无效但仍有音轨被选，输出**静默缺视频**却成功；转码路径有阻断而 Remux 没有。修复：v2 显式选择解析为空时 Block。 |
| **D2 / P2** | [RequestTypes.cs:178](../src/TransFlux.Domain/Plans/RequestTypes.cs#L178) `RequestVersion = 1` 默认值 | 文档称“新请求恒为 2”，但默认仍是 1；非 App 调用方/反序列化缺字段会落到 v1 旧语义（选音轨移除字幕，R03 回归面）。修复：默认改 2 或加构造守卫。 |
| **D3 / P2** | [RecoveryService.cs:24-44](../src/TransFlux.Application/Services/RecoveryService.cs#L24-L44) 循环内 `EnsureOutputAsync/DeleteJournalAsync` 无逐条 try/catch；[:91](../src/TransFlux.Application/Services/RecoveryService.cs#L91) MarkActiveJobsInterrupted 在循环之后 | 任一条 journal 的 DB 操作抛异常会中断整个恢复：剩余 journal 不处理、活动任务不置 Interrupted，任务永久“运行中”且无法重试。修复：逐条 try/catch→NeedsAttention 并继续，最后仍执行 MarkActiveJobsInterrupted。 |
| **D4 / P3** | [RecoveryService.cs:31-44](../src/TransFlux.Application/Services/RecoveryService.cs#L31-L44) 成功 reconcile 只 EnsureOutput+删 journal，不改 job 状态；[:49-88](../src/TransFlux.Application/Services/RecoveryService.cs#L49-L88) 第二循环才置 Succeeded；[:157-188](../src/TransFlux.Application/Services/RecoveryService.cs#L157-L188) 无 `IOutputRecordVerifier` 即失败关闭 | 生产 OutputCommitter 同时实现三接口故当前路径 OK；若某 committer 未实现 IOutputRecordVerifier，已核验发布会被误判 NeedsAttention（契约脆弱）。修复：reconcile 成功即置 Succeeded。 |
| **D5 / P3** | [JobStateMachine.cs:8-15](../src/TransFlux.Domain/Jobs/JobStateMachine.cs#L8-L15) Interrupted 仅来自 Running/Verifying/Committing；[SqliteJobStore.cs:31-35](../src/TransFlux.Infrastructure/SqliteJobStore.cs#L31-L35) ActiveStates 含 Preflighting/Recovering/CancelRequested，恢复时直接写 Interrupted 绕过状态机 | CancelRequested 崩溃后变 Interrupted 而非 Canceled；Preflighting/Recovering→Interrupted 在纯状态机非法。建议补边或明确“恢复以 store 为准”并加测试。 |

### Engine / Hardware
| ID / 级别 | 证据 | 说明与最小修复 |
|---|---|---|
| **E1 / P2** | [CapabilityService.cs:36-37](../src/TransFlux.Hardware.Windows/CapabilityService.cs#L36-L37) 枚举异常 `catch { adapters=[]; }`；[:54](../src/TransFlux.Hardware.Windows/CapabilityService.cs#L54) TTL 判定看 `DeviceId is not null && !Available`；[:73-77](../src/TransFlux.Hardware.Windows/CapabilityService.cs#L73-L77) 无匹配即 Unavailable(DeviceId=null) | DXGI 枚举瞬时失败会被当作“本机无显卡”，因 DeviceId=null 不触发 10 分钟失败 TTL，反而按 7 天正常 TTL 缓存 → 硬编长期不可用。修复：枚举失败不写缓存/单独标记。 |
| **E2 / P2** | [FfmpegEngineCapabilities.cs:171-178](../src/TransFlux.Engine.FFmpeg/Capabilities/FfmpegEngineCapabilities.cs#L171-L178)/[:237-252](../src/TransFlux.Engine.FFmpeg/Capabilities/FfmpegEngineCapabilities.cs#L237-L252) RunListAsync 不判 TimedOut/ExitCode；[:81-107](../src/TransFlux.Engine.FFmpeg/Capabilities/FfmpegEngineCapabilities.cs#L81-L107) `_cached` 实例终身有效 | 一次 -encoders/-filters 超时或非零退出会产生空集合并被永久缓存，Planner 误判无硬编/无过滤器直到重启。修复：枚举失败抛错且不缓存。 |
| **E3 / P2** | [FfmpegEngineCapabilities.cs:35-68](../src/TransFlux.Engine.FFmpeg/Capabilities/FfmpegEngineCapabilities.cs#L35-L68) 裸 `Process.Start` + 先 `ReadToEnd()` 后 `WaitForExit(5000)`，未走 `ManagedProcess` | `ReadToEnd` 先阻塞到 EOF，5000ms 超时实际不生效；崩溃窗口内 -version 进程可残留（即上文 N01 的代码级细节，合并看待）。修复：改用 `ProcessCapture.RunAsync`。 |
| **E4 / P2** | [ProcessCapture.cs:58](../src/TransFlux.Engine.FFmpeg/Processes/ProcessCapture.cs#L58) kill 后 `WaitForExitAsync(CancellationToken.None)` 无超时；[FfmpegRunner.cs](../src/TransFlux.Engine.FFmpeg/Runner/FfmpegRunner.cs) 全程无总超时、输出泵无有界排空 | 进程树无法及时终止或子进程继承 stdout 句柄时可永久挂起，占住调度槽。修复：kill 后等待加超时，Runner 复用有界排空 + 总时长/停滞上限。 |
| **E6 / P2** | [FfmpegPlanner.cs:394-395](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs#L394-L395) 非 Strip 时 `-map_chapters 0` 原样复制；[:361-382](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs#L361-L382) trim 用 `-ss/-t` | 带章节源裁剪转码后，章节时间戳仍指原时间轴，与内容错位。修复：trim 时 `-map_chapters -1` 或重基准 + 警告。 |
| **E7 / P2** | [FfmpegPlanner.cs:410-422](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs#L410-L422) AddOneStreamIdentity 只保留 default/forced，其余显式写 `-disposition ... 0` | hearing_impaired/visual_impaired/comment/lyrics 等无障碍/评论标记被静默清空。修复：完整保留源 disposition，或文档明确并警告。 |
| **E9 / P2（待验证）** | [FfprobeJsonParser.cs:113](../src/TransFlux.Engine.FFmpeg/Probe/FfprobeJsonParser.cs#L113) `Int("bits_per_raw_sample") ?? Int("bits_per_sample") ?? InferBitDepth(pix)` | 若 ffprobe 返回 `"0"`，0 非 null 会压过像素格式推断，10-bit 可能被误判 8-bit SDR 放行。需真实 10-bit 样片核实；修复：0 视为无效回退推断。 |
| **E8 / P3** | [FfmpegPlanner.cs:747](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs#L747) `schema = 1` 与 [:759](../src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs#L759) `PlanSchemaVersion = 2` | 计划哈希内的 schema 字面量与版本标记不一致，迁移/审计易混淆（不影响执行）。修复：统一常量。 |
| **E10 / P3** | `tools/ffmpeg/bin` 同时存在两套运行库：清单仅 pin `avcodec-62/avfilter-11/...`，但目录还有 `avcodec-63/avfilter-12/...` | 发布脚本只复制清单内文件，安装包不受影响；但开发树存在未 pin 的第二套运行库，影响 A03 完整性口径。建议清理或全部纳入 manifest。 |
| **E5 / P3** | [JobObjectHandle.cs:39-40](../src/TransFlux.Engine.FFmpeg/Processes/JobObjectHandle.cs#L39-L40) SetInformationJobObject 失败抛异常时 `_handle` 未 Close | 极小概率句柄泄漏。修复：构造失败路径 CloseHandle 后再抛。 |

### Infrastructure / App
| ID / 级别 | 证据 | 说明与最小修复 |
|---|---|---|
| **X01 / P1** | [TransFluxAppHost.cs:31-33](../src/TransFlux.App/Hosting/TransFluxAppHost.cs#L31-L33) `store.InitializeAsync(...).GetAwaiter().GetResult()` 无 try/catch；[App.cs:9](../src/TransFlux.App/App.cs#L9) 在 `ReactorApp.Run` 之前调用 | **数据库损坏/被杀软或他进程占用/磁盘满 → 启动直接崩溃，用户看不到任何错误，也无 .bak 自动恢复。** 建议捕获 `SqliteException`：尝试 .bak 恢复或改名重建空库并提示，启动降级为只读/空库。 |
| **X02 / P2** | [OutputTargets.cs:70-74](../src/TransFlux.App/State/OutputTargets.cs#L70-L74) 空索引仍设 `Explicit`；[StreamSelectionResolver.cs:68-75](../src/TransFlux.Domain/Rules/StreamSelectionResolver.cs#L68-L75) 返回空且无警告；[MediaCompatibility.cs:54-79](../src/TransFlux.Domain/Rules/MediaCompatibility.cs#L54-L79) 视频转码无音轨选择校验 | 视频转码“指定音轨”全部取消勾选 → 输出**静默无音轨、无告警**，与失败无异样地开始。建议 UI 禁止取消最后一条或映射为 None 并提示；域层 Explicit.Count==0 时 Block/Warn。 |
| **X03 / P2** | [WorkbenchRules.cs:11](../src/TransFlux.App/State/WorkbenchRules.cs#L11) CanRemove 含 NeedsAttention；[WorkbenchStore.cs:335-349](../src/TransFlux.App/State/WorkbenchStore.cs#L335-L349) RemoveAsync 仅删内存卡片并持久化；[JobScheduler.cs:926-936](../src/TransFlux.Application/Services/JobScheduler.cs#L926-L936) 保留 reservation/workdir/temp/journal | 移除 NeedsAttention 卡片后，job 行与预留锁/`.transflux-work` 残留磁盘且无 sweep（A05），重启不再恢复卡片，用户难定位唯一暂存输出。建议移除时提示或提供“放弃并清理暂存（不动用户文件）”。 |
| **X06 / P2** | [WorkbenchStore.cs:74-131](../src/TransFlux.App/State/WorkbenchStore.cs#L74-L131) catch 只 SetNotice 不重订阅；Start 见 WatchTask 非空即不再启动 | 事件流一次异常后永久停止刷新，UI 可能卡在“正在恢复…”。建议带退避重订阅 + 可重试入口。 |
| **X07 / P2** | [WorkspacePersistence.cs:9-13](../src/TransFlux.Infrastructure/WorkspacePersistence.cs#L9-L13) 直接反序列化；[WorkbenchStore.cs:65-67](../src/TransFlux.App/State/WorkbenchStore.cs#L65-L67)/[:94-126](../src/TransFlux.App/State/WorkbenchStore.cs#L94-L126) 损坏时丢弃终态卡片并用降级结果覆盖 | workspace JSON 损坏/未来 schema → 已完成卡片消失且原文件被覆盖无备份。建议备份坏文件、逐条容错、不覆盖原文件。 |
| **X08 / P3** | [OutputCommitter.cs:348-358](../src/TransFlux.Infrastructure/OutputCommitter.cs#L348-L358) SaveOutputAsync 写 `VerificationSummary = string.Empty`；[JobScheduler.cs:1160-1174](../src/TransFlux.Application/Services/JobScheduler.cs#L1160-L1174) Ensure 仅在不存在时补写 → 永不覆盖 | 重启后技术详情“验证摘要”为空（不影响文件正确性，但丢失验证证据）。建议 CommitRequest 携带摘要并由 committer 落库，或改 upsert。 |
| **X04 / P2（待验证）** | [DebouncedAction.cs:17-33](../src/TransFlux.App/State/DebouncedAction.cs#L17-L33) Schedule 只取消延时、不串行化已执行 action | 快速连续改参数时旧 action 可能晚于新 action 完成而回写旧值。建议 SemaphoreSlim 串行化或版本号丢弃过期。 |
| **X05 / P2（待验证）** | [HistoryDialog.cs:41-55](../src/TransFlux.App/Components/Dialogs/HistoryDialog.cs#L41-L55)、[SettingsDialog.cs:50-67](../src/TransFlux.App/Components/Dialogs/SettingsDialog.cs#L50-L67) 在 `Task.Run` 内调 setter；[AppRoot.cs:43-45](../src/TransFlux.App/Components/Shell/AppRoot.cs#L43-L45) 对 store 变更做了 DispatcherQueue | 对话框后台线程直接 setState 可能触发跨线程错误/渲染崩溃或状态不刷新（无法动态验证）。建议回 UI 线程再 setState。 |
| **X09 / P3** | [WorkbenchStore.cs:273-281](../src/TransFlux.App/State/WorkbenchStore.cs#L273-L281) EditQueuedAsync→RemoveQueuedAsync；[JobScheduler.cs:524-541](../src/TransFlux.Application/Services/JobScheduler.cs#L524-L541) Queued→Canceled；GetHistoryAsync 纳入 Canceled | “移出队列并编辑”把从未运行的任务写成 Canceled 历史噪音。建议提供不入历史的移除路径。 |
| **X10–X15 / P3** | 见评审明细 | 其余一致性/清理项：源=目标同文件检测已实现但全仓未调用（X10）、Failed journal 行永久累积（X11）、内置预设升级不生效（X12）、诊断脱敏不覆盖裸文件名/未开始状态显示“排队中”（X13）、恢复用裸 SQL 置 Succeeded 绕过状态机（X14）、v1→v2 UI 迁移假设旧索引全为音轨（X15，待验证）。 |

## 四、安全小结

- 正向：src 内**无网络/遥测**（grep `HttpClient/WebRequest/https://`、`Telemetry` 均 0）；已确认**无 shell/命令注入路径**（用户文本不进 filter，颜色有白名单，路径单参数替换）；子进程 `ArgumentList` + `UseShellExecute=false` + Job Object（除 E3/N01）；SQL **全参数化**；输出名/路径清洗（[OutputNaming.cs](../src/TransFlux.Domain/Rules/OutputNaming.cs)）；提交用不覆盖 rename + 内容哈希身份对账（[OutputCommitter.cs](../src/TransFlux.Infrastructure/OutputCommitter.cs)）；诊断导出默认脱敏路径（[DiagnosticReport.cs:72-89](../src/TransFlux.App/State/DiagnosticReport.cs#L72-L89)）。
- 主要安全待办：A03（核心完整性 fail-closed）、N01（进程托管）、N07（输入协议/格式收敛）。N02 属发布完整性风险。

## 五、测试与覆盖

- 438/438 通过。缺口：无 UI 自动化 / 无故障注入矩阵（拔盘、断电、网络盘、rename 后崩溃）/ 无 DV·HDR·字幕烧录真样片验收。13 与 docs/09 已把前两类列为发布门禁，需补。

## 六、建议处理顺序

1. **可信与结果真实性（最优先）**：A03 核心 fail-closed、R04 DV 准入收紧+保留验证、R08 逐轨 `-map`+ `-xerror` 验证。
2. **启动/静默错误与恢复稳健性**：X01 启动 DB 故障降级（P1）、X02 静默丢音轨、D1 Remux 空视频阻断、D2 RequestVersion 默认改 2、D3/D4/D5 恢复逐条隔离与状态一致、E1/E2 能力缓存误判。
3. **保真与一致性**：R06 附件/字体、R12 逐轨色彩、E6 章节重基准、E7 disposition 保留、E9 位深推断。
4. **发布/生命周期/持久化**：A04 迁移 fail-closed、A05 orphan sweep（配合 X03/X07 清理滞留暂存）、A08 发布清单/许可门禁、E4 进程挂起、X06 watch 重订阅、X08 验证摘要落库、N02 删旧脚本。
5. **文档同步**：A09、N03–N06（版本、日志/缩略图承诺、依赖锁文件说明）。

## 七、第一轮修复状态（2026-11-03 执行；以下为当时快照）

本轮按上述优先级修复了全部 P1 与一批 P2，并补了 19 个回归测试（当时记录 438 → **457 通过 / 0 失败**），Release 构建 0 警告/0 错误，真实 E2E **PASS**（2.7s，源文件不变）。第二轮复核后的最终数字与新增修复见第八节。

| 项 | 状态 | 落地内容 |
|---|---|---|
| X01 启动 DB 崩溃 | ✅ 已修 | `SqliteJobStore.InitializeWithRecoveryAsync`：损坏库移旁 + `.bak` 恢复；`TransFluxAppHost` 再加临时库兜底，数据库问题不再阻止窗口打开。测试：损坏库/备份恢复。 |
| A03 核心完整性 | ✅ 已修 | 无 manifest、缺 sha、畸形 manifest 一律 fail-closed；显式目录/环境变量为权威、不再回退；移除 CWD 搜索；保留显式 `allowUnverified` 开发口。测试 4 项。 |
| R08 逐轨验证 | ✅ 主要修复 | 采样/全量解码按 `0:v:N`/`0:a:N` 显式映射全部视频与音频并加 `-xerror`；视频 codec 逐轨校验。剩余：声道布局/profile/disposition 断言仍未建模。测试：映射单测 + 现有 2 音轨回归。 |
| R04 DV 准入 | ✅ 部分修复 | 收紧为仅 `Profile 8 且 BL compatibility id > 0`；profile 7+compat 等组合阻断。**RPU/EL 保留验证仍未实现**（需真实 DV 样片与 ExpectedOutput 扩展）。测试 3 项。 |
| X02 静默丢音轨 | ✅ 已修 | 视频转码 v2 下 `Explicit` 空音轨选择直接 Block；Remux 同样阻断。测试 2 项。 |
| D1 Remux 丢视频 | ✅ 已修 | v2 Remux 视频解析为空时 Block `VIDEO_SELECTION`。测试 2 项。 |
| D3 恢复中断 | ✅ 已修 | 每条 journal 独立 try/catch→NeedsAttention 并继续，其余 journal 与活动任务仍处理。测试 1 项。 |
| E1/E2 能力缓存 | ✅ 已修 | DXGI 枚举失败不再读写缓存；能力枚举超时/非零退出抛错且不缓存空结果。测试 1 项。 |
| E3 身份进程 | ✅ 已修 | `GetIdentity` 改用 `ProcessCapture`（Job Object + 真超时 + 有界输出）。 |
| A04 迁移 fail-closed | ✅ 部分修复 | 拒绝高于本构建的 schema 版本；校验已应用迁移 checksum。workspace/preset 版本化仍未做。测试 2 项。 |
| X08 验证摘要 | ✅ 已修 | `CommitRequest.VerificationSummary` 贯通到 `OutputRecord`；恢复路径写“Recovered from commit journal.”。测试断言。 |
| E4 进程挂起 | ⚠️ 部分修复 | kill 后等待加 10s 上限；Runner 总时长/停滞检测仍未加。 |
| R06 字体附件 | ✅ 已修 | MKV 视频转码映射全部附件、`-c:t copy`、写 filename/mimetype；ExpectedOutput 附件数与总流数同步；非 MKV 给警告。测试：真实 ttf 附件转码。 |
| N02 旧安装脚本 | ✅ 已修 | `scripts/build-installer.ps1` 改为显式报错弃用（指向 `tools/package-exe.ps1`），`.gitignore` 注释同步。 |

**第一轮结束时仍未处理**：D2、D4/D5、X03/X04/X05/X06/X07、A01/A02/A05/A06/A07/A08、R12、E6/E7/E8/E9/E10，以及 R04/R08 的验证深度补强。其中 D2/D4/X03/X06/X07/E7 等已在第二轮（第八节）修复；其余见第八节末尾的风险清单。

## 八、第二轮复核与修复（2026-11-03 执行）

本节记录对第七节修复工作区的再次检查与修复。基线：第七节完成后的未提交工作区。第二轮结束时验证：`dotnet build TransFlux.slnx -c Release` **0 警告 / 0 错误**；`dotnet test TransFlux.slnx -c Release` **485 通过 / 0 失败**（Domain 140 / Application 127 / Infrastructure 59 / Engine 159）；真实 E2E **PASS**（3.4s，源文件不变）。第三轮后的最终数字见第九节。

| 项 | 状态 | 落地内容与证据 |
|---|---|---|
| **N04 Release 构建失败** | ✅ 已修 | `dotnet build -c Release` 曾因 `PublishTrimmed=true` + 框架依赖构建报 NETSDK1102；`TransFlux.App.csproj` 现固定 `PublishTrimmed=false`，`PublishReadyToRun` 仅在 `SelfContained=true` 时开启。完整 Release 构建 0 警告/0 错误。 |
| **D2 RequestVersion 兼容** | ✅ 已修 | `ConversionRequest` 参数化构造默认 2，`[JsonConstructor] ConversionRequest(int requestVersion = 1)` 使缺少 `requestVersion` 的旧 JSON 仍按 v1 解析；`LegacyRequestRemainsDisplayable...` 回归通过。 |
| **D4 恢复成功未置 Succeeded** | ✅ 已修 | `RecoveryService.MarkSucceededAsync`：journal 对账成功后先 CAS 完成 job，再标记 attempt；仅 Committing/NeedsAttention 可升级，终态不覆盖。新增 ReconcilerOnly / 非 Committing 测试。 |
| **X03 NeedsAttention 移除残留** | ✅ 已修 | `IJobService.AbandonAsync`：NeedsAttention→Canceled CAS 先行，随后释放预留锁、删除 `.transflux-work` attempt 目录、尽力删除 journal；绝不删用户最终文件。`WorkbenchStore.RemoveAsync` 失败保留卡片；放弃项不可撤销。 |
| **X07 workspace 损坏丢数据** | ✅ 已修 | `WorkspacePersistence.LoadAsync` 捕获 `JsonException/NotSupportedException`，将原始值备份到 `workspace.v1.corrupt-<timestamp>` 并返回空；后续保存继续写主键。`_restoreFailed` 及其保存抑制已删除。 |
| **X06 watch 异常后停更** | ✅ 已修 | 重订阅循环保留 1s 退避；修复每次重试重复 `SetNotice` 的通知风暴，改为每次中断一条通知。新增 flaky watch 测试。 |
| **R08 验证深度** | ✅ 主要修复 | `ExpectedOutput.TrackSignatures`（按 `-map` 顺序校验 kind/codec/声道/default/forced）+ `FfmpegVerifier.CheckTrackSignatures`；真实输出声道篡改测试。profile/bit-depth 逐轨断言仍未建模。 |
| **R04 DV 保留** | ✅ 部分修复 | `FfprobeJsonParser` 解析 DOVI `rpu/el/bl_present_flag`；`ExpectedOutput.DolbyVision` + `CheckDolbyVision` 校验 profile/BL compat/RPU 保留。**仍无真实 DV/RPU 样片，属合成 JSON/plan 级验证。** |
| **E7 disposition 丢失** | ✅ 已修 | `StreamDescriptor` 补齐 16 个 disposition 标志；`FfprobeJsonParser` 全量解析；`FfmpegPlanner.AddOneStreamIdentity` 重建完整掩码；真实 MKV 转码 hearing_impaired/comment/visual_impaired 保留测试。 |
| **X11 Failed journal 累积** | ✅ 已修 | `GetOpenJournalsAsync` 纳入 Failed 行；`OutputCommitter.ReconcileAsync` 关闭该行；契约/测试同步。 |
| **N03/N05/N06 文档漂移** | ✅ 已修 | docs/08 标注日志/缩略图保留尚未实现；INSTALLER/IMPLEMENTATION-STATUS 标注源码 1.0.0.9、最后安装包证据 1.0.0.8；docs/04 移除不存在的 `Directory.Packages.props` 说明。 |
| **审计补充：无 A/V 输出的 Remux 误判失败** | ✅ 已修 | `FfmpegVerifier.SampleDecodeAsync` 在无视频/音频流时直接跳过解码（先检查取消令牌），避免有效字幕/附件-only MKV 被 ffmpeg `-map` 空集报 `-22`；新增单测。 |
| **审计补充：SQLITE_BUSY 被误当损坏** | ✅ 已修 | `InitializeWithRecoveryAsync` 仅对 `SQLITE_CORRUPT/NOTADB`（11/26）执行移旁恢复；锁/占用不再移动活动数据库。新增 FileShare.None 锁定库测试。 |
| **审计补充：Failed journal 过早删除** | ✅ 已修 | `OutputCommitter` 提交失败保留 `Failed` journal，由下次启动 recovery 关闭，消除 job 仍 Committing 且无 journal 的崩溃窗口；测试改为断言 Failed 行保留。 |
| **审计补充：恢复/工作台细节** | ✅ 已修 | `MarkSucceededAsync` 增加 active attempt 归属检查；`WorkbenchStore` 处理 `Removed` 事件清除陈旧行；`AddDecodeMaps` 改用全局流索引避免 attached-pic 序号错位；disposition 用户提示改为按容器能力表述。 |

**第二轮仍保留的风险/未处理**：A01 资源租约、A02 能力 key 深化、A05 orphan sweep、A06 多位置 HDR 探测、A07 验证超时预算化、A08 发布材料进一步自动化、E4 Runner 总时长/停滞上限、E10 开发树双运行库、N07 demuxer 收敛、A09 历史文档；R04 真实 DV 样片与 R08 profile/位深逐轨断言待补。

## 九、第三轮复核与修复（2026-11-04 执行）

基线：第八节完成后的未提交工作区。最终验证：`dotnet build TransFlux.slnx -c Release` **0 警告 / 0 错误**；`dotnet test TransFlux.slnx -c Release` **514 通过 / 0 失败**（Domain 154 / Application 127 / Infrastructure 65 / Engine 168）；真实 E2E **PASS**（3.5s，源文件不变）。

| 项 | 状态 | 落地内容与证据 |
|---|---|---|
| **E4 Runner 总时长/停滞** | ✅ 已修 | 新增 `RunnerLimits`（默认 24h 总上限 / 10min 无进度停滞 / 1s 轮询）与 `FfmpegRunner(core, limits)`；每次 `-progress` 刷新心跳，watchdog 先记录原因再杀进程树，返回 `run.timeout`/`run.stalled` 而非取消；杀死后 stdout/stderr 泵等待有界（10s）并可取消。RunnerLimitTests 5 项真实 ffmpeg 测试。 |
| **A05 孤儿暂存清理** | ✅ 已修 | 新增 `OrphanCleanupService`：启动时对 24h 前、非活动且无开放 journal 的终态任务清理 `.transflux-work/<token>` 和 `.transflux-reserve`；共享 `OutputStagingLayout` 严格校验目录布局，`OutputPathService.DeleteWorkDirectory` 增加防御性拒绝，绝不删除用户最终文件。启动接线在 RecoveryService 后、调度器前，失败不影响启动。测试 6 项。 |
| **R04 DV EL/BL 保留** | ✅ 部分修复 | `DolbyVisionExpectation.ElRequired/BlRequired` 来自源 `DvElPresent/DvBlPresent`，Verifier 检查输出丢失 EL/BL 标志；新增 lost-EL/lost-BL 测试。真实 DV/RPU 样片仍未验证。 |
| **N07 输入格式收敛** | ✅ 已修 | `MediaCompatibility.Validate` 对 `concat/ffconcat/hls/applehttp/dash/image2/image2pipe` 的 `format_name` 返回阻塞 `INPUT_FORMAT`；正常单文件容器不受影响，含真实 HLS manifest 探测测试。 |
| **A09 历史文档漂移** | ✅ 已修 | docs/12、docs/15 的“当前版本 1.0.0.8”改为历史版本并指向 docs/16；docs/04 与 docs/08 此前的同步保持。 |

**第三轮仍保留的风险/未处理**：A01 资源租约、A02 能力 key 深化、A06 多位置 HDR 探测、A07 验证超时预算化、A08 发布材料进一步自动化、E10 开发树双运行库；R04 真实 DV/RPU 样片验收、R08 profile/位深逐轨断言。A05 的清理仅覆盖 `.transflux-work` 标准布局，旧式/损坏路径会保留目录（fail-safe）；orphan sweep 的年龄基于 `job.UpdatedUtc`。

## 十、第四轮复核与修复（2026-11-04 执行）

基线：第九节完成后的未提交工作区。最终验证：`dotnet build TransFlux.slnx -c Release` **0 警告 / 0 错误**；`dotnet test TransFlux.slnx -c Release` **527 通过 / 0 失败**（Domain 161 / Application 133 / Infrastructure 65 / Engine 168）；真实 E2E **PASS**（3.4s，源文件不变）。

| 项 | 状态 | 落地内容与证据 |
|---|---|---|
| **P2 “移出队列并编辑”Removed 竞态** | ✅ 已修 | `WorkbenchStore.EditQueuedAsync` 先捕获 jobId 并把 draft 的 JobId 置空，再调用 `RemoveQueuedAsync`；失败/异常恢复原卡片。新增强制 Removed 事件先到达的回归测试。 |
| **P2 NeedsAttention 一键移除破坏暂存** | ✅ 已修 | `WorkbenchRules.CanRemove` 排除 NeedsAttention，新增 `CanAbandon`；`TaskRow` 增加明确“放弃并清理暂存”按钮，`WorkbenchStore.AbandonAsync` 走 `IJobService.AbandonAsync`；普通移除不再清理暂存，`UndoNotification` 文案改为“仅从工作台隐藏，源文件和已发布输出文件均未删除”。 |
| **P2 RemoveAsync 部分失败留 ghost card** | ✅ 已修 | 每个候选独立处理；成功项移除、失败项保留、单条通知、始终 `PersistAsync`；测试两卡场景（第二张 abandon 失败，第一张仍移除且持久化）。 |
| **P2 RecoveryService 非 journal 失败中止** | ✅ 已修 | Committing 清理逐 job try/catch→NeedsAttention 并继续；`MarkActiveJobsInterruptedAsync` 放入 finally 尽力执行。测试覆盖输出读取失败与列表失败仍处理其它 job。 |
| **P2 X02 显式音轨全部无效** | ✅ 已修 | 视频转码/Remux 的 v2 `Explicit` 选择在 `StreamSelectionResolver.Resolve(...).Audio.Count == 0` 时 Block（覆盖空列表与 `[99]` 等全无效列表）；混合有效/无效列表仍允许。Domain 测试 7 项。 |
| **P3 R04 次要 DV 轨绕过** | ✅ 已修 | Remux 对所有 `SelectRemuxStreams` 视频轨逐轨评估 DV 分类与 `CanRemuxDolbyVision`，不再只看主视频轨；新增 legacy/v2 次要 DV 测试。 |
| **P3 AppHost 临时库兜底** | ✅ 已修 | 兜底改用唯一临时 DB 并包裹 try/catch；两次初始化都失败时抛出一条带 `AggregateException` 的清晰 `InvalidOperationException`。 |
| **P3 提交边界取消** | ✅ 已修 | `JobScheduler` 在 `cts` 取消时重新抛 `OperationCanceledException`，走 Canceled 路径而非 NeedsAttention；新增关闭期提交测试。 |

**第四轮仍保留的风险/未处理**：A01 资源租约、A02 能力 key 深化、A06 多位置 HDR 探测、A07 验证超时预算化、A08 发布材料进一步自动化、E10 开发树双运行库；A05 的未知空间/UNC 预检仍偏保守；R04 真实 DV/RPU 样片验收与 R08 profile/位深/字幕轨签名。另：Abandon 仍是无二次确认的单个明确动作（已与普通移除分离），EditQueued 在极端“发布事件后抛异常”窗口可能短暂回显旧卡；orphan sweep 仅覆盖标准 `.transflux-work` 布局。

## 附：范围与方法

- 第一轮为只读静态审查 `src/**`（约 12k 行）、`tests/**`、`tools/**`、`scripts/**`、`packaging/**`、`docs/**`；实跑一次 Release 全量测试。第二轮的代码修改与验证见第八节。
- 第一轮未修改生产代码；第二轮修复均已附构建/测试/E2E 结果。未做动态故障注入或渗透测试；JSON/日志证据以本机实际命令输出为准。
- 不声称穷尽所有缺陷。若需要，可进一步开展完整安全审计（六阶段、产出 findings.json/REPORT.md）或针对 R04/R08/A03 的修复与失败用例。
