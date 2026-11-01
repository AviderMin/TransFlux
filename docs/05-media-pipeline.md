# 05 · 媒体转换管线

## 核心选择与信任边界

首发采用成对分发的 `ffmpeg.exe` / `ffprobe.exe`，通过结构化参数调用。两者必须来自同一固定构建，记录版本、`-buildconf`、哈希、许可证和可用编解码器。不是调用 PATH 上任意同名程序。默认核心候选包含 `libx264`，按 GPL 工具包路线核验；发布前完成 08 的许可材料。

FFmpeg 文档说明硬件编译能力不代表当前机器可运行，实际支持取决于硬件与驱动；选项也可能随构建不同。所有示例都必须针对固定构建集成测试。[FFmpeg 命令行文档](https://ffmpeg.org/ffmpeg.html)

## 探测与归一化

设计参数序列：`ffprobe -v error -print_format json -show_format -show_streams -show_chapters <input>`。由安全参数列表传递路径；限制网络协议，并限制解析输出大小和运行时间。协议限制不能把用户文件当可信容器，本地引用型格式首发也须按安全策略拒绝或隔离。

每条流记录 index、codec_type/name、disposition、language/title、time_base、duration、start_time；视频记录宽高、SAR/DAR、rotation、pix_fmt、位深、color_range/space/transfer/primaries、HDR side data、avg/r_frame_rate；音频记录 channels/layout、sample_rate、bit_rate。全局时长和流时长分别保留，不用文件大小推算时长。

普通本地 probe 初始超时 15 秒、JSON 上限 8MiB，UNC 初始超时 30 秒；超限可由用户显式重试更高预算，但不能无限挂起或无限积累输出。缩略图按需生成，初始最长边 320px、单文件解码超时 5 秒；失败使用通用图标，不影响转换资格。解码像素数与异常大尺寸设明确上限并纳入恶意输入测试，不能只限制编码文件大小。

缺失/`N/A`/零分母为 Unknown，不转成 0 后参与 ETA。排除 attached_pic 作为主视频，多个普通视频流需用户选择。识别 HDR 依赖 transfer、位深与 side data 组合；未知或冲突色彩标为需确认，不能自动当 SDR。输入指纹至少为规范化路径、长度、修改时间 UTC；不要求导入时哈希整个大文件，执行期间仍可能被改写，须在验证后再次检测。

## 计划编译步骤

`SourceSnapshot + RequestSnapshot + CapabilitySnapshot + CoreIdentity → ValidatedPlan`。

1. 校验媒体模式、源状态、时间范围、选中流。
2. 检查容器/编码/字幕/声道/像素格式兼容矩阵。
3. 计算旋转、显示比例、缩放、偶数尺寸、帧率和色彩策略。
4. 选择允许的编码候选与设备路径，编译 typed filter graph。
5. 计算输出扩展名、元数据和 stream mapping；扩展名与实际 muxer 必须一致。
6. 生成参数列表、声明式预期输出、估算值、警告、计划哈希与版本。

禁止拼接用户自由文本滤镜或全局参数。只读命令展示仅供诊断，实际执行永远用结构化参数。

## 当前输出兼容策略（2026-11-01 扩展）

| 模式 | 视频 | 音频 | 字幕/附件 |
|---|---|---|---|
| MP4 转码 | H.264 / H.265 / AV1 | AAC / MP3 / ALAC | 视频转码不输出字幕/附件，开始前提示 |
| MKV 转码 | H.264 / H.265 / AV1 / VP9 | AAC / Opus / Vorbis / FLAC / MP3 / PCM 16/24-bit | 同上 |
| MOV 转码 | H.264 / H.265 | AAC / ALAC / PCM 16/24-bit | 同上；当前核心不支持 MOV/AV1，界面不提供 |
| WebM 转码 | VP9 / AV1 | Opus / Vorbis | 同上 |
| M4A 音频 | 无 | AAC / ALAC | 音频输出只写选中的一条音轨 |
| MP3 / FLAC / AAC(ADTS) 音频 | 无 | 分别为 MP3 / FLAC / AAC | 无视频/字幕/附件 |
| WAV 音频 | 无 | PCM 16-bit / PCM 24-bit | 同上 |
| OGG / Opus 音频 | 无 | OGG：Vorbis/Opus；Opus：Opus | Opus 自动重采样到 48kHz，显式采样率需合法 |
| 视频转封装 | 按容器兼容白名单复制 | 按容器兼容白名单复制 | MKV 文本字幕；MP4/MOV 仅已有 mov_text；WebM 仅已有 webvtt；位图字幕仍阻断 |

界面和执行验证共用 Domain 的 OutputFormatCatalog，避免把容器与编码绑定成少量固定预设。
Planner 按所选编码生成参数，Verifier 对所有支持的视频/音频编码以及容器族做检查并解码，不能只识别 H.264/AAC。
这不是 FFmpeg 能力全集。所有 59 个界面提供的转码组合已用固定核心实际编码、探测、全片解码验证，并故意指定错误编码验证拒绝行为。
输入色彩/位深约束与输出格式数量分开管理；当前视频转码仍限定 SDR BT.709、8-bit 4:2:0，不因新增 H.265/AV1 就宣称已支持 HDR/10-bit 转码。
检测到额外章节、标签或附件时说明保留与舍弃，不声称所有 metadata 无损。

帧率默认保留时间戳，不强制 `-r`；VFR 样本单独验收。源旋转默认烘焙到输出像素并清理旋转标签，避免播放器重复旋转。横竖屏尺寸根据显示方向计算，按比例向下取整为偶数且不放大；SAR 非 1:1 的输入按显示比例输出方形像素，验证 DAR 误差。不默认把非 BT.709 色彩粗暴打成 BT.709；首发转码限定已验证的 SDR BT.709 输入，SD/BT.601、广色域或色彩未知先阻断转码，允许符合矩阵的 copy。扩展转换需有显式色彩处理和测试。

## 质量与码率

界面提供“较小体积 / 均衡 / 更高画质”与单独速度档。CPU H.264 的初始候选映射为 CRF 26/23/20、preset medium；这只是待测起点。NVENC/QSV/AMF 分别用自身支持的质量/码率控制参数标定，参数不跨编码器照搬，尚未标定的组合不开放“质量等效”承诺。

H.265 初始质量映射为 30/26/22，AV1/VP9 为 40/34/28；这些参数只是各编码器自己的默认档位，不承诺跨编码器质量相等。
软件编码器分别为 libx264、libx265、libaom-av1、libvpx-vp9。新增 H.265/AV1/VP9 当前使用 CPU，H.264 保留原有硬件路径。

自动模式只在通过同一质量档准入的候选中按预估完成时间选择；没有合格硬件则 CPU。硬件不能满足用户硬约束（10-bit 等）时不允许靠降规格通过。

质量模式体积默认显示“无法准确估计”或来自相似历史的宽范围并标注估算。已知目标码率才可用 `duration × (video bitrate + audio bitrate) / 8 + 容器余量` 估计；恒定质量不保证大小。P0 不做目标体积承诺，也不把单遍平均码率伪装为精准大小。

## 命令模板与进程通道

以下为简单 SDR 单视频单音轨 CPU 转码的设计模板，`<...>` 是说明占位，不能原样执行：

```text
ffmpeg -hide_banner -nostdin -n -loglevel warning
  -stats_period 0.5 -progress pipe:1
  -i <absolute-input>
  -map 0:<video-index> -map 0:<audio-index>
  -c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p
  -c:a aac -b:a 192k
  -map_metadata -1 -map_chapters -1
  -movflags +faststart -f mp4 <same-volume-attempt-temp-path>
```

模板不含路径协议白名单、裁剪、缩放等完整策略，实际由 Compiler 补全。无音轨时省略 audio map 与编码选项；不使用 `-map ...?` 隐藏用户显式选中的流消失。多音轨按输出索引分别设置参数与 language/disposition。元数据默认清除自由标签和章节，保留/重建选中轨道 language、默认轨标记及必要媒体语义；保留标题/章节作为可选项，摘要说明可能含隐私。

临时文件不能靠后缀推断 muxer，始终显式 `-f`。`-n` 防止覆盖已有文件，attempt 临时名唯一且核心不得覆盖源。`-nostdin` 表示不通过 `q` 控制退出；取消走受管理进程终止，残片删除，见 08。

## 进度、验证与正确性

解析 `-progress` 的 key=value 数据块，不从 stderr 正则猜完整进度。优先使用固定构建支持的 `out_time_us` 或时间文本；`out_time_ms` 名称可能误导，必须以测试确认单位。`progress=end` 只代表进程报告结束，不能独立判成功。ETA 按滑动窗口测量，起步/回退/未知时不显示，裁剪按有效输出时长计算，避免取源时长。

成功须满足：退出码 0；临时输出存在且非零；ffprobe 可读；流数量/codec/尺寸/pix_fmt/声道符合 plan；时长在容差内；快起播等要求可检查；输出提交成功。视频时长容差初始设 max(0.5 秒, 两帧有效帧时长)，音频加入编码 padding 容差并按格式样片校准。起点非零、VFR、裁剪、AAC priming 分别验收，不能拿一个宽阈值遮蔽错误。

默认对首/中/尾代表片段做短解码校验（采样验证不保证全片无损）；发布回归和用户选“完整验证”时全片解码。验证时的 CPU/设备预算也归调度器管理。验证错误保留诊断，不发布正式文件。

FFmpeg 基础进度、流映射和硬件参数依据官方命令文档；详细 filter 语法与 probe 字段在 M0 固定核心后补录官方参考与实测样例。[FFmpeg 官方文档入口](https://ffmpeg.org/documentation.html)
