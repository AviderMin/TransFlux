# G2 音频原样复制切片 — 验证证据

日期：2026-11-04。代码基线：当前未提交工作区（应用版本 1.0.0.9）。核心：Gyan FFmpeg 8.0.1 full_build-shared，core manifest 校验通过；E2E 输出的 core hash 前缀 `E2738D939693`。

## 范围

本切片只开放：**视频转码（VideoTranscode）+ MKV 容器 + AudioCodec.Copy**。

- UI 音频编码选项新增“复制原始音轨（不重新编码）”，明确提示编码、声道和码率保留自源文件；Atmos 等空间音频不做单独验证。
- 仅 MKV；MP4/MOV/WebM 等仍要求重新编码音频，切换容器会自动回退到目标容器的第一个兼容编码。
- 逐条解析后的源音轨 codec 必须在 `OutputFormatCatalog.RemuxAudioCodecs(MKV)` 白名单中（AC3/EAC3/TrueHD/DTS/FLAC/PCM 等），否则任务开始前以 `AUDIO_COPY_UNSUPPORTED` 阻断。
- 计划命令使用 `-c:a copy`，不生成 `-b:a`/`-ar`/`-ac`；ExpectedOutput.AudioCodec=Copy，TrackSignatures 按源音轨实际 codec/声道校验。
- 不把 Copy 加入普通编码矩阵；Remux 的原有 Copy 语义不变。

## 验证

### 构建与全量测试

```powershell
dotnet build TransFlux.slnx -c Release
# 0 warnings / 0 errors

dotnet test TransFlux.slnx -c Release --no-build
# 542 passed / 0 failed
# Domain 170 / Application 137 / Infrastructure 65 / Engine 170
```

### 新增测试

- Domain `MediaCompatibilityTests`：MKV+Copy 允许 AC3/EAC3/TrueHD；MP4 阻断；非白名单源 codec 阻断；多轨中任一不可复制阻断；无音轨/None 阻断；Copy 不在普通编码矩阵。
- Engine `PlannerTests`：`-c:a copy`、无码率/采样率/声道参数、ExpectedOutput.AudioCodec=Copy、源 AC3 6ch 签名、rationale 为 audio copy。
- Engine `PipelineRegressionTests`：真实生成 h264 + AC3 6ch MKV，执行视频转码并复制音频；输出仍为 ac3/6ch，验证器通过；篡改 TrackSignatures codec=aac 时验证器拒绝。
- Application `OutputSelectionTests`：Copy 仅视频转码 MKV 可选；Copy→MP4 回退 AAC、Copy→WebM 回退 Opus；AudioExtract 不显示 Copy；Remux/WithVideoCodec(Copy) 语义保持；旧预设/草稿 Copy+MP4 归一化。

### 真实 E2E

`dotnet run --project tools/TransFlux.E2E/TransFlux.E2E.csproj -c Release --no-build` → `E2E_RESULT=PASS`（3.48s，源文件 SHA 不变，CPU libx264；该 E2E 是通用 SDR 路径，音频复制路径由上述 Engine integration 覆盖）。

## 未验证 / 边界

- EAC3/TrueHD 只有 Domain 规则与 synthetic 测试，没有真实样片转码证据。
- MP4/MOV/WebM 音频复制未开放；Dolby Atmos 未做对象/Atmos 证据检测，UI 明确不宣称 Atmos 保留。
- 没有 UI 自动化/截图证据；UI 文案由共享常量单测覆盖，渲染需后续人工验收。
- 该切片未生成新安装包；安装目录验证仍需发布时执行。
