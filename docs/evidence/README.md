# docs/evidence · 验证与发布证据

本目录只存放**真实执行**的构建、测试、样片与性能证据。规则（见 docs/09、docs/README.md 一致性规则）：

1. 只记录实际执行过的命令与真实输出；不得把设计计划、推测或「应该能通过」写成结果。
2. 未运行的项目必须显式标注 **未执行（Not executed）** 并写明原因与运行前置条件。
3. 每条证据包含：环境、命令、原始输出摘要、结论、未验证项。
4. 样片 hash、核心版本与 core-manifest 一致；不要把私人媒体提交仓库。

## 证据索引

| 文件 | 内容 | 状态 |
|---|---|---|
| [M0-toolchain.md](M0-toolchain.md) | M0 工具链/Reactor/核心/解决方案骨架（Lead 维护） | 已执行（开发机，非干净 VM） |
| [QA-domain-tests.md](QA-domain-tests.md) | Domain 单元测试真实结果 + 样片清单校验 | 已执行 |
| [QA-media-samples.md](QA-media-samples.md) | 确定性样片生成、hash、解码校验 | 已执行 |
| [M1-cpu-slice.md](M1-cpu-slice.md) | M1 CPU 纵向切片（核心 smoke 已执行；App 管线未执行） | 部分执行 |
| [M2-persistence.md](M2-persistence.md) | M2 可靠批量与持久化 | 未执行（待集成） |
| [M4-ui.md](M4-ui.md) | M4 完整 P0 产品体验/UI | 未执行（待集成） |

## 运行约定

~~~
dotnet build TransFlux.slnx -c Debug
dotnet test  tests/TransFlux.Domain.Tests/TransFlux.Domain.Tests.csproj -c Debug
pwsh -File  tests/media/generate-samples.ps1
~~~

媒体核心固定于 `tools/ffmpeg/ffmpeg-master-latest-win64-gpl/bin/`，版本与 SHA-256 记录在 [`tools/ffmpeg/core-manifest.json`](../../tools/ffmpeg/core-manifest.json)。
