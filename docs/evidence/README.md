# docs/evidence · 验证与发布证据

本目录只存放**真实执行**的构建、测试、样片与性能证据。规则（见 docs/09、docs/README.md 一致性规则）：

1. 只记录实际执行过的命令与真实输出；不得把设计计划、推测或「应该能通过」写成结果。
2. 未运行的项目必须显式标注 **未执行（Not executed）** 并写明原因与运行前置条件。
3. 每条证据包含：环境、命令、原始输出摘要、结论、未验证项。
4. 样片 hash、核心版本与 core-manifest 一致；不要把私人媒体提交仓库。

## 证据索引

| 文件 | 内容 | 状态 |
|---|---|---|
| [IMPLEMENTATION-STATUS.md](IMPLEMENTATION-STATUS.md) | 当前构建的实现状态、测试计数、限制与下一步 | 当前汇总 |
| [M0-toolchain.md](M0-toolchain.md) | M0 工具链/Reactor/核心/解决方案骨架 | 已执行（开发机，非干净 VM） |
| [QA-domain-tests.md](QA-domain-tests.md) | Domain 单元测试真实结果 + 样片清单校验 | 已执行 |
| [QA-media-samples.md](QA-media-samples.md) | 确定性样片生成、hash、解码校验 | 已执行 |
| [M1-cpu-slice.md](M1-cpu-slice.md) | M1 CPU 纵向切片（核心 smoke；App 管线部分已由 M1-E2E 补齐） | 部分执行（历史阶段） |
| [M1-E2E-runtime.md](M1-E2E-runtime.md) | 真实 probe→plan→encode→verify→commit 端到端 | 已执行 |
| [M2-persistence.md](M2-persistence.md) | M2 可靠批量与持久化故障注入模板 | 未执行（故障注入）；基础持久化测试见状态汇总 |
| [M3-hardware.md](M3-hardware.md) | DXGI 枚举与能力试探 | 部分执行（NVENC 实机未签核） |
| [M4-ui.md](M4-ui.md) | M4 完整 P0 产品体验/UI 早期模板 | 历史模板；当前见 M4-ui-runtime 与 UX-workbench-redesign |
| [M4-ui-runtime.md](M4-ui-runtime.md) | 原生窗口/UI 运行证据（Format-Factory 时代） | 已执行（历史界面） |
| [UX-workbench-redesign.md](UX-workbench-redesign.md) | 文件优先任务工作台重构（当时 189 项测试） | 已执行（开发机） |
| [OUTPUT-FORMATS.md](OUTPUT-FORMATS.md) | 输出目标分层、59 种组合真实编码/解码（当时 259 项，当前 265 项） | 已执行（开发机） |
| [INSTALLER.md](INSTALLER.md) | Inno Setup 安装包构建、安装/启动/E2E/卸载 | 已执行（开发机） |

## 运行约定

~~~
dotnet build TransFlux.slnx -c Debug -p:WindowsPackageType=None
dotnet test  TransFlux.slnx -c Debug
dotnet run --project tools/TransFlux.E2E/TransFlux.E2E.csproj -c Debug
pwsh -File tests/media/generate-samples.ps1
~~~

媒体核心固定于 tools/ffmpeg/bin/，版本与 SHA-256 记录在 [tools/ffmpeg/core-manifest.json](../../tools/ffmpeg/core-manifest.json)。

> 历史证据描述的是当时界面与测试计数，不代表当前构建；当前结论以 IMPLEMENTATION-STATUS、OUTPUT-FORMATS 和最新一次构建/测试记录为准。
