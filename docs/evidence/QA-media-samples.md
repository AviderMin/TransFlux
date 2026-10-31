# QA · 确定性样片与清单证据

日期：2026-10-31（Asia/Shanghai）。状态：**已执行**（开发机）。

## 生成命令

~~~
pwsh -NoProfile -File tests/media/generate-samples.ps1
~~~

生成器使用固定核心 `tools/ffmpeg/ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe`（`N-128212-g17a4a474f4-20261030`），编码带 `-fflags +bitexact -map_metadata -1 -threads 1`，样片由 lavfi `testsrc2`/`sine` 合成，不使用任何私人媒体。

## 产出与 hash（`tests/media/manifest.json`）

| id | 文件 | 字节 | sha256 |
|---|---|---|---|
| S01 | S01_1080p_sdr_bt709_h264_aac_5s.mp4 | 4,243,154 | `5d259208575c5a0a9eef707b9dc3105a79dc19a9de212e8156e5b69f9f49d614` |
| S02 | S02_vertical_1080x1920_sdr_h264_aac_5s.mp4 | 4,576,576 | `4eb0bcc9bc16f473458e82ca1ceaaad409be6fbece1439111e0d7ecfb6b2e370` |
| S03 | S03_nonsquare_sar_1440x1080_sdr_h264_aac_5s.mp4 | 3,180,443 | `17f8611d840038106ab1d17dfab49de26e1e52855a35319965bf6a912c6ff392` |
| S04 | S04_no_audio_1080p_h264_5s.mp4 | 4,160,912 | `e7031f02732673cecfd25db314b42291c808695909a46e0350ad3d9bdce8a6e5` |
| S05 | S05_audio_only_aac_5s.m4a | 79,751 | `2b65efde3e0b066259dab7d90bdc2d4ea2afdd7298140b93ded029ab05f94b04` |
| S06 | S06_5.1_surround_h264_aac_5s.mkv | 4,403,968 | `b0024e60ae4741468a72f12de99579433e98d104f3bb58003f7417d7af5eb079` |
| S07 | S07_text_subtitle_h264_aac_srt_5s.mkv | 2,058,431 | `f65af85212b3eeec8d10d5bc7f1efac6e72fc06b26ae7c72f112afe2200ba218` |
| S08 | S08_truncated_from_S01.mp4 | 1,697,261 | `1da5f3874bccdafca932397ea972c33d8d7db35b6126081bf075156a0c6d9458` |
| S09 | 特殊 目录 & test/S09 空格 中文字符 & test (1).mp4 | 4,243,154 | `5d259208575c5a0a9eef707b9dc3105a79dc19a9de212e8156e5b69f9f49d614` |

S09 为 S01 的副本，放在含中文、空格、`&`、括号的路径下；S08 为 S01 按 40% 字节长度截断。

## 确定性校验

连续执行生成脚本 3 次，九个样片的 SHA-256 与字节大小完全一致（见上表），表明在固定核心上可复现。`manifest.json` 的 `generatedUtc` 每次更新，其余字段一致。

## 解码/探测校验（真实输出摘要）

~~~
# S01 全解码（期望成功）
ffmpeg -v error -xerror -i S01_1080p_sdr_bt709_h264_aac_5s.mp4 -f null -
# -> exit 0（无错误输出）

# S08 截断样片全解码（期望失败）
ffmpeg -v error -xerror -i S08_truncated_from_S01.mp4 -f null -
# -> exit -1094995529 (AVERROR_INVALIDDATA)
#    stderr: Invalid NAL unit size (28210 > 724) / corrupt input packet in stream 0

# S06 音轨：aac 6ch 5.1 48000Hz；S07 字幕：subrip
~~~

> 注意：不加 `-xerror` 时 ffmpeg 会容错跳过损坏数据并返回 0；因此判断截断样片必须用 `-xerror`（或检查 stderr）。

## 未验证 / 边界

- 未生成 docs/09 的 S06（10-bit HDR10/Dolby Vision/未知色彩）、S08 4K 长片、S09（硬链接/重解析点/网络路径）等样本；首发可通过手工制作或后续补充脚本。
- 未在干净机器上重放；hash 取决于固定核心版本。
