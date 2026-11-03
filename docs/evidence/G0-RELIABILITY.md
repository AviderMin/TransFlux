# G0 可靠性闸门 · 执行与验证证据

日期：2026-11-01（Asia/Shanghai）。对应 [15 任务单](../15-advanced-media-handoff.md) 的 T01/T02 与 [13 审查](../13-project-review.md) 的 R01/R02/R03/R05/R07/R09/R10/R11。审查基线为 commit `bf04a00efb37a0616ce10f38480ff09b9624314b`；接手时用户已有的 UI/滚动/文档修改已由用户提交为 `32a4a13`，内容全部保留，本轮代码改动均基于该提交。

结论：**G0 通过**。本轮只完成可靠性闸门；Dolby 音频、Dolby Vision、HDR、内封字幕烧录等高级能力仍未实现，见文末“未验证/禁用”。

## 环境与命令

- .NET SDK 10.0.401；Windows 11；应用版本仍为 1.0.0.8（本轮未生成新安装包）。
- 媒体核心：Gyan FFmpeg 8.0.1 shared，`tools/ffmpeg/bin/ffmpeg.exe`，CoreHash 前 12 位 `E2738D939693`（真实 E2E 使用）。
- 构建：`dotnet build TransFlux.slnx -c Release -p:WindowsPackageType=None` → **0 warnings / 0 errors**。
- 测试：`dotnet test TransFlux.slnx -c Release -p:WindowsPackageType=None --no-build` → **438 passed / 0 failed / 0 skipped**（Domain 130、Application 119、Infrastructure 51、Engine.IntegrationTests 138；基线为 366，本轮新增 72）。
- E2E：`dotnet run --project tools/TransFlux.E2E/TransFlux.E2E.csproj -c Release -p:WindowsPackageType=None` → `E2E_RESULT=PASS`（真实 probe→plan→encode→verify→commit，源文件 hash 前后一致；attempt 记录 `executed=cpu-libx264`）。

## T01 · 恢复与提交保全（R01/R02）

修复前失败证据（实现前运行新增/改写测试）：

- Application：5 项失败——Published journal 不调用 reconciler 即关闭、提交失败删除 temp/work directory 等。
- Infrastructure：2 项失败——Published/NeedsAttention 缺失 final 仍被判 Succeeded。

实现：

- `RecoveryService`：Published 与 Prepared journal 统一走 `ICommitReconciler.ReconcileAsync`；失败时保留 journal、转 NeedsAttention。Committing 且有 OutputRecord 的 job 走 `IOutputRecordVerifier.VerifyOutputAsync`（存在/大小/ journal 哈希），不再以数据库记录证明文件存在。
- `OutputCommitter`：新增 `VerifyOutputAsync`；Prepared journal 在 move 前复核 staging 内容哈希，身份不匹配的 temp 不会被发布。
- `OutputPathService`：拆分 `ReleaseReservation`（只删命名锁）与 `DeleteWorkDirectory`（只删 staging）；`Release` 保留为兼容组合操作。
- `JobScheduler`：提交失败分支（rename 前后、DB 写入失败、取消/异常）保留 reservation、work directory、temp 与 journal，job → NeedsAttention；正常成功与提交前失败路径仍按原语义清理。
- `SqliteJobStore.RecoverNeedsAttentionJobsAsync`：启动恢复 NeedsAttention 前核验 final 存在、大小与可用的 journal content hash，缺失/不匹配保持 NeedsAttention。

关键测试（真实 SQLite + 真实临时目录）：`PublishedJournal_MissingFinal_KeepsJournalAndMovesJobToNeedsAttention`、`PublishedJournal_SameLengthReplacement_KeepsJournalAndMovesJobToNeedsAttention`、`PublishedJournal_LegacyWithoutHash_KeepsJournalAndMovesJobToNeedsAttention`、`PreparedJournal_WrongStoredHash_KeepsJournalAndStagedTemp`、`PublishedJournal_Normal_RecordsOutputAndIsIdempotent`、`CommittingJob_OutputRecordMissingFinal_StaysNeedsAttention`、`CommitFailure_PreservesTempAndWorkDirectory_AndMovesJobToNeedsAttention`、`CommitException_PreservesTempAndWorkDirectory_AndMovesJobToNeedsAttention`、`Commit_OutputRecordWriteFailsAfterRename_LeavesPublishedJournalAndFinalFile`。

已确认语义：没有文件的任务不新记成功；正常/重复恢复幂等；提交不确定时 NeedsAttention 并保留可用证据；不删除用户 final，不自动重复转换。遗留：NeedsAttention 的 staging/lock 目前保留，后续需要人工处理或受控清理策略；legacy final-only 无哈希 journal 一律 NeedsAttention；大文件恢复哈希会增加启动耗时。

## T02 · 选流、取消、实际执行记录与工作台一致性

### R03/R05/R07 选流语义（T02A）

修复前失败证据：`OutputSelectionTests.SelectingAnAudioTrackDoesNotRemoveSubtitles`、`OutputSelectionTests.AudioExtractMultiTrackSelectionIsBlockedInsteadOfSilentlyTakingFirst`、`StreamSelectionWorkbenchTests.TranscodeKeepingSubtitlesDoesNotWarnAboutSubtitleLoss` 在实现前失败（3 项）。

实现：

- 请求 v2：`ConversionRequest.RequestVersion = 2`，新增 `VideoSelect` / `AudioTracks` / `Subtitles`，枚举 Auto/None/Explicit（视频 Auto/Explicit）。`RequestVersion = 1` 旧请求继续按旧规则解释（非空 SelectedStreamIndices 同时过滤视频/音频/字幕，空集合=全部），旧快照执行结果不变。
- 新增 `StreamSelectionResolver` 作为唯一解析入口；`MediaCompatibility`、`FfmpegPlanner`、`SubtitlePolicy`、`WorkbenchRules` 共用。v2 音频选择不影响字幕；字幕默认 AutoPreserve；None 明确排除；Explicit 只选列出的字幕轨。
- 音频提取单轨：v2 多选在 `MediaCompatibility` 阻断并提示“音频提取一次只输出一条音轨”；`FfmpegPlanner` 二次防御，不再静默只输出第一条。v1 保持旧行为。
- UI：`OutputSettingsPanel` 增加音轨模式（保留全部/不输出/指定）与字幕模式（保留全部（默认）/不输出/指定）；批量只允许 Auto/None；旧 `SelectedStreams` 不会作为字幕索引。`AppRoot` 的“记住默认参数”同时清空 AudioMode/AudioStreamIndices/SubtitleMode/SubtitleStreamIndices。
- `WorkbenchRules` / `FfmpegPlanner` 警告按解析后的字幕选择逐轨说明；零字幕不再声称“全部保留”。

### R09 取消（T02B）

修复前失败证据：新增 12 个测试中 9 个在实现前失败（取消被当成 TimedOut/failure；另有首次编译 7 个 CS1061，见 `artifacts/review-20261101/t02b-cancel-evidence.md`，该目录不随源码提交）。

实现：

- `ProcessCapture.CaptureResult` 增加 `Canceled`；启动前取消/运行中取消 `Canceled=true, TimedOut=false`，超时仍 `TimedOut=true`；kill 进程树后有界 drain，保留已读 stdout/stderr。
- `FfmpegProbe`：`Canceled` → `Result.Canceled()`；`FfmpegVerifier`：取消抛 `OperationCanceledException`（JobScheduler 现有 catch 收敛为 attempt Canceled / job Canceled，不再误判 VerifyFailed）。
- `FfmpegEngineCapabilities` 的试编码/列表查询取消一律抛 OCE，CapabilityService 不会把取消写入失败缓存。

关键测试：`ProcessCaptureTests`（启动前/运行中取消、超时非取消、正常退出）、`CancellationSemanticsTests`（Probe/Verifier/Capabilities 取消与缓存）。

### R10/R11 实际执行记录与工作台持久化（T02C）

修复前失败证据：

- `ExecutedCandidateTests.AutomaticFallback_RecordsExecutedCandidateForEachAttempt` 与 `SingleCandidateAttempt_RecordsExecutedCandidate` 在 JobScheduler 记录实际候选前失败（Actual: null）。
- `WorkbenchTests.PausedStartPersistsJobBinding_AndCardSurvivesRestart` 与 `PausedRetryPersistsBinding_BeforeQueueStaysPaused` 在旧 `if (!wasPaused && PersistAsync())` 代码路径上失败（持久化集合中没有新 JobId）；用临时回退实现复跑确认 2 项失败后恢复修复，复跑通过。

实现：

- `Attempt.ExecutedCandidateId` 持久化（SQLite migration 4，旧库可读）；`JobScheduler` 在每次 attempt 创建时记录实际候选（自动回退两次 attempt 分别记录 NVENC / CPU）。
- `DiagnosticReport` / `AppRoot` 按实际候选显示；legacy 单候选可安全推断，多候选缺失时显示“未知”，不再显示 CandidatePaths[0]。
- `WorkbenchStore.StartAsync/RetryAsync`：无论是否暂停，先落盘卡片→job 绑定，再决定是否恢复队列；暂停时也持久化。

关键测试：`ExecutedCandidateTests`（2）、`WorkbenchTests` 新增 4（暂停启动恢复、暂停重试持久化、诊断报告实际候选、legacy 不猜测候选）、`SqliteJobStoreTests`（migration 4 重放、ExecutedCandidateId roundtrip）。

## 未验证 / 未实现（不得当作已支持）

- **G1 及以后未开始**：ResolvedMediaPlan/逐轨 ExpectedStream/LossReport、媒体证据与验证、高级无损保留、字幕烧录、HDR 转换、Dolby 编码/DV 专项均未实现；HDR/DV/Atmos 支持矩阵维持原状。
- 本轮 UI 只做编译 + 逻辑测试；未重新做真实 WinUI 窗口、DPI/读屏/浅深色人工验收。
- 硬件路径未在本轮实机签核；E2E 为 CPU H.264/AAC 路径，`--hardware`/`--fallback-smoke` 未运行。
- NeedsAttention 保留的 staging/lock 清理策略、崩溃后人工处理界面未在本轮实现。
- 本轮未生成新安装包；发布前仍需按 15 的版本/核心/安装目录要求核对。
