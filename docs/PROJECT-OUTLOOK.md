# TransFlux 项目展望

更新日期：2026-11-04（Asia/Shanghai）  
对应源码版本：1.0.0.9  
配套文档：[DEVELOPMENT-SUMMARY.md](DEVELOPMENT-SUMMARY.md)

## 1. 项目愿景

把 TransFlux 从“可靠的 SDR 通用转换器”扩展为“能解释、保留并验证高级媒体结果的桌面向导”：用户能看懂源媒体的音轨、色彩、Dolby/HDR/DV 与字幕事实，选择保守、可验证的输出策略，失败时得到证据而不是假成功。

不变的产品原则：

1. 源文件只读，绝不覆盖；输出先 staging、后验证、再 no-replace 提交。
2. 没有真实样片/实机证据的能力不开放、不宣传；UI 与实际执行使用同一份解析事实。
3. 默认本地、无遥测、无网络媒体输入；高级能力按 case 放行，不整体“Dolby=true”或“HDR=true”。
4. 每个阶段以可运行纵向切片交付：代码 + 迁移 + 失败转通过的测试 + 真实核心/样本证据 + 更新的支持矩阵。
5. 先 CPU/小矩阵，后 GPU/复杂组合；回退必须保留同一音轨/字幕/色彩意图。

## 2. 当前基线（已完成）

- 原生工作台：文件优先卡片、右侧输出设置、队列/历史/设置/诊断。
- 真实 FFmpeg 探测/计划/执行/验证/安全提交，SQLite 持久化与崩溃恢复。
- CPU H.264/H.265/AV1/VP9；H.264 的 NVENC/QSV/AMF 候选；音频提取、Remux、MKV 字体附件。
- 请求 v2 独立音频/字幕选流；逐轨 codec/声道/disposition 验证；Dolby Vision DOVI 配置保留断言。
- 2026-11-04：G2 首个切片——视频转码 + MKV 音频原样复制（AC3/EAC3/TrueHD 规则，真实 AC3 6ch）。
- 最近验证：Release 0 warnings/0 errors，542 tests passed，真实 E2E PASS。

详细完成清单见 [DEVELOPMENT-SUMMARY.md](DEVELOPMENT-SUMMARY.md)。

## 3. 路线图

推荐顺序：**G1 → G2 完成 → G3 → G4 → G5 → G6 → G7**。G0（可靠性闸门）已完成。

| 阶段 | 目标 | 主要工作 | 完成条件 |
|---|---|---|---|
| **G1 媒体契约收尾** | 把逐轨媒体事实固化为一等契约 | `ResolvedMediaPlan` / `LossReport` / 每输出轨序号、未来版本拒绝、旧请求/旧 workspace 固定 fixtures | UI、计划、验证消费同一事实；旧 JSON/DB 全可读；全矩阵回归 |
| **G2 无损保留（完成中）** | 安全复制与保留 Dolby/HDR/DV | Dolby 音频识别（AC3/EAC3/TrueHD/Atmos 证据状态）；HDR10/HLG stream-copy 与静态 metadata 验证；DV 8.1 真实 RPU/EL/BL 样片；MP4/MOV 音频复制矩阵 | 真样片前后证据一致；Atmos 不靠五声道/7.1 猜测；不兼容容器不静默转码 |
| **G3 SDR 文本字幕烧录** | 内封文本字幕永久绘制到画面 | 受控 filter graph、fonts staging、SRT→ASS/字体、裁剪/PTS/旋转/SAR/缩放、样式策略 | 输出画面确实有字、时序/字体正确；失败/取消无孤儿进程与临时文件 |
| **G4 CPU HDR 基线** | HDR10/HLG 的可验证 CPU 转换 | ColorPlan、tone-map/gamut/range/dither、x265 Main10、HDR10→HDR10 与 HDR→SDR 两条切片 | 真实 10-bit 输出与正确 BT.709/HDR 信令；不靠改标签；动态 metadata 降级显式告知 |
| **G5 扩展音频与位图字幕** | 普通 Dolby 编码与 PGS/DVD overlay | AC3/EAC3 参数与 downmix；PGS/DVD 解码、画布、alpha、seek/PTS | 编码布局/码率合法；Atmos 不误标；位图间隙无残影、forced/多语言位置正确 |
| **G6 硬件与组合** | Main10 GPU、资源租约、libplacebo/HDR 合成 | 按设备+完整图试跑、CPU/GPU/Vulkan/验证分账、统一租约、HDR 字幕亮度/alpha | NVIDIA/Intel/AMD 矩阵逐项证据；并发/显存/取消可诊断；fallback 语义等价 |
| **G7 Dolby 专项** | DV profile5/7、HDR10+ 动态重编码、受限 DV 重编码、Atmos provider | 每子项独立样片/依赖/许可/检测器；RPU/EL 逐帧或访问单元证据 | 有授权素材与目标设备证据；未授权/未验证能力保持禁用 |

## 4. 近期优先级（建议下一轮）

### P0 · G2 音频复制补全
- 增加真实 EAC3/TrueHD 样片复制验证；把 Domain 规则证据升级为真实转码证据。
- 评估 MP4/MOV 的 AC3/EAC3 复制矩阵（按 codec/profile/播放器验证后逐格开放）。
- UI 增加源音轨 Dolby 识别展示：AC-3/E-AC-3/TrueHD、声道布局、Atmos 状态（Confirmed/NotObserved/Unknown），不把 5.1/7.1 当 Atmos 证据。
- 记录“复制音频”与“重编码音频”的独立 LossReport 项。

### P1 · G1 契约收尾
- 引入 `ResolvedMediaPlan` 与 `LossReport`，让 UI 的“保留/丢弃/重编码”说明与 Planner/Verifier 共用同一模型。
- 补 v1/v2 请求、旧 workspace、旧 preset 的固定 fixtures；未来版本明确拒绝。
- 为每输出轨建立序号与来源映射，避免批量文件/多轨场景的歧义。

### P2 · G3 字幕烧录最小切片
- 先做单条 SRT 轨 → 实际画面 → 画面级验证；再做 ASS/SSA + 字体。
- 处理 Windows filter 转义、字体临时目录、裁剪/PTS/旋转/SAR/缩放；不修改用户字幕文件。
- 保留额外软字幕或明确说明只输出烧录画面；取消/失败清理临时字体与进程。

## 5. 工程与验收要求

- **样片 manifest**：来源、使用/再分发权限、SHA-256、期望流/metadata、是否合成；Dolby/Atmos/DV 不得用伪造 JSON 或改标签冒充。
- 每个阶段记录：真实核心/工具/驱动身份、关键命令、计划/参数、输出 probe/metadata 报告、通过/失败/未执行列表。
- 测试分层：Domain 规则、Engine 真实核心、Application 调度/恢复、Infrastructure SQLite/文件系统、App UI 构建；真实样本与 synthetic 证据分开标注。
- 发布前：Release build 0 警告/0 错误、全量测试通过、真实 E2E PASS、安装目录验证；版本升级且不复用旧安装包文件名。
- 未开放能力在 UI 中不可选或明确标记实验；不把“识别到”写成“已保留/已验证”。

## 6. 非目标与边界

- 不做播放器/HDMI bitstream passthrough/系统音效；TransFlux 是文件转换器。
- 不默认做在线更新、媒体上传、云端转码或遥测。
- 不默认开放 TrueHD 编码、Atmos 创作、DV profile 5/7 完整渲染；这些需独立 provider/授权/样片闸门。
- 不承诺通用倍速或“更小体积”；性能数字必须实测并附配置。
- 不重写现有 WinUI 3 + Reactor 架构；在现有分层上增量演进。

## 7. 交接入口

- 当前完成事实：见 [DEVELOPMENT-SUMMARY.md](DEVELOPMENT-SUMMARY.md)。
- 代码入口：`src/TransFlux.App/Hosting/TransFluxAppHost.cs`、`src/TransFlux.Application/Services/JobScheduler.cs`、`src/TransFlux.Engine.FFmpeg/Planner/FfmpegPlanner.cs`。
- 核心/依赖：`tools/ffmpeg/core-manifest.json`；发布脚本 `tools/package-exe.ps1`。
- 新增能力前先读本文件与完成汇总，再按路线图写失败测试/样片证据，不要跳过 G1/G2 直接实现 G7。
