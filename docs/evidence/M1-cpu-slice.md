# M1 · CPU 纵向切片证据

日期：2026-10-31（Asia/Shanghai）。状态：**部分执行**（媒体核心 smoke 已执行；App 端到端管线未执行）。

## 环境

| 项 | 值 |
|---|---|
| OS | Windows 11（开发机，见 M0-toolchain.md） |
| .NET SDK | 10.0.401 |
| 媒体核心 | BtbN win64-gpl `N-128212-g17a4a474f4-20261030`（SHA-256 见 core-manifest.json） |
| 样片 | `tests/media/samples/S01_1080p_sdr_bt709_h264_aac_5s.mp4`（见 QA-media-samples.md） |

## 1) 媒体核心 smoke（裸 FFmpeg，**已执行**）

目的：确认固定核心可对 S01 做真实 H.264/AAC 转码，且不修改源文件。注意：这是**核心能力 smoke，不是 App/Engine 管线**。

~~~
ffmpeg -hide_banner -y -nostdin -loglevel error -i input.mp4 \
  -c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p \
  -c:a aac -b:a 128k -movflags +faststart output.mp4
~~~

真实输出摘要：

~~~
transcode exit=0
input sha256 before=5d259208575c5a0a9eef707b9dc3105a79dc19a9de212e8156e5b69f9f49d614
input sha256 after =5d259208575c5a0a9eef707b9dc3105a79dc19a9de212e8156e5b69f9f49d614
input unchanged=True
output exists=True size=4157960
--- output probe ---
codec_name=h264  codec_type=video width=1920 height=1080 pix_fmt=yuv420p
codec_name=aac   codec_type=audio sample_rate=48000 channels=2
format_name=mov,mp4,m4a,3gp,3g2,mj2  duration=5.000000
~~~

结论：核心可用，输出 1080p H.264（yuv420p）+ AAC 立体声 5s，源文件 hash 未变。

## 2) App 端到端管线（**未执行**）

待验证流程：导入 → ffprobe 归一化 → 计划编译 → 调度领取 → Runner 执行 → Verifier 校验 → 同卷临时输出 no-replace 提交 → Succeeded。

运行前置条件：

1. `dotnet build TransFlux.slnx -c Debug` 通过；
2. Engine（probe/planner/runner/verifier）与 Application（JobScheduler/RecoveryService）已由各自 owner 集成到组合根；
3. 可运行的打包/loose 应用或集成测试宿主。

待执行用例（docs/09）：T02（默认 CPU 转 MP4 正确性）、T06 基础（裁剪/旋转）、T10（无覆盖/无源修改）、T14（取消与完成终态唯一）、T18（exit 0 但输出不符 plan → Verifier 失败不发布）。

待填命令（示例，验证者按实际项目执行）：

~~~
dotnet build TransFlux.slnx -c Debug
# 运行 Engine.IntegrationTests / App 后用真实 S01 跑 T02
~~~

## 未验证 / 边界

- 未验证 GPU 路径（M3）：NVENC/QSV/AMF 全部未执行，不以「编码器名存在」代替可用。
- 未验证进度解析、取消进程树终止、验证容差的实际行为。
- 本节所有 App 管线结论待集成后由 owner/QA 补充真实输出。
