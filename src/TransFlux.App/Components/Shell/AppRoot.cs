using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Dialogs;
using TransFlux.App.Components.Shared;
using TransFlux.App.Components.Workbench;
using TransFlux.App.Interop;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;

namespace TransFlux.App.Components.Shell;

/// <summary>Native shell: selected-card inspector, lifecycle cards and a single execution bar.</summary>
public sealed class AppRoot : Component<AppServices>
{
    private Func<IReadOnlyList<string>, Task<int>>? _import;
    private Func<string, Task>? _importFolder;
    private Action<Notice?>? _notice;
    private Action<bool>? _dropChanged;
    private CancellationTokenSource? _folderScan;
    private DebouncedAction? _saveDebounce;
    private bool _allowClose, _modal;

    public override Element Render()
    {
        var services = Props;
        var coreMissing = string.IsNullOrWhiteSpace(services.CoreIdentity.FfmpegPath);
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var window = UseWindow()?.NativeWindow;
        var store = UseMemo(() => new WorkbenchStore(services.Jobs, services.Probe, services.Workspace,
            action => { if (dispatcher is null || dispatcher.HasThreadAccess) action(); else dispatcher.TryEnqueue(() => action()); },
            services.Options.MaxConcurrentProbes), services);
        var saveDebounce = UseMemo(() => new DebouncedAction(TimeSpan.FromMilliseconds(400)), services);
        _saveDebounce = saveDebounce;
        var snapshot = UseExternalStore(store.Subscribe, store.GetSnapshot);
        var initialSettings = UseMemo(() => new AppSettings(), Array.Empty<object>());
        var (settings, setSettings) = UseState(initialSettings);
        var (settingsLoaded, setSettingsLoaded) = UseState(false);
        var (defaults, setDefaults) = UseState(ConversionOptions.Default);
        var (presets, setPresets) = UseState<IReadOnlyList<Preset>>(Array.Empty<Preset>());
        var (recursiveFolderImport, setRecursiveFolderImport) = UseState(false);
        var (folderScanning, setFolderScanning) = UseState(false);
        var (scanFound, setScanFound) = UseState(0);
        var (filter, setFilter) = UseState(0);
        var (dialog, setDialog) = UseState<string?>(null);
        var (dialogKey, setDialogKey) = UseState(0);
        var (detailsId, setDetailsId) = UseState<Guid?>(null);
        var (cancelId, setCancelId) = UseState<Guid?>(null);
        var (resetOpen, setResetOpen) = UseState(false);
        var (dragOver, setDragOver) = UseState(false);
        var (exitOpen, setExitOpen) = UseState(false);
        var (exiting, setExiting) = UseState(false);
        _modal = dialog is not null || detailsId is not null || cancelId is not null || resetOpen || exitOpen;
        _notice = store.SetNotice;
        _dropChanged = setDragOver;

        UseEffect(() =>
        {
            store.Start();
            return () => { _folderScan?.Cancel(); _folderScan?.Dispose(); saveDebounce.Dispose(); store.Dispose(); };
        }, store);
        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _ = LoadAsync();
            async Task LoadAsync()
            {
                try
                {
                    var loaded = await services.Settings.GetAsync(cts.Token);
                    setSettings(loaded);
                    var format = TargetFormats.Find(loaded.LastTargetFormatKey ?? "") ?? TargetFormats.Default;
                    var quality = Enum.TryParse<QualityTier>(loaded.LastQuality, out var q) && Enum.IsDefined(q) ? q : QualityTier.Balanced;
                    setDefaults(format.ApplyTo(ConversionOptions.Default) with { Quality = quality,
                        OutputDirectory = loaded.DefaultOutputDirectory,
                        MetadataPolicy = loaded.PreserveTitleAndChaptersByDefault ? MetadataPolicy.PreserveTitleAndChapters : MetadataPolicy.StripPersonalTags });
                    setSettingsLoaded(true);
                    setPresets(await services.Presets.GetAllAsync(cts.Token));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // Fall back to in-memory defaults rather than leaving the workbench permanently disabled.
                    setSettingsLoaded(true);
                    store.SetNotice(Notices.SettingsLoadFailed(ex.Message));
                }
            }
            return () => cts.Cancel();
        }, services);

        UseEffect(() =>
        {
            if (window is null) return () => { };
            void Closing(AppWindow sender, AppWindowClosingEventArgs e)
            {
                if (_allowClose) return;
                e.Cancel = true;
                if (_modal) { store.SetNotice(Notices.CloseDialogFirst()); return; }
                var current = store.GetSnapshot();
                if (!current.Loaded) { _allowClose = true; window.Close(); return; }
                if (current.Busy || current.Items.Any(i => i.Execution?.IsActive == true)) setExitOpen(true);
                else _ = CloseSafelyAsync();
            }
            async Task CloseSafelyAsync()
            {
                await saveDebounce.FlushAsync();
                await services.Jobs.PauseQueueAsync(CancellationToken.None);
                if (!await store.PersistAsync()) return;
                _allowClose = true; window.Close();
            }
            window.AppWindow.Closing += Closing;
            return () => window.AppWindow.Closing -= Closing;
        }, window, store);

        async Task RunAsync(Func<Task> action)
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { store.SetNotice(Notices.OperationFailed(ex.Message)); }
        }
        void Run(Func<Task> action) => _ = RunAsync(action);
        void OpenDialog(string name)
        {
            if (name == "settings" && !settingsLoaded) return;
            setDialogKey(dialogKey + 1); setDialog(name);
        }

        async Task<int> ImportAsync(IReadOnlyList<string> paths, bool notifyEmpty = true)
        {
            if (!settingsLoaded)
            {
                if (notifyEmpty) store.SetNotice(Notices.SettingsStillLoading());
                return 0;
            }
            var count = await store.ImportAsync(paths, defaults);
            if (count == 0 && notifyEmpty) store.SetNotice(Notices.NothingImported());
            else if (count > 0) setFilter(0);
            return count;
        }
        _import = paths => ImportAsync(paths);
        async Task ImportFilesAsync()
        {
            var paths = await FilePickerInterop.PickFilesAsync(window);
            if (paths.Count > 0) await ImportAsync(paths);
        }
        async Task ScanFolderAsync(string folder)
        {
            if (!settingsLoaded || _folderScan is not null)
            {
                store.SetNotice(_folderScan is not null ? Notices.ScanBusy() : Notices.SettingsStillLoading());
                return;
            }
            var cts = new CancellationTokenSource();
            _folderScan = cts;
            setFolderScanning(true); setScanFound(0);
            var discovered = 0;
            var imported = 0;
            try
            {
                var excluded = snapshot.Items.Select(i => i.Draft.Options.OutputDirectory)
                    .Concat(new[] { settings.DefaultOutputDirectory, defaults.OutputDirectory })
                    .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToArray();
                await Task.Run(() => FilePickerInterop.EnumerateMediaFilesAsync(folder, recursiveFolderImport,
                    async batch =>
                    {
                        discovered += batch.Count;
                        setScanFound(discovered);
                        imported += await ImportAsync(batch, notifyEmpty: false);
                    }, cts.Token, excluded));
                store.SetNotice(imported == 0 ? discovered == 0 ? Notices.NoMediaFound() : Notices.NoNewMedia() : null);
            }
            catch (OperationCanceledException)
            {
                store.SetNotice(Notices.ScanCanceled(imported));
            }
            catch (Exception ex) { store.SetNotice(Notices.FolderScanFailed(ex.Message)); }
            finally
            {
                if (ReferenceEquals(_folderScan, cts)) _folderScan = null;
                setFolderScanning(false);
                cts.Dispose();
            }
        }
        _importFolder = ScanFolderAsync;
        async Task ImportFolderAsync()
        {
            if (folderScanning) return;
            var folder = await FilePickerInterop.PickFolderAsync(window);
            if (folder is not null) await ScanFolderAsync(folder);
        }
        var selected = snapshot.Selection;
        var selectedIds = selected.Select(i => i.Id).ToArray();
        async Task ApplyAsync(Func<ConversionOptions, ConversionOptions> mutate)
        {
            if (selected.Count == 0 || selected.Any(i => !i.CanEdit)) return;
            await store.EditAsync(selectedIds, mutate);
            var nextDefaults = mutate(selected[0].Draft.Options) with { OutputFileName = null, TrimStart = null, TrimEnd = null, SelectedStreams = [] };
            setDefaults(nextDefaults);
            var target = TargetFormats.Find(nextDefaults);
            var remembered = settings with { LastTargetFormatKey = target?.Key, LastQuality = nextDefaults.Quality.ToString() };
            setSettings(remembered);
            // Per-keystroke edits coalesce into one save; exit paths flush the pending run.
            saveDebounce.Schedule(async () =>
            {
                try { await services.Settings.SaveAsync(remembered, CancellationToken.None); }
                catch (Exception ex) { store.SetNotice(Notices.SaveFailed(ex.Message)); }
            });
        }
        async Task PickOutputAsync()
        {
            var folder = await FilePickerInterop.PickFolderAsync(window);
            if (folder is not null) await ApplyAsync(o => o with { OutputDirectory = folder });
        }
        async Task ResetOptionsAsync()
        {
            if (selected.Count == 0 || selected.Any(i => !i.CanEdit)) return;
            var reset = TargetFormats.Default.ApplyTo(ConversionOptions.Default) with
            {
                OutputDirectory = settings.DefaultOutputDirectory,
                MetadataPolicy = settings.PreserveTitleAndChaptersByDefault
                    ? MetadataPolicy.PreserveTitleAndChapters : MetadataPolicy.StripPersonalTags,
            };
            await store.EditAsync(selectedIds, _ => reset);
        }
        async Task SaveSettingsAsync(AppSettings next)
        {
            await saveDebounce.FlushAsync();
            await services.Settings.SaveAsync(next, CancellationToken.None);
            services.Jobs.ConfigureExecution(next.MaxConcurrentTranscodes, next.AllowAutomaticFallback, next.FullVerification);
            setSettings(next);
            setDefaults(defaults with { OutputDirectory = next.DefaultOutputDirectory,
                MetadataPolicy = next.PreserveTitleAndChaptersByDefault ? MetadataPolicy.PreserveTitleAndChapters : MetadataPolicy.StripPersonalTags });
        }
        async Task SavePresetAsync(string name)
        {
            if (selected.Count != 1 || !selected[0].CanEdit) return;
            var o = selected[0].Draft.Options;
            await services.Presets.SaveAsync(new Preset { PresetId = Guid.NewGuid(), Name = name,
                Template = o.ToTemplate() }, CancellationToken.None);
            setPresets(await services.Presets.GetAllAsync(CancellationToken.None));
        }
        async Task ExportDiagnosticsAsync(WorkbenchItem item)
        {
            if (window is null) return;
            var path = await FilePickerInterop.PickSaveReportAsync(window,
                $"TransFlux-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            if (string.IsNullOrWhiteSpace(path)) return;
            await File.WriteAllTextAsync(path, DiagnosticReport.Build(item), CancellationToken.None);
            store.SetNotice(Notices.DiagnosticSaved());
        }
        async Task<bool> WaitUntilAsync(Func<bool> done)
        {
            // Bounded wait: a stuck pipeline must not leave the exit flow spinning forever.
            for (var i = 0; i < 300; i++) { if (done()) return true; await Task.Delay(100); }
            return done();
        }
        async Task CancelAndExitAsync()
        {
            setExiting(true);
            try
            {
                if (!await WaitUntilAsync(() => !store.GetSnapshot().Busy)) { store.SetNotice(Notices.ExitTimeout()); setExiting(false); return; }
                await services.Jobs.PauseQueueAsync(CancellationToken.None);
                foreach (var item in store.GetSnapshot().Items.Where(i => i.Execution?.CanCancel == true))
                    await store.CancelAsync(item.Id);
                if (!await WaitUntilAsync(() => !store.GetSnapshot().Items.Any(i => i.Execution?.IsActive == true)))
                { store.SetNotice(Notices.ExitTimeout()); setExiting(false); return; }
                await saveDebounce.FlushAsync();
                if (!await store.PersistAsync()) { setExiting(false); return; }
                _allowClose = true; window?.Close();
            }
            catch (Exception ex) { store.SetNotice(Notices.ExitStopFailed(ex.Message)); setExiting(false); }
        }

        bool Matches(WorkbenchItem i) => filter switch
        {
            1 => i.CanEdit,
            2 => i.Execution is { IsActive: true } or { State: JobState.Queued },
            3 => i.Execution?.State == JobState.Succeeded,
            4 => i.Draft.Status == DraftStatus.Invalid || i.Issues.Any(x => x.Blocking)
                || i.Execution?.State is JobState.Failed or JobState.Interrupted or JobState.NeedsAttention or JobState.Canceled,
            _ => true,
        };
        var visible = snapshot.Items.Where(Matches).ToList();
        Element listContent;
        if (!snapshot.Loaded) listContent = Ui.Dim("正在恢复工作台和未完成任务…").Margin(Space.Xl);
        else if (snapshot.Items.Count == 0)
            listContent = VStack(Space.Lg, Title("放入文件，开始转换"),
                Ui.Dim("拖入文件或文件夹，然后在右侧选择输出格式。").TextWrapping(TextWrapping.Wrap),
                Button("添加文件", () => Run(ImportFilesAsync)).AccentButton().HAlign(HorizontalAlignment.Left)
                    .ToolTip(UiText.AddFilesTip)
                    .IsEnabled(settingsLoaded && !exiting && !folderScanning))
                .Margin(Space.Xl).VAlign(VerticalAlignment.Center);
        else if (visible.Count == 0) listContent = Ui.Dim("此筛选下没有任务。").Margin(Space.Xl);
        else
            listContent = (LazyVStack(visible, item => item.Id.ToString(), (item, _) =>
            {
                var id = item.Id;
                return TaskRow.Render(item, snapshot.Selected.Contains(id), snapshot.Paused,
                    new TaskRowActions(
                        () => store.Select(id), () => store.Select(id, true),
                        () => Run(() => store.RemoveAsync([id])), () => setCancelId(id),
                        () => Run(async () => { await store.CopyAsync(id); setFilter(0); }),
                        () => Run(() => store.MoveQueuedAsync(id, -1)), () => Run(() => store.MoveQueuedAsync(id, 1)),
                        () => Run(() => store.RetryAsync(id)), () => Run(() => store.EditQueuedAsync(id)),
                        () => { if (item.Execution?.Output?.FinalPath is { } path) Run(() => LauncherInterop.OpenAsync(path)); },
                        () =>
                        {
                            if (item.Execution?.Output?.FinalPath is { } path)
                                Run(() => { LauncherInterop.RevealInExplorer(path); return Task.CompletedTask; });
                        },
                        () => { if (item.Execution?.Output?.FinalPath is { } path) Run(() => { LauncherInterop.CopyText(path); return Task.CompletedTask; }); },
                        () => setDetailsId(id))).Margin(Space.ScrollBarGutter, 0, Space.ScrollBarGutter, 0);
            }) with { Spacing = Space.Md }).LayoutAnimation();

        var canImport = snapshot.Loaded && settingsLoaded && !exiting;
        var toolbar = FlexRow(
            Ui.WithAccelerator(Ui.IconTextButton("\uE8A5", UiText.AddFiles, () => Run(ImportFilesAsync))
                .ToolTip(UiText.AddFilesTip).IsEnabled(canImport && !folderScanning), VirtualKey.O, VirtualKeyModifiers.Control),
            Ui.WithAccelerator(Ui.IconTextButton("\uE8F4", UiText.AddFolder, () => Run(ImportFolderAsync))
                .ToolTip(UiText.AddFolderTip).IsEnabled(canImport && !folderScanning), VirtualKey.O, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
            CheckBox(recursiveFolderImport, setRecursiveFolderImport, "包含子文件夹")
                .AutomationName("递归扫描子文件夹").IsEnabled(canImport && !folderScanning),
            Button("取消扫描", () => _folderScan?.Cancel()).SubtleButton().IsEnabled(folderScanning)
                .AutomationName("取消文件夹扫描"),
            Button("全选", () => store.SelectAll(true)).SubtleButton().IsEnabled(snapshot.Loaded && !exiting),
            Button("清除选择", () => store.SelectAll(false)).SubtleButton().IsEnabled(snapshot.Loaded && !exiting))
            with { Wrap = FlexWrap.Wrap, ColumnGap = Space.Sm, RowGap = Space.Xs };
        var filters = ComboBox(new[] { "全部", "待开始", "进行中", "已完成", "需要处理" }, filter, setFilter)
            .Width(140).AutomationName("筛选任务");
        var selectionBar = Grid(columns: [GridSize.Star(), GridSize.Auto, GridSize.Auto], rows: [GridSize.Auto],
            Ui.DimCaption($"{snapshot.Items.Count} 个项目 · 已选 {selected.Count} 项").VAlign(VerticalAlignment.Center).Grid(column: 0),
            Button("移除已选", () => Run(() => store.RemoveAsync(selectedIds))).SubtleButton()
                .IsEnabled(selected.Any(i => i.CanRemove)).ToolTip("仅移除待开始或已经结束的项目；运行中的任务请取消。").Grid(column: 1),
            filters.Grid(column: 2));
        var taskSurface = Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            toolbar.Grid(row: 0).Margin(0, 0, 0, Space.Md),
            selectionBar.Grid(row: 1).Margin(0, 0, 0, Space.Md), listContent.Grid(row: 2));
        var inspector = Component<OutputSettingsPanel, OutputSettingsProps>(new OutputSettingsProps(selected, presets,
            mutate => Run(() => ApplyAsync(mutate)), () => Run(PickOutputAsync),
            () => Run(() => ApplyAsync(o => o with { OutputDirectory = null })), SavePresetAsync,
            () => setResetOpen(true)));
        var main = Grid(columns: [GridSize.Star(3), GridSize.Star(2)], rows: [GridSize.Star()],
            taskSurface.Grid(column: 0), inspector.Grid(column: 1)) with { ColumnSpacing = Space.Lg };

        var startItems = snapshot.Items.Where(i => i.CanStart && (selectedIds.Length == 0 || selectedIds.Contains(i.Id))).ToArray();
        var completed = snapshot.Items.Count(i => i.Execution?.State == JobState.Succeeded);
        var failed = snapshot.Items.Count(i => i.Execution?.State is JobState.Failed or JobState.NeedsAttention or JobState.Interrupted);
        var queued = snapshot.Items.Count(i => i.Execution?.State == JobState.Queued);
        var active = snapshot.Items.Count(i => i.Execution?.IsActive == true);
        var footer = Grid(columns: [GridSize.Star(), GridSize.Auto, GridSize.Auto], rows: [GridSize.Auto],
            VStack(2, TextBlock($"{completed} 项完成 · {failed} 项需处理 · {active} 项转换中 · {queued} 项等待").SemiBold(),
                Ui.DimCaption(snapshot.Paused ? "后续任务已暂停，当前转换继续。" : "源文件始终保留；同名输出自动使用新名称。")).Grid(column: 0),
            Button(snapshot.Paused ? "继续队列" : "暂停后续任务", () => Run(store.TogglePauseAsync))
                .AutomationName("队列控制").IsEnabled(snapshot.Loaded && !snapshot.Busy && !exiting).Margin(Space.Sm, 0).Grid(column: 1),
            Button(snapshot.Busy ? "正在加入…" : $"开始转换 {startItems.Length} 项", () => Run(() => store.StartAsync(startItems.Select(i => i.Id).ToArray())))
                .AccentButton().AutomationName("开始转换").IsEnabled(startItems.Length > 0 && !snapshot.Busy && !exiting && !coreMissing).Grid(column: 2)).Margin(0, Space.Lg, 0, 0);
        var banners = new List<Element>();
        if (coreMissing)
            banners.Add(VStack(Space.Sm,
                InfoBar("缺少媒体核心", "未检测到可用的 FFmpeg/ffprobe。转换已禁用；请恢复随应用分发的媒体核心后重启。")
                    .Error().IsClosable(false),
                Button("打开安装目录", () => Run(() => LauncherInterop.OpenFolderAsync(AppContext.BaseDirectory)))
                    .SubtleButton().HAlign(HorizontalAlignment.Left).AutomationName("打开安装目录")));
        if (folderScanning)
            banners.Add(InfoBar("正在扫描文件夹", $"已发现 {scanFound} 个媒体文件；可通过工具栏取消扫描。")
                .Informational().IsClosable(false));
        if (snapshot.Notice is { } shown) banners.Add(Ui.NoticeBar(shown, () => store.SetNotice(null)));
        if (snapshot.CanUndo)
            banners.Add(Component<UndoNotification, UndoNotificationProps>(
                new(snapshot.UndoCount, () => Run(store.UndoAsync), () => store.DismissUndo())));
        var content = Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            VStack(Space.Sm, banners.ToArray()).LayoutAnimation().Grid(row: 0).Margin(0, 0, 0, Space.Sm),
            main.Grid(row: 1), footer.Grid(row: 2)).Padding(Space.Lg);
        var top = TitleBarView.Render(new TitleBarProps(() => OpenDialog("settings"), () => OpenDialog("about"), () => OpenDialog("history"), SettingsEnabled: settingsLoaded));
        Element overlay = Empty();
        if (dialog == "settings") overlay = Component<SettingsDialog, SettingsDialogProps>(new(services, settings, SaveSettingsAsync, () => setDialog(null))).WithKey("settings-" + dialogKey);
        if (dialog == "about") overlay = Component<AboutDialog, AboutDialogProps>(new(() => setDialog(null))).WithKey("about-" + dialogKey);
        if (dialog == "history") overlay = Component<HistoryDialog, HistoryDialogProps>(new(services,
            async path => await ImportAsync([path]) > 0, () => setDialog(null))).WithKey("history-" + dialogKey);
        if (detailsId is Guid detail && snapshot.Items.FirstOrDefault(i => i.Id == detail) is { } entry)
        {
            var row = entry.Execution;
            var attempt = row?.Attempt;
            var candidate = attempt?.PlanSnapshot.CandidatePaths.FirstOrDefault();
            var expected = attempt?.PlanSnapshot.ExpectedOutput;
            var failure = row?.Failure ?? entry.Draft.Failure;
            var details = new List<Element> { TextBlock(entry.Draft.FileName).SemiBold() };
            void Detail(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) details.Add(Ui.KeyValue(label, value));
            }
            Detail("执行状态", row is null ? "尚未生成执行计划" : Fmt.JobStateText(row.State));
            Detail("编码器", candidate is null ? null : $"{candidate.EncoderName} · {candidate.Backend}");
            Detail("设备", candidate?.DeviceId ?? attempt?.DeviceIdentity);
            Detail("回退", attempt?.FallbackReason ?? "无");
            Detail("流映射", expected is null ? null : $"{expected.VideoStreamCount} 视频 / {expected.AudioStreamCount} 音频 / {expected.StreamCount} 总流");
            Detail("核心哈希", attempt?.CoreHash);
            Detail("验证", row?.Output?.VerificationSummary);
            if (entry.Issues.Count > 0)
                Detail(UiText.CompatibilityLabel, string.Join("\n", entry.Issues.Select(WorkbenchRules.IssueText)));
            Detail("错误", failure is null ? null : $"{failure.Code}: {failure.TechnicalDetail}");
            if (candidate?.Arguments.Count > 0)
                details.Add(Caption("只读参数：\n" + string.Join(" ", candidate.Arguments))
                    .TextWrapping(TextWrapping.Wrap).Set(t => t.IsTextSelectionEnabled = true));
            if (!string.IsNullOrWhiteSpace(attempt?.StderrTail))
                details.Add(Caption("FFmpeg 尾部：\n" + attempt.StderrTail)
                    .TextWrapping(TextWrapping.Wrap).Set(t => t.IsTextSelectionEnabled = true));
            details.Add(Button("导出脱敏诊断报告", () => Run(() => ExportDiagnosticsAsync(entry))).SubtleButton()
                .AutomationName("导出脱敏诊断报告"));
            overlay = DialogChrome.Render("技术详情", VStack(Space.Md, details.ToArray()), "关闭", null,
                _ => DialogChrome.DeferClose(() => setDetailsId(null)), contentWidth: 620, maxHeight: 560);
        }
        if (cancelId is Guid cancelTarget && snapshot.Items.FirstOrDefault(i => i.Id == cancelTarget) is { } cancelEntry)
        {
            overlay = DialogChrome.Render(UiText.CancelConfirmTitle, VStack(Space.Md,
                TextBlock(UiText.CancelConfirmBody).TextWrapping(TextWrapping.Wrap),
                Ui.DimCaption(cancelEntry.Draft.FileName)),
                UiText.Confirm, UiText.CancelAction,
                result =>
                {
                    if (result == ContentDialogResult.Primary)
                    {
                        Run(() => store.CancelAsync(cancelTarget));
                        store.SetNotice(Notices.CancelRequested());
                    }
                    DialogChrome.DeferClose(() => setCancelId(null));
                }, contentWidth: 480);
        }
        if (resetOpen)
        {
            overlay = DialogChrome.Render(UiText.ResetConfirmTitle, VStack(Space.Md,
                TextBlock(UiText.ResetConfirmBody).TextWrapping(TextWrapping.Wrap)),
                UiText.ResetConfirmAction, UiText.Cancel,
                result =>
                {
                    if (result == ContentDialogResult.Primary) Run(ResetOptionsAsync);
                    DialogChrome.DeferClose(() => setResetOpen(false));
                }, contentWidth: 480);
        }
        if (exitOpen) overlay = DialogChrome.Render("退出转换器？", VStack(Space.Md,
            TextBlock(exiting ? "正在停止任务并安全保存，请稍候…" : "仍有任务在执行。取消后临时输出会清理，源文件和已完成结果均保留。")
                .TextWrapping(TextWrapping.Wrap),
            Button("取消任务并退出", () => Run(CancelAndExitAsync)).IsEnabled(!exiting)), "继续转换", null,
            _ => { if (!exiting) DialogChrome.DeferClose(() => setExitOpen(false)); }, contentWidth: 480, primaryEnabled: !exiting);
        var windowRoot = Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star()],
                top.Grid(row: 0), content.Grid(row: 1), overlay.Grid(row: 1))
            .RequestedTheme(AppTheme.Resolve(settings.Theme));
        // Mica follows the OS theme, so it may only show through in follow-system mode where text
        // contrast holds; an explicit Light/Dark choice paints the theme's solid base instead.
        var micaShows = AppChrome.MicaEnabled && settings.Theme is null or "" or "System";
        if (!micaShows) windowRoot = windowRoot.Background(Theme.SolidBackground);
        Element dropHint = dragOver
            ? Border(VStack(Space.Sm, Icon(FontIcon("\uE898", fontSize: 24)),
                    TextBlock(UiText.DropHint).SemiBold()).HAlign(HorizontalAlignment.Center))
                .Background(Theme.LayerFill).WithBorder(Theme.Accent, 2).CornerRadius(8).Padding(Space.Xl)
                .VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Center).IsHitTestVisible(false)
            : Empty();
        return Grid(columns: [GridSize.Star()], rows: [GridSize.Star()],
            windowRoot.Grid(row: 0), dropHint.Grid(row: 1)).Set(ConfigureDrop);
    }

    private void ConfigureDrop(FrameworkElement target)
    {
        target.AllowDrop = true;
        target.DragEnter -= DragEnter; target.DragEnter += DragEnter;
        target.DragLeave -= DragLeave; target.DragLeave += DragLeave;
        target.DragOver -= DragOver; target.DragOver += DragOver;
        target.Drop -= Drop; target.Drop += Drop;
    }
    private void DragEnter(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            _dropChanged?.Invoke(true);
        }
    }
    private void DragLeave(object sender, DragEventArgs e) => _dropChanged?.Invoke(false);
    private static void DragOver(object sender, DragEventArgs e)
    { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; }
    private async void Drop(object sender, DragEventArgs e)
    {
        _dropChanged?.Invoke(false);
        var deferral = e.GetDeferral();
        try
        {
            if (_modal) return;
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var item in items)
                if (item is StorageFile file) paths.Add(file.Path);
                else if (item is StorageFolder folder && _importFolder is not null)
                    await _importFolder(folder.Path);
            if (_import is not null) await _import(paths);
        }
        catch (Exception ex) { _notice?.Invoke(Notices.ImportFailed(ex.Message)); }
        finally { deferral.Complete(); }
    }
}
