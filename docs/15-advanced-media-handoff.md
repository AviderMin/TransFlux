# 15 · 交给编码 AI 的执行任务单

日期：2026-11-01。本文是下一轮编码的入口。2026-11-01 本轮已按本单完成 T01/T02（G0），详见文末执行记录与 [G0 证据](evidence/G0-RELIABILITY.md)；T03 及以后仍按本单执行。用户目标：全面改善项目可靠性，并增加 Dolby 音频、Dolby Vision、HDR 和内封字幕烧录。阅读顺序：**13 → 14 → 本页 → 03/04/07/09**。旧 12 中“先搭 M0 空项目”的启动指令仅为历史，不再适用当前已有项目。

## 可以直接复制的启动提示

> 请继续开发当前 TransFlux 仓库，先读取 docs/13-project-review.md、14-advanced-media-roadmap.md、15-advanced-media-handoff.md，然后按需阅读 03、04、07、09 和实际代码。保持 WinUI 3 + Microsoft.UI.Reactor，不替换成 WebView/WPF，不重建已完成的空壳。先检查 AGENTS.md、git status 和既有未提交修改，保留它们。
>
> 先执行 T01、T02，修复提交恢复假成功、提交失败临时成果保全、选流语义和取消结果；使用失败测试证明问题后再修改实现。G0 通过后才新增高级功能。Dolby 音频和 Dolby Vision 都在规划内，但 Atmos 创作编码、DV profile5/7 完整转换、动态 HDR metadata 重建需专项闸门，不用普通编码器或改标签冒充。
>
> 每次完成一个可运行纵向切片，更新测试、迁移、支持矩阵与 evidence。先 CPU/小矩阵基线，再设备试跑和 GPU；fallback 必须保留同一色彩/音轨/字幕意图。没有真实样片或实机证据的组合标为未验证/禁用。不要联网上传媒体、替换驱动、下载 latest 核心或覆盖源文件。打包前核对版本、核心/工具清单与安装目录行为。

## 开始时应确认的事实

- 审查基线 commit 为 `bf04a00efb37a0616ce10f38480ff09b9624314b`，另有用户的 UI/滚动和文档工作区修改；应以接手时真实 diff 为准，不以本页覆盖新改动。
- 当时版本 1.0.0.8（当前源码版本与最新复核见 [16 项目复核](16-project-review-20261103.md)）；旧安装包不能代表后来源码。继承对称 12 DIP 滚动内容留白、工作台 3:2、右侧设置分组、文件优先和即时生效语义。
- 核心 Gyan FFmpeg 8.0.1 shared，manifest 固定 exe/DLL/归档身份；已有 NVIDIA 591.86 兼容证据。试跑 H.264 不等于 Main10/HDR/Vulkan 可用。
- 本轮 Release 测试 366 项通过；13 的 Published 假成功、音轨选择移除字幕等边界仍已复现，不能用全绿测试否定报告。
- 当前已有 HEVC/AV1/VP9 软件编码和软字幕转换；不要按旧“首发只 H.264、字幕全部丢弃”的设计倒退实现。

## 可独立评审的任务

### T01 · 结果恢复与提交保全（G0，最高优先级）

关联 R01/R02。入口 RecoveryService、OutputCommitter、OutputPathService、JobScheduler。

先写 Published+缺失 final、同长度替换、rename 前后失败、DB 写失败、取消/崩溃的测试；真实 SQLite 与真实临时目录至少各覆盖一次。将 Published 恢复统一交给身份 reconciler；将 reservation release 与 staging cleanup 拆开，未解决提交保留唯一输出和 journal。设计变更同时审查取消与退出分支，不能只修一个 return。

完成：没有文件的任务绝不新记成功；正常恢复幂等；提交不确定时明确 NeedsAttention，保留可用证据，不删除用户 final，不自动重复转换。

### T02 · 选流、取消、实际执行记录与工作台一致性（G0）

关联 R03/R05/R07/R09/R10/R11。入口 RequestTypes/ConversionOptions、SubtitlePolicy、Planner、ProcessCapture、WorkbenchRules/Store、DiagnosticReport、AppRoot 技术详情。

先冻结 Auto/None/Explicit 的语义和旧请求读取规则，分开音频/字幕选流。音频提取先明确只允许单轨还是多输出，不让 UI 多选后静默丢轨。ProcessCapture 明确 canceled，与 timeout/unsupported 区分；缓存不记录取消。Attempt 记录实际候选。入队/重试即使暂停也落盘绑定，再决定是否继续队列。

完成：选音轨不改变字幕，UI/LossReport 与命令一致，CPU fallback 诊断显示 CPU；暂停入队恢复不丢卡、不重复；取消所有阶段收敛为取消状态。旧不可变任务不被迁移成新意图。

### T03 · 请求 v2 与解析计划（G1）

依赖 T01/T02。按 14 的职责表增量演进，不一次建立空的“万能媒体框架”。入口 Domain、Application Contracts、WorkspacePersistence、PresetService/SqliteJobStore、WorkbenchStore。

建立 ResolvedMediaPlan/逐轨 ExpectedStream/LossReport，UI、编译器、验证消费同一事实；旧 RequestVersion=1、PlanSchemaVersion=2、旧工作区/预设提供固定 fixtures。拒绝未知 future version。源 stream ID、每输出轨序号与每文件批量解析规则分开。

对于旧 SelectedStreamIndices：已入队的旧快照按旧规则解析，不重新解释而改变执行结果；可编辑旧草稿迁移到新选流模型时说明有歧义的字幕策略，避免把旧音轨列表直接用作字幕列表。最终映射随 plan 冻结。

完成：旧 JSON/DB 可读，新写入版本明确，运行中请求不可改；新界面与旧软件输出组合无回归。

### T04 · 媒体证据与验证（G1，T05 及以后前置）

关联 R04/R06/R08/R12/A06。入口 FfprobeJsonParser、ColorClassifier、StreamDescriptor、FfmpegProbe、FfmpegVerifier。

增加每轨颜色/Dolby/附件依赖证据，区分 absent/unknown/conflicting；有预算地探测 frame metadata，不能把 `%+#1` 视为全片证明。验证显式选中全部 AV 轨，逐轨 codec/profile/布局/disposition；复制验证规范化 payload/时序，不要求不同容器全文件 hash 相同。DV/HDR 动态 metadata 与烧录画面各有专项验证器。

完成：错误第二音轨、metadata 丢失、默认/强制字幕标记变化能被测试识别；没有检测器/样片时不返回“已保留 Atmos/DV”。

### T05 · 高级媒体无损保留（G2）

依赖 T03/T04。先视频复制/重编码与逐轨音频 Copy 解耦，允许验证过的 Dolby 音轨复制。HDR10/HLG、DV 8.1 小矩阵单列，收紧原 DV 宽泛谓词。保留依赖字体与技术 metadata，维持隐私标签策略。

完成：真样片中 Dolby/HDR 证据前后一致，UI 明示范围；不兼容容器不得静默转 AAC/SDR。MP4/MOV/额外 DV profile 只有独立证据后开放。

### T06 · SDR 文本字幕烧录（G3）

依赖 T03/T04，T05 并非文本烧录的硬依赖，但建议按产品路线顺序完成。入口新 FilterGraph 编译器/ArtifactManifest、Planner、Runner、ManagedProcess、OutputSettingsPanel。

先 SRT 一条轨 → 实际画面 → 验证，再 ASS/SSA 与字体。受控 staging 文件名、Windows filter 转义、字幕流编号、裁剪/PTS/旋转/SAR/缩放同时覆盖；原样式和自定义样式分开。不要改动用户字幕文件，不把外部字幕/翻译/OCR一并塞进本切片。

完成：输出画面确实有字、时序/字体正确；明确是否保留额外软字幕；回退仍烧录；取消准备阶段不留无主进程或泄漏字体临时文件。

### T07 · CPU HDR 转换基线（G4）

依赖 T04/T05。分别提交 HDR10/HLG→SDR 与 HDR10→Main10 两条切片，不先放开全部格式。定义输入颜色、目标、tone-map/gamut/range/dither，显式处理动态 metadata 降级。HDR→SDR 可以在之后接 T06 烧录，保留 HDR+烧录暂禁。

完成：真实 10-bit 输出/正确 BT.709 SDR，有测试图与 metadata 证明；不能靠改标签或 yuv420p 转格式宣称完成。HDR/SDR 回退语义一致。

### T08 · 普通 Dolby 编码与位图字幕（G5）

AC3/EAC3 普通编码和 PGS/DVD overlay 各独立切片。普通 EAC3 不标 Atmos；TrueHD 编码器为实验性时不默认开放。位图支持按 decoder/画布/alpha/seek/PTS 验证，不复用 libass，不引入默认 OCR。

完成：布局/码率约束与错误提示准确；位图间隙无残影；多语言/forced、不同分辨率与旋转后位置正确。

### T09 · Main10/GPU、资源调度和复杂组合（G6）

关联 A01/A02。入口 CapabilityService、HardwareEncoding、JobScheduler、Planner、硬件/性能 evidence。

建立按设备和完整图的能力试跑；CPU、编码 GPU、Vulkan filter GPU、验证/准备分别计费。定义统一租约和取消释放。只有证据充分的 NVENC/QSV/AMF Main10、libplacebo 图进入候选。HDR 字幕合成要独立处理字幕亮度/alpha/高精度颜色，不能直接沿用 SDR overlay。

完成：NVIDIA/Intel/AMD 各自有通过/未验证矩阵；并发和显存故障可诊断，无资源死锁；CPU fallback 不丢 HDR/DV/字幕输出语义。

### T10 · Dolby 专项实验与 provider（G7）

依赖前述验证基础。DV profile5 的重塑路径、profile7 MEL/FEL 保留/渲染、HDR10+ 动态重编码、受限 DV 重编码与 Atmos 专业编码 provider **分别立项**。记录外部工具精确版本、哈希、许可证/依赖材料与检测器依据。

完成：每个子项有真实合法素材、逐帧/对象 metadata 验证和目标设备证据；支持与不支持的组合清楚可见。不要把提取/再注入 RPU 当作任意编辑后 DV 仍正确的证明。没有授权/验证的 Atmos 创作编码保持不可用，普通复制和转换可以独立发布。

## 每个任务的完成定义

1. 先写问题触发条件或功能纵向测试，证明改动有效；不要写镜像实现的常量测试冒充正确性。
2. 相关单测/集成/E2E/故障注入通过，旧矩阵保持；真实执行与 synthetic JSON 测试分开标记。
3. 请求、计划、UI、验证与恢复一致；清楚列出降级、依赖和不支持原因。
4. evidence 记录命令、核心/工具/驱动、样片来源/hash、关键结果、未验证项；更新 13 的相关 ID 状态及 14 的支持表。
5. 构建和打包必须可复现；下一版本安装包不复用 1.0.0.8 文件名，不能以开发目录成功代替安装目录验证。

## 基线命令

```powershell
git status --short
pwsh -File scripts/fetch-ffmpeg.ps1
dotnet build TransFlux.slnx -c Release -p:WindowsPackageType=None
dotnet test TransFlux.slnx -c Release -p:WindowsPackageType=None --no-build
dotnet run --project tools/TransFlux.E2E
# 在确有相应 GPU 的机器上补硬件/回退 E2E；新增语义以新的矩阵为准。
dotnet run --project tools/TransFlux.E2E -- --hardware
dotnet run --project tools/TransFlux.E2E -- --fallback-smoke
```

本机 `artifacts/review-20261101/repro.log` 只用于定位现有问题；下一位 AI 应在正式 tests 中重建复现，不依赖临时目录、不把复现得到的错误行为当成验收通过。

## 本轮执行记录（2026-11-01）

- **T01 已完成**：Published/Prepared 恢复统一身份核验；提交失败保留 staging/journal；真实 SQLite + 临时目录故障测试通过。
- **T02 已完成**：请求 v2 选流（v1 旧快照语义不变）、音频/字幕解耦、音频提取单轨、取消语义、ExecutedCandidateId、暂停工作台持久化；失败测试先行有记录。
- 验证：Release 构建 0 warnings / 0 errors，438/438 测试通过，真实 CPU E2E PASS。详见 [G0-RELIABILITY.md](evidence/G0-RELIABILITY.md)。
- **T03/T04 及以后未开始**；2026-11-04 补充：T05 的 G2 音频复制切片（视频转码 + MKV 原样复制音频）部分完成，见 [G2 证据](evidence/G2-AUDIO-COPY.md)；Dolby/HDR/字幕烧录仍未实现，不能据此宣称高级能力已全面支持。
