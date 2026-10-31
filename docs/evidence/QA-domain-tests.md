# QA · Domain 单元测试证据

日期：2026-10-31（Asia/Shanghai）。状态：**已执行**（开发机）。

## 环境

| 项 | 值 |
|---|---|
| OS | Windows 11（开发机，见 M0-toolchain.md） |
| .NET SDK | 10.0.401 |
| 测试运行时 | .NET 10.0.12 |
| Git HEAD | 4100ff9（工作区含并行开发中的未提交改动） |
| 测试项目 | `tests/TransFlux.Domain.Tests`（xunit 2.9.3 / runner 3.1.4） |

## 命令与结果

~~~
dotnet test tests/TransFlux.Domain.Tests/TransFlux.Domain.Tests.csproj -c Debug
~~~

真实输出摘要：

~~~
已通过! - 失败: 0，通过: 83，已跳过: 0，总计: 83，持续时间: 209 ms - TransFlux.Domain.Tests.dll (net10.0)
~~~

## 覆盖范围（trait：输入/预期表与关键不变量）

| 测试类 | 覆盖点 | 用例数 |
|---|---|---|
| `RationalTests` | Create 合法/零分母/空、ToDouble 帧率、ToString、值相等 | 9 |
| `TrimRangeTests` | 合法区间、start<0、end<=start、end>duration、未知时长、end==duration | 8 |
| `JobStateMachineTests` | 全部 12×12 迁移矩阵、非法迁移抛异常、IsTerminal/AllowsCancel/IsActive | 10 |
| `MediaCompatibilityTests` | 转码 SDR 通过/HDR/未知色彩/无视频/容器/位深/像素格式/裁剪越界；音频提取容器-编码匹配/采样率；转封装白名单/DV/位图字幕/TRIM；流选择 | 38 |
| `OutputNamingTests` | 非法字符、尾部点空格、保留名、控制字符、长度截断、唯一命名、默认输出目录 | 13 |
| `BuiltInPresetsTests` | 4 个内置预设唯一、按 id 获取、未知 id 抛异常 | 3 |
| `MediaSampleManifestTests` | manifest 存在/可解析、包含 S01–S09、purpose/hash 格式、生成文件 hash+size 与记录一致 | 4 |

> 说明：`MediaSampleManifestTests` 会在样片文件存在时逐个校验 SHA-256 与大小；若样片未生成则只做结构校验（先运行 `tests/media/generate-samples.ps1`）。

## 未验证 / 边界

- 本证据只覆盖 **Domain 纯领域规则**，不含 Application/Engine/Infrastructure 集成测试。
- 未在干净 VM 上运行；性能与硬件矩阵未测。
- 并行开发期间其他项目可能仍在改动；本结果仅对应上述命令与提交时点。
