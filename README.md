# TransFlux

以 **WinUI 3 + Microsoft.UI.Reactor** 为界面核心的本地媒体转换软件。

产品主张：**拖入文件，选择用途，放心转换。** 通过可解释的硬件调度、可靠的输出保护与渐进式设置，在便捷操作、转换质量和吞吐之间取得平衡。

当前状态：产品与技术设计完成，尚未实现应用代码。本文档包不代表任何功能或性能已经验证。

## 开始阅读

- [开发文档总入口](docs/README.md)：阅读顺序、约束与文档地图。
- [项目定调与范围](docs/01-product-direction.md)：产品原则、版本边界、关键决策。
- [AI 编码交接总结](docs/12-ai-handoff.md)：下一位 AI 的启动指令、里程碑和验收要求。

技术基线：C#、.NET 10、原生 WinUI 3、Microsoft.UI.Reactor、FFmpeg/ffprobe 独立进程、SQLite。首发 Windows 11 22H2+ x64，ARM64 后续独立验证。准确版本由技术验证阶段锁定。

设计基线日期：2026-10-31（Asia/Shanghai）。应用自身沿用仓库 MIT 许可证；第三方媒体核心的分发许可独立管理。
## 实现状态（本次构建）

已实现可运行的纵向切片：Reactor 原生界面、真实 FFmpeg 探测/转码/验证/安全提交、SQLite 持久化与恢复、队列与状态机、硬件能力探测（DXGI）。完整构建 0 警告 0 错误，测试 171 项全部通过，真实端到端转换通过且源文件不变。

- 详细状态与未验证项：[docs/evidence/IMPLEMENTATION-STATUS.md](docs/evidence/IMPLEMENTATION-STATUS.md)
- 端到端证据：[docs/evidence/M1-E2E-runtime.md](docs/evidence/M1-E2E-runtime.md)
- 界面运行证据：[docs/evidence/M4-ui-runtime.md](docs/evidence/M4-ui-runtime.md)

构建 / 运行 / 测试：

```powershell
pwsh -File scripts/fetch-ffmpeg.ps1                       # 获取并校验固定核心
dotnet build TransFlux.slnx -c Debug                      # 构建全部项目
dotnet run --project src/TransFlux.App/TransFlux.App.csproj -c Debug -p:WindowsPackageType=None
dotnet test TransFlux.slnx -c Debug                       # 运行测试
dotnet run --project tools/TransFlux.E2E/TransFlux.E2E.csproj -c Debug   # 端到端真实转换
```