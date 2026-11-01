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

    // ---- Format-Factory workbench (new) ----
    public const string TargetFormat = "目标格式";
    public const string TargetFormatHint = "选择输出格式，然后添加文件。";
    public const string Start = "开始";
    public const string Pause = "暂停";
    public const string Resume = "恢复";
    public const string DeleteSelected = "删除";
    public const string BatchSettings = "批处理设置";
    public const string ClearSelection = "取消选择";
    public const string NoFilesTitle = "添加文件，开始转换";
    public const string NoFilesHint = "将视频或音频拖放到这里，或使用左侧/上方按钮选择文件。";
    public const string Ready = "待转换";
    public const string Converting = "转换中";
    public const string Done = "已完成";
    public const string FailedText = "失败";
    public const string InvalidText = "无法转换";
    public const string AboutTitle = "关于 TransFlux";
    public const string ParameterTitle = "转换参数";
    public const string SavePreset = "保存为预设";
    public const string PresetNamePlaceholder = "新预设名称";
    public const string NoPresets = "暂无可用预设。";
    public const string OutputDirectory = "输出目录";
    public const string OutputFileName = "文件名";
    public const string ChooseFolder = "选择…";
    public const string DefaultOutput = "默认（源文件旁的 TransFlux Output）";
    public const string LeaveAuto = "留空自动";
    public const string DirtySelectionHint = "所选文件的设置不同，修改会同时应用到所有选中项。";
    public const string BatchTitleFormat = "批处理设置（{0} 项）";
    public const string SaveSettingsAction = "保存";

    // ---- P1 interactions ----
    public const string SelectedCountFormat = "已选 {0} 项";
    public const string UndoDelete = "撤销";
    public const string DeletedCountFormat = "已删除 {0} 项";
    public const string UndoDeleteHint = "撤销可把这些任务重新加回列表。";
    public const string ConversionCompleteFormat = "转换完成：{0}";
    public const string OpenOutputFolder = "打开输出文件夹";
    public const string HistoryRecords = "历史记录";
    public const string HistoryEmpty = "暂无历史记录。";
    public const string HistoryLoading = "正在读取历史记录…";
    public const string HistoryClear = "清空历史";
    public const string HistoryClearConfirm = "确定清空全部历史记录？";
    public const string HistoryClearHint = "只删除历史记录，不会删除已生成的输出文件。";
    public const string HistoryConfirmClear = "确认清空";
    public const string Rejoin = "重新加入";
    public const string OpenFolder = "打开文件夹";

    // ---- UI/UX fix round (2026-11-01) ----
    // Notice titles and bodies shared by the four-level notification channel.
    public const string PromptTitle = "提示";
    public const string ErrorTitle = "出错";
    public const string PresetTitle = "预设";
    public const string SaveFailedTitle = "保存失败";
    public const string SaveFailedPrefix = "保存失败：";
    public const string SettingsLoadFailedTitle = "设置读取失败";
    public const string SettingsLoadFailedBody = "已使用默认设置：";
    public const string ImportFailedTitle = "导入失败";
    public const string ImportFailedPrefix = "导入失败：";
    public const string FolderScanFailedTitle = "文件夹导入失败";
    public const string FolderScanFailedPrefix = "文件夹导入失败：";
    public const string ExitStopFailedTitle = "退出失败";
    public const string ExitStopFailedPrefix = "退出前停止任务失败：";
    public const string ExitTimeout = "停止任务超时，仍有任务未结束。请稍后重试，或使用任务卡片逐项取消。";
    public const string WorkbenchRestoreFailedTitle = "工作台恢复失败";
    public const string WorkbenchRestoreFailedPrefix = "工作台恢复失败：";
    public const string JobWatchFailedTitle = "任务状态读取失败";
    public const string JobWatchFailedPrefix = "任务状态读取失败，请重新打开应用：";
    public const string EnqueueFailedTitle = "任务加入失败";
    public const string EnqueueFailedPrefix = "任务加入失败：";
    public const string PersistFailedTitle = "工作台保存失败";
    public const string PersistFailedPrefix = "工作台保存失败：";
    public const string CancelRejectedTitle = "无法取消";
    public const string CancelRejected = "任务已进入保存阶段或已经结束，无法取消；请等待状态更新。";
    public const string CancelRequestedInfo = "已请求取消；卡片会保持到任务停止。";
    public const string EditQueuedRejectedTitle = "无法移出队列";
    public const string EditQueuedRejected = "任务已经开始，无法移出队列；可取消或复制为新任务。";
    public const string SettingsStillLoading = "设置仍在加载，请稍后重试。";
    public const string NothingImported = "未添加文件：文件可能已在待开始列表中、已不存在，或工作台仍在恢复。";
    public const string ScanBusy = "正在扫描文件夹，请先取消或等待完成。";
    public const string NoMediaFound = "未发现支持的媒体文件。";
    public const string NoNewMedia = "未发现新的媒体文件；文件可能已在待开始列表中。";
    public const string ScanCanceledFormat = "已取消文件夹扫描；已添加的 {0} 项保留。";
    public const string ScanCanceled = "已取消文件夹扫描。";
    public const string DiagnosticSaved = "诊断报告已保存。";
    public const string CloseDialogFirst = "请先关闭当前对话框，再退出应用。";

    // Workbench chrome copy.
    public const string AppSubtitle = "格式转换工作台";
    public const string DropHint = "松开以添加文件和文件夹";
    public const string AddFilesTip = "添加文件（Ctrl+O）";
    public const string AddFolderTip = "添加文件夹（Ctrl+Shift+O）";
    public const string MoveUpTip = "在队列中上移";
    public const string MoveDownTip = "在队列中下移";
    public const string MoreIssuesFormat = "另有 {0} 条提示，请打开“技术详情”查看。";
    public const string CompatibilityLabel = "兼容性提示";
    public const string ResetConfirmTitle = "重置所选参数？";
    public const string ResetConfirmBody = "将所选项目的输出参数恢复为默认值，并清除输出文件名、裁剪范围与音轨选择。";
    public const string ResetConfirmAction = "重置参数";

    // Settings dialog copy.
    public const string SettingsDirtyNotice = "更改尚未保存。再次点击“放弃更改”将丢弃这些更改。";
    public const string DiscardChanges = "放弃更改";

    // Preset copy.
    public const string PresetHint = "预设只填入参数；手动改动后会自动显示为“自定义”。";
    public const string PresetCustomNotice = "“自定义”不是预设，仅表示当前参数与所有预设不同。";
    public const string PresetNameRequired = "请填写预设名称。";
    public const string PresetSaved = "已保存预设。";

    // History dialog copy.
    public const string HistoryLoadFailedTitle = "历史记录读取失败";
    public const string HistoryCleared = "已清空历史记录。";
    public const string HistoryClearFailedTitle = "清空历史失败";
    public const string RejoinDone = "已重新加入工作台，正在读取媒体信息。";
    public const string RejoinFailedExists = "无法重新加入：文件不存在或已在任务列表中。";
    public const string RejoinFailedTitle = "重新加入失败";
    public const string OpenOutputFailedTitle = "打开输出失败";
    public const string RevealFailedTitle = "打开文件夹失败";
}
