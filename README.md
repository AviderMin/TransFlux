# TransFlux

**1.0.0.5 硬件兼容修复**：修正设备绑定、硬件检测和自动回退，固定兼容核心；[实测与开发记录](docs/evidence/HARDWARE-COMPATIBILITY.md)。

以 **WinUI 3 + Microsoft.UI.Reactor** 为界面核心的本地媒体转换软件。

产品主张：**拖入文件，选择用途，放心转换。** 通过可解释的硬件调度、可靠的输出保护与渐进式设置，在便捷操作、转换质量和吞吐之间取得平衡。

当前状态：已实现原生工作台、FFmpeg 转换管线、队列及持久化。2026-11-01 工作台改为文件优先、右侧所选参数、完整任务卡片生命周期，详见 [UX 规范](docs/03-ui-ux.md) 与 [重构验证记录](docs/evidence/UX-workbench-redesign.md)。

## 开始阅读

- [开发文档总入口](docs/README.md)：阅读顺序、约束与文档地图。
- [项目定调与范围](docs/01-product-direction.md)：产品原则、版本边界、关键决策。
- [AI 编码交接总结](docs/12-ai-handoff.md)：下一位 AI 的启动指令、里程碑和验收要求。

技术基线：C#、.NET 10、原生 WinUI 3、Microsoft.UI.Reactor、FFmpeg/ffprobe 独立进程、SQLite。首发 Windows 11 22H2+ x64，ARM64 后续独立验证。准确版本由技术验证阶段锁定。

设计基线日期：2026-10-31（Asia/Shanghai）。应用自身沿用仓库 MIT 许可证；第三方媒体核心的分发许可独立管理。
## 实现状态（本次构建）

已实现可运行的纵向切片：Reactor 原生界面、真实 FFmpeg 探测/转码/验证/安全提交、SQLite 持久化与恢复、队列与状态机、硬件能力探测（DXGI）。输出目标按媒体形式、容器与编码分别选择，支持 11 种容器/输出类型，完整兼容矩阵和验证范围见 [格式扩展记录](docs/evidence/OUTPUT-FORMATS.md)。

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
