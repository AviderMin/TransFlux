# TransFlux

**1.0.0.9**：当前源码版本（`Directory.Build.props`）。最近一次有安装/卸载/E2E 证据的安装包为 1.0.0.8。

以 **WinUI 3 + Microsoft.UI.Reactor** 为界面核心的本地媒体转换软件。

产品主张：**拖入文件，选择用途，放心转换。** 通过可解释的硬件调度、可靠的输出保护与渐进式设置，在便捷操作、转换质量和吞吐之间取得平衡。

当前状态：已实现原生工作台、FFmpeg 转换管线、队列及持久化；已完成 G0 可靠性闸门与 G2 的 MKV 音频原样复制切片。详细完成清单与限制见 [已完成开发汇总](docs/DEVELOPMENT-SUMMARY.md)，后续路线见 [项目展望](docs/PROJECT-OUTLOOK.md)。

## 文档

- [已完成开发汇总](docs/DEVELOPMENT-SUMMARY.md)：已交付能力、架构、验证、限制与代码地图。
- [项目展望](docs/PROJECT-OUTLOOK.md)：G1–G7 路线图、近期优先级与验收要求。
- 发布/安装证据已并入 [已完成开发汇总](docs/DEVELOPMENT-SUMMARY.md) 的“验证状态”与“已知限制”章节。

技术基线：C#、.NET 10、原生 WinUI 3、Microsoft.UI.Reactor、FFmpeg/ffprobe 独立进程、SQLite。首发 Windows 11 22H2+ x64，ARM64 后续独立验证。

## 实现状态

已实现可运行的纵向切片：Reactor 原生界面、真实 FFmpeg 探测/转码/验证/安全提交、SQLite 持久化与恢复、队列与状态机、硬件能力探测（DXGI）、输出目标分层与 G2 MKV 音频复制。当前 Release 全量 **542 测试通过 / 0 失败**，真实 CPU E2E PASS。

构建 / 运行 / 测试：

```powershell
pwsh -File scripts/fetch-ffmpeg.ps1                       # 获取并校验固定核心
dotnet build TransFlux.slnx -c Release                    # 构建全部项目
dotnet run --project src/TransFlux.App/TransFlux.App.csproj -c Release -p:WindowsPackageType=None
dotnet test TransFlux.slnx -c Release                     # 运行测试
dotnet run --project tools/TransFlux.E2E/TransFlux.E2E.csproj -c Release   # 端到端真实转换
```

设计基线日期：2026-10-31（Asia/Shanghai）。应用自身沿用仓库 MIT 许可证；第三方媒体核心的分发许可独立管理。
