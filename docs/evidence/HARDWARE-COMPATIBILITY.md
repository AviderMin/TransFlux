# 硬件兼容性修复 · 1.0.0.5

日期：2026-11-01。修复来自设置页 NVIDIA 检测失败的反馈。

## 根因和处理

- 旧探测直接将 `LUID-…` 拼进参数，被 FFmpeg 当作输出文件。LUID 现在仅为身份；另外记录 DXGI ordinal，通过 `-init_hw_device d3d11va=tfbase:<index>` 创建设备。QSV 从 D3D11 派生；NVENC/AMF 接收该设备的硬件帧。探测和转换共用 HardwareEncoding，不能将 DXGI ordinal 当成 NVENC `-gpu` 索引。
- 原 nightly 核心还要求 NVENC API 13.1 / 驱动 610.00，本机 591.86 提供 API 13.0，修正参数仍失败。固定为实测通过的 **Gyan FFmpeg 8.0.1 full_build-shared / GPLv3**，没有修改驱动。下载的 9.0.2、8.1.2 构建也有相同 API 要求。
- H.264 自动模式改为已通过试编码的 GPU → CPU（最多一次自动回退）。选定 adapterId 参与过滤并进入执行记录。HEVC/AV1/VP9 仍为 CPU，未增加这些编码器的 GPU 支持。
- 多适配器逐一试跑，软件设备排除。缓存 schema 3 包含核心、驱动、设备、DXGI ordinal、目标编码器；未试跑结果不落盘，失败缓存 10 分钟，成功最多 7 天。取消不记作不兼容。转换前完成必要试跑，设置页可强制重测。
- 驱动/API 和硬件初始化错误可回退；磁盘空间/权限错误优先归类。设置页完整设备名可换行，失败原因简明，原始 stderr 按需展开。
- manifest 固定归档、两个 exe 和七个 DLL 的哈希；coreHash 包含运行库哈希。下载先校验全部暂存文件，打包只复制 manifest 文件，避免夹带旧核心 DLL。

## 实测与复现

Windows 11，NVIDIA GeForce RTX 4060 Laptop GPU，驱动 591.86（Windows 版本 32.0.15.9186）。DXGI 返回两个 NVIDIA 逻辑适配器，均试跑通过，不代表两张物理显卡。Intel/AMD 没有对应实机，**未做实机验收**；设备派生参数及选择逻辑由自动化测试覆盖。

```powershell
pwsh -File scripts/fetch-ffmpeg.ps1 -Force
dotnet test TransFlux.slnx -c Debug -p:WindowsPackageType=None
dotnet run --project tools/TransFlux.E2E -- --hardware
dotnet run --project tools/TransFlux.E2E -- --fallback-smoke
pwsh -File tools/package-exe.ps1
```

- 格式回归包含界面公开的 **59 种容器/编码组合**，实际编码和验证均通过，没有缩小格式范围。
- 全量 **271 项测试通过**（Domain 83、Application 54、Infrastructure 31、Engine 103），Debug 构建 0 警告/0 错误。新增覆盖未试跑缓存、多显卡绑定、目标缓存隔离、三个后端计划及 DLL 篡改后重新验证。
- `--hardware`：真实调度器、SQLite、NVENC、验证和提交；1280×720 / 5 秒 H.264 + AAC，一个 GPU attempt，约 1.3 秒完成；源文件哈希不变，输出再次完整解码通过。
- `--fallback-smoke`：仅 E2E 工具注入不兼容的 H.264 level，真实 FFmpeg 返回初始化失败，第二个 CPU attempt 成功提交；源文件不变，结果完整解码通过。注入不在应用中。
- 不能用不存在的 DXGI index 验证回退：实测 FFmpeg 会静默使用默认设备，导致假通过。
- 日志：忽略的 `artifacts/hardware-compat/hardware-e2e.log`、`fallback-e2e.log`，临时工作目录在各日志首行。
- 通过 computer-use 技能操作隔离数据目录的 Debug 应用：设置页完整显示设备名，NVENC 显示“试编码通过，可用”，点击“重新检测硬件”后恢复可用。截图保存在 `artifacts/hardware-compat/settings.png`。未操作用户正在运行的旧版窗口。
- 最终安装 payload 内的核心通过 `TRANSFLUX_FFMPEG_DIR` 指定后，再次完整 GPU E2E PASS，核心集合哈希与开发目录一致；日志为 `payload-e2e.log`。此次没有替换用户的已安装应用，也未宣称完成新版安装/卸载测试。

SDR H.264 通过不推断任意尺寸/位深/负载通过；实际任务保留回退。跨 CPU/GPU 质量档尚未标定；未宣称 GPU 解码/滤镜已实现。

## 来源与后续维护

[Gyan 8.0.1 发布](https://github.com/GyanD/codexffmpeg/releases/tag/8.0.1)、[构建许可说明](https://www.gyan.dev/ffmpeg/builds/)、[FFmpeg 设备初始化文档](https://ffmpeg.org/ffmpeg.html)。归档、源码 tag、精确哈希见 [manifest](../../tools/ffmpeg/core-manifest.json)。GPL 分发路线不变。

这是实测兼容基线；升级核心须再次验证驱动 API 基线、GPU 转换与回退、全部格式及 DLL 完整性，再更新固定归档和 manifest，不能改为无版本 latest 链接。
