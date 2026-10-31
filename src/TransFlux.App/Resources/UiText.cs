namespace TransFlux.App.Resources;

/// <summary>User-facing copy (Chinese) centralized so screens stay consistent with docs/03.</summary>
public static class UiText
{
    public const string AppTitle = "TransFlux";

    // Navigation
    public const string NavConvert = "转换";
    public const string NavTasks = "任务";
    public const string NavPresets = "预设";
    public const string NavSettings = "设置";

    // Convert
    public const string AddFiles = "添加文件";
    public const string AddFolder = "添加文件夹";
    public const string EmptyTitle = "拖放视频或音频到这里";
    public const string EmptyHint = "支持 MP4、MKV、MOV、M4A、FLAC、WAV 等本地文件；目录会按支持的媒体类型扫描。";
    public const string EmptySecondary = "或使用上方按钮选择文件 / 文件夹";
    public const string OutputSettings = "输出设置";
    public const string AdvancedSettings = "高级设置";
    public const string PlanSummary = "转换计划";
    public const string AddToQueue = "加入队列";
    public const string StartConvert = "开始转换";
    public const string Reading = "正在读取";
    public const string RemoveSelected = "移除所选";
    public const string SelectAll = "全选";

    // Batch labels
    public const string Purpose = "用途";
    public const string Quality = "质量";
    public const string SpeedLabel = "速度";
    public const string Hardware = "硬件";
    public const string OutputLocation = "输出位置";
    public const string Container = "容器";
    public const string Streams = "轨道";
    public const string Resize = "缩放";
    public const string Trim = "裁剪";
    public const string Metadata = "元数据";

    // Quality descriptions (docs/03)
    public const string QualityBalancedHint = "均衡：兼顾画质与体积。实际大小取决于画面内容。";
    public const string QualitySmallerHint = "较小体积：降低码率，适合分享与存档。";
    public const string QualityHigherHint = "更高画质：保留更多细节，文件更大。";

    // Hardware caveat (docs/03)
    public const string HardwareCaveat = "硬件加速不保证更小或更快，取决于显卡、驱动与素材。";
    public const string HardwareAuto = "自动（优先已通过测试的硬件编码）";
    public const string HardwareCpu = "CPU 软件编码";
    public const string HardwareNvidia = "NVIDIA NVENC";
    public const string HardwareIntel = "Intel Quick Sync";
    public const string HardwareAmd = "AMD AMF";

    // Queue
    public const string QueueTitle = "任务队列";
    public const string PauseQueue = "暂停队列";
    public const string ResumeQueue = "恢复队列";
    public const string PauseHint = "当前任务继续，仅暂停开始新任务";
    public const string Cancel = "取消";
    public const string Retry = "重试";
    public const string Remove = "移除";
    public const string OpenOutput = "打开输出";
    public const string RevealOutput = "在资源管理器中显示";
    public const string History = "历史";
    public const string Diagnostics = "诊断";

    // Confirmations
    public const string CancelConfirmTitle = "取消转换？";
    public const string CancelConfirmBody = "将终止当前转换并删除未完成的临时文件，源文件不会被修改。";
    public const string Confirm = "确认";
    public const string CancelAction = "继续转换";

    // Presets
    public const string PresetsTitle = "预设";
    public const string BuiltIn = "内置";
    public const string Custom = "自建";
    public const string SaveAs = "另存为";
    public const string Delete = "删除";

    // Settings
    public const string SettingsTitle = "设置";
    public const string Appearance = "外观";
    public const string Language = "语言";
    public const string OutputRules = "输出规则";
    public const string Concurrency = "并发转换数";
    public const string AutoFallback = "自动能力回退";
    public const string LogCleanup = "日志与历史清理";
    public const string ThemeSystem = "跟随系统";
    public const string ThemeLight = "浅色";
    public const string ThemeDark = "深色";

    // Failure copy (docs/03)
    public const string HdrBlocked = "此文件包含 HDR 信息。当前版本支持兼容的转封装，HDR 转码暂未开放。";
    public const string DiskFull = "输出磁盘空间不足。请选择其他文件夹或释放空间后重试。";
    public const string Interrupted = "上次转换未完成，可以从头重试。";
    public const string NoEstimate = "无法准确估计";
}
