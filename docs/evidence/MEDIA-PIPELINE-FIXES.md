# 媒体管线缺陷修复（task-2）· 2026-11-01

依据 ffmpeg-reviewer 审查报告修复 src/TransFlux.Engine.FFmpeg 与 src/TransFlux.Domain 的媒体管线缺陷。
写入范围：src/TransFlux.Engine.FFmpeg、src/TransFlux.Domain、tests、docs（未触碰 src/TransFlux.App）。

## Major（6 项，全部完成）

1. **MP4 多音轨静默截断**（原 FfmpegPlanner.cs:490-491 Take(1)）：移除截断；未显式选流时保留全部音轨（与转封装语义一致），显式选流全部保留。回归：Transcode_Mp4_MultiAudio_KeepsAllTracks、Planner_Mp4KeepsMultipleAudioMaps。
2. **流级语言/标题剥离**：StripPersonalTags 仍清自由标签/章节，但 language 与 default/forced 标记经 -metadata:s:a:N language= 与 -disposition 显式重建（音轨+字幕，转码/提取/转封装全路径），与 docs/05 承诺一致。回归：Transcode_PreservesLanguageAndDisposition、Planner_RebuildsLanguageAndDisposition。
3. **字幕链路**：转码携带字幕（copy 或 mov_text/srt/webvtt 转码，SubtitlePolicy）；MKV 位图字幕（PGS/DVB/DVD）直通放行，MP4/MOV/WebM 跳过并警告；MKV 转封装保留字体附件（filename/mimetype 显式重建，StreamDescriptor 新增字段）；语言/disposition 随 2 重建。回归：Transcode_Mp4_TextSubtitle_BecomesMovText、Remux_Mkv_KeepsFontAttachments、Planner_MkvBitmapSubtitle_StreamCopies、Planner_Mp4Subrip_UsesMovText、SubtitlePolicyTests（12 例）。
4. **Dolby 分级放行**：ColorClassifier 仅以 DOVI side data 判 DV（smpte432→宽色域，回归 ColorClassifier_Smpte432_IsWideGamut_NotDolbyVision）；FfprobeJsonParser 解析 dv_profile/bl_signal_compatibility_id（回归 Parser_DoviConfigurationRecord_ExtractsProfile）；P8（兼容基底）允许 MKV 直通保留 RPU，P5/7 与未知 profile 阻断并给出原因（MediaCompatibility.CanRemuxDolbyVision）；MKV remux 白名单放行 truehd/mlp/dts/mp2 等，MP4/MOV 放行 ac3/eac3（RemuxWhitelistTests 14 例）。
5. **HDR 元数据结构化传递**：StreamDescriptor 新增 MasterDisplay/MaxCLL/MaxFALL/DvProfile/DvBlSignalCompatibilityId/TimeBase/StartUs；probe 追加 -show_frames -read_intervals %+#1 合并帧级 side data（FFmpeg 8 把 MDCV/CLL 挂在帧上）；转码经 setparams= 滤镜段 + 输出侧 -color_* 双通道携带色彩，Verifier 抽查 color_primaries/transfer/space（回归 Probe_ParsesHdrStaticMetadata、Transcode_PreservesColourMetadata、Parser_FrameLevelHdrSideData_MergesIntoStream）。
6. **remux 白名单扩展**：OutputFormatCatalog.RemuxVideoCodecs/RemuxAudioCodecs/RemuxBitmapSubtitles 汇总按容器的直通白名单（含 mpeg2video/mpeg4/vc1/prores/theora/vp8/ffv1；DVD VOB(mpeg2+ac3)→MKV 可用），替代散落特判（vp8 特判归入目录）。

## Minor（11 项完成，1 项按约定暂缓）

- (8) libvpx-vp9 -cpu-used 独立映射 5/4/2（libvpx 上限 5），libaom-av1 保持 8/6/4。
- (9) x265 移除硬编码 pools=4:frame-threads=2，仅保留 log-level=error。
- (10) NVENC 无码率改 -rc constqp -qp（消除 vbr -cq 无目标码率的未定义语义）；AMF cqp 补 -qp_b；试编码参数同步。
- (11) ParseNames 剔除 -encoders/-pix_fmts 图例行（"V..... = Video" 不再产生 "=" 条目）。
- (12) ValidateEncoderAsync：AddDevice 移入 try 返回 FailureReason；testsrc2 尺寸取偶（Evenize）。
- (13) ffprobe 解析 time_base/start_time；UNC 路径默认探测超时 30s（docs/05 约定）。
- (14) Verifier 新增色彩抽查、语言保留校验（Audio/SubtitleLanguages）、附件流计数校验。
- (15) UsesGpuDecode/UsesGpuFilter 修正（仅硬件候选 UsesGpuFilter=true，解码恒为软解）；AudioSpec.KeepChannelLayout 保留（App 引用中，删除会破坏 src/TransFlux.App 编译），语义以文档注释固化：仅显式 Channels 时才 -ac。
- (16) 警告具体化：字幕跳过逐条说明、章节清除、附件未保留（非 MKV）、元数据策略说明。
- (17) Runner 错误分类补 QSV/AMF 串（mfx session/qsv/amf/no device available/cannot init）；TryDelete 放弃后 Trace 告警。
- (7) **暂缓**：HEVC/AV1 硬件编码（hevc_nvenc/hevc_qsv/hevc_amf/av1_nvenc/av1_qsv）。能力试跑目标列表在 Infrastructure CapabilityService（本次写入范围外），且 HARDWARE-COMPATIBILITY 基线只实测 H.264；半途接入会产生"探测不到即永远 CPU"的死代码。待下轮跨层实施并重新做硬件验收。

## Nit（5 项全部完成）

- (19) InferBitDepth 补 14le/14be 与 rgb48/rgba64→16。
- (20) MediaTypes/ColorClassifier 注释对齐：无标签 4:2:0 假定 SDR BT.709 为文档化启发式。
- (21) ColorClassification.Hlg 独立档（App switch 有默认臂，追加安全）。
- (22) FfmpegCoreLocator.ResolveRelative 限定 manifest 目录子树内解析（与 runtimeFiles 约束一致，防篡改 manifest 指向外部二进制）。
- (23) vp8 特判并入 OutputFormatCatalog.RemuxVideoCodecs。

## 明确暂缓项（功能扩展，不算未完成）

- HDR tonemap / 10-bit 输出（SDR↔HDR 转换）、zscale 路线。
- 字幕烧录（subtitles/overlay 滤镜）与外挂字幕导入。
- 2-pass / 恒定码率（CBR）模式。
- GIF/WebP/APNG 动图输出。
- 设备预设（手机/游戏机等）。
- HEVC/AV1 硬件编码（见 Minor 7）。
- 无损归档组合（FFV1/H.264 lossless）与 TS/FLV/MKA 等容器。

## 关键实测结论（固定核心 Gyan 8.0.1 full_build-shared）

1. 输出侧 -color_primaries/-color_trc 在该核心不生效（仅 -colorspace/-color_range 可用）；setparams= 滤镜段对 libx264/libx265/libvpx-vp9/libaom-av1/h264_nvenc 全部可靠。
2. MKV 源的 color_range=tv 无法写回（编码器包装层只取帧级 primaries/transfer/space 与 avctx range；实测 setparams range=pc 可、tv 不可）；Verifier 将 range 的 tv/unspecified 等价处理（所有解码器都把 unspecified 当 limited）。
3. FFmpeg 8 的 HDR 静态元数据（MDCV/CLL）位于帧级 side data，流级 -show_streams 不可见；probe 固定多读 1 帧合并。
4. NVENC 最小编码尺寸大于 128x72（试编码/回归测试用 160x96 以上）。

## 验证

- dotnet build TransFlux.slnx -c Debug -p:WindowsPackageType=None：0 警告 0 错误。
- dotnet test 全量：Domain 120、Application 96、Infrastructure 32、Engine.Integration 118，共 366 项全部通过（基线 265 项保留；Remux_BitmapSubtitle_IsBlocked 按新行为拆分为 Remux_BitmapSubtitle_ToMkv_IsAllowed 与 Remux_BitmapSubtitle_ToMp4_IsBlocked）。
- 新增回归：Domain 52 项（RemuxWhitelistTests/SubtitlePolicyTests/改造后的 MediaCompatibilityTests），Engine 15 项（PipelineRegressionTests：多音轨、语言/disposition、mov_text、附件直通、色彩传递、HDR 静态元数据、smpte432/HLG/位深、DOVI profile 解析）。
