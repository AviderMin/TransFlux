using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
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
    private Action<string?>? _error;
    private bool _allowClose, _modal;

    public override Element Render()
    {
        var services = Props;
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var window = UseWindow()?.NativeWindow;
        var store = UseMemo(() => new WorkbenchStore(services.Jobs, services.Probe, services.Workspace,
            action => { if (dispatcher is null || dispatcher.HasThreadAccess) action(); else dispatcher.TryEnqueue(() => action()); },
            services.Options.MaxConcurrentProbes), services);
        var snapshot = UseExternalStore(store.Subscribe, store.GetSnapshot);
        var initialSettings = UseMemo(() => new AppSettings(), Array.Empty<object>());
        var (settings, setSettings) = UseState(initialSettings);
        var (defaults, setDefaults) = UseState(ConversionOptions.Default);
        var (presets, setPresets) = UseState<IReadOnlyList<Preset>>(Array.Empty<Preset>());
        var (filter, setFilter) = UseState(0);
        var (dialog, setDialog) = UseState<string?>(null);
        var (dialogKey, setDialogKey) = UseState(0);
        var (detailsId, setDetailsId) = UseState<Guid?>(null);
        var (exitOpen, setExitOpen) = UseState(false);
        var (exiting, setExiting) = UseState(false);
        _modal = dialog is not null || detailsId is not null;
        _error = store.SetError;

        UseEffect(() => { store.Start(); return () => store.Dispose(); }, store);
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
                    setPresets(await services.Presets.GetAllAsync(cts.Token));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { store.SetError("设置或预设读取失败：" + ex.Message); }
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
                if (_modal) { store.SetError("请先关闭当前对话框，再退出应用。"); return; }
                var current = store.GetSnapshot();
                if (!current.Loaded) { _allowClose = true; window.Close(); return; }
                if (current.Busy || current.Items.Any(i => i.Execution?.IsActive == true)) setExitOpen(true);
                else _ = CloseSafelyAsync();
            }
            async Task CloseSafelyAsync()
            {
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
            catch (Exception ex) { store.SetError(ex.Message); }
        }
        void Run(Func<Task> action) => _ = RunAsync(action);
        void OpenDialog(string name) { setDialogKey(dialogKey + 1); setDialog(name); }

        async Task<int> ImportAsync(IReadOnlyList<string> paths)
        {
            var count = await store.ImportAsync(paths, defaults);
            if (count == 0) store.SetError("未添加文件：文件可能已在待开始列表中、已不存在，或工作台仍在恢复。");
            else setFilter(0);
            return count;
        }
        _import = ImportAsync;
        async Task ImportFilesAsync()
        {
            var paths = await FilePickerInterop.PickFilesAsync(window);
            if (paths.Count > 0) await ImportAsync(paths);
        }
        async Task ImportFolderAsync()
        {
            var folder = await FilePickerInterop.PickFolderAsync(window);
            if (folder is null) return;
            var paths = await Task.Run(() => FilePickerInterop.EnumerateMediaFiles(folder, recursive: true));
            await ImportAsync(paths);
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
            await services.Settings.SaveAsync(remembered, CancellationToken.None);
        }
        async Task PickOutputAsync()
        {
            var folder = await FilePickerInterop.PickFolderAsync(window);
            if (folder is not null) await ApplyAsync(o => o with { OutputDirectory = folder });
        }
        async Task SaveSettingsAsync(AppSettings next)
        {
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
        async Task CancelAndExitAsync()
        {
            setExiting(true);
            try
            {
                while (store.GetSnapshot().Busy) await Task.Delay(100);
                await services.Jobs.PauseQueueAsync(CancellationToken.None);
                foreach (var item in store.GetSnapshot().Items.Where(i => i.Execution?.CanCancel == true))
                    await store.CancelAsync(item.Id);
                while (store.GetSnapshot().Items.Any(i => i.Execution?.IsActive == true)) await Task.Delay(100);
                if (!await store.PersistAsync()) { setExiting(false); return; }
                _allowClose = true; window?.Close();
            }
            catch (Exception ex) { store.SetError("退出前停止任务失败：" + ex.Message); setExiting(false); }
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
        var list = new List<Element>();
        foreach (var item in visible)
        {
            var id = item.Id;
            list.Add(TaskRow.Render(item, snapshot.Selected.Contains(id), snapshot.Paused,
                () => store.Select(id), () => store.Select(id, true),
                () => Run(() => store.RemoveAsync([id])), () => Run(() => store.CancelAsync(id)),
                () => Run(async () => { await store.CopyAsync(id); setFilter(0); }),
                () => Run(() => store.RetryAsync(id)), () => Run(() => store.EditQueuedAsync(id)),
                () => { if (item.Execution?.Output?.FinalPath is { } path) Run(async () => { await LauncherInterop.OpenAsync(path); }); },
                () => { if (item.Execution?.Output?.FinalPath is { } path) LauncherInterop.RevealInExplorer(path); },
                () => setDetailsId(id)));
        }
        if (!snapshot.Loaded) list.Add(Ui.Dim("正在恢复工作台和未完成任务…").Margin(Space.Xl));
        else if (snapshot.Items.Count == 0)
            list.Add(VStack(Space.Lg, TextBlock("放入文件，开始转换").SemiBold().FontSize(24),
                Ui.Dim("拖入文件或文件夹，然后在右侧选择输出格式。").TextWrapping(TextWrapping.Wrap),
                Button("添加文件", () => Run(ImportFilesAsync)).AccentButton().HAlign(HorizontalAlignment.Left))
                .Margin(Space.Xl).VAlign(VerticalAlignment.Center));
        else if (list.Count == 0) list.Add(Ui.Dim("此筛选下没有任务。").Margin(Space.Xl));
        var listScroll = ScrollViewer(VStack(Space.Md, list.ToArray())).VerticalScrollMode(ScrollMode.Auto)
            .HorizontalScrollMode(ScrollMode.Disabled).Set(s => { s.HorizontalContentAlignment = HorizontalAlignment.Stretch; s.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; });

        var toolbar = HStack(Space.Sm,
            Ui.WithAccelerator(Button("添加文件", () => Run(ImportFilesAsync)).AutomationName("添加文件"), VirtualKey.O, VirtualKeyModifiers.Control),
            Ui.WithAccelerator(Button("添加文件夹", () => Run(ImportFolderAsync)).AutomationName("添加文件夹"), VirtualKey.O, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
            Button("全选", () => store.SelectAll(true)).SubtleButton(),
            Button("清除选择", () => store.SelectAll(false)).SubtleButton())
            .IsEnabled(snapshot.Loaded && !exiting);
        var filters = ComboBox(new[] { "全部", "待开始", "进行中", "已完成", "需要处理" }, filter, setFilter)
            .Width(140).AutomationName("筛选任务");
        var selectionBar = Grid(columns: [GridSize.Star(), GridSize.Auto, GridSize.Auto], rows: [GridSize.Auto],
            Ui.Dim($"{snapshot.Items.Count} 个项目 · 已选 {selected.Count} 项").FontSize(12).VAlign(VerticalAlignment.Center).Grid(column: 0),
            Button("移除已选", () => Run(() => store.RemoveAsync(selectedIds))).SubtleButton()
                .IsEnabled(selected.Any(i => i.CanRemove)).ToolTip("仅移除待开始或已经结束的项目；运行中的任务请取消。").Grid(column: 1),
            filters.Grid(column: 2));
        var taskSurface = Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            toolbar.Grid(row: 0).Margin(0, 0, 0, Space.Md),
            selectionBar.Grid(row: 1).Margin(0, 0, 0, Space.Md), listScroll.Grid(row: 2)).Set(ConfigureDrop);
        var inspector = Component<OutputSettingsPanel, OutputSettingsProps>(new OutputSettingsProps(selected, presets,
            mutate => Run(() => ApplyAsync(mutate)), () => Run(PickOutputAsync),
            () => Run(() => ApplyAsync(o => o with { OutputDirectory = null })), SavePresetAsync));
        var main = Grid(columns: [GridSize.Star(), GridSize.Px(320)], rows: [GridSize.Star()],
            taskSurface.Grid(column: 0), inspector.Grid(column: 1)) with { ColumnSpacing = Space.Lg };

        var startItems = snapshot.Items.Where(i => i.CanStart && (selectedIds.Length == 0 || selectedIds.Contains(i.Id))).ToArray();
        var completed = snapshot.Items.Count(i => i.Execution?.State == JobState.Succeeded);
        var failed = snapshot.Items.Count(i => i.Execution?.State is JobState.Failed or JobState.NeedsAttention or JobState.Interrupted);
        var queued = snapshot.Items.Count(i => i.Execution?.State == JobState.Queued);
        var active = snapshot.Items.Count(i => i.Execution?.IsActive == true);
        var footer = Grid(columns: [GridSize.Star(), GridSize.Auto, GridSize.Auto], rows: [GridSize.Auto],
            VStack(2, TextBlock($"{completed} 项完成 · {failed} 项需处理 · {active} 项转换中 · {queued} 项等待").SemiBold(),
                Ui.Dim(snapshot.Paused ? "后续任务已暂停，当前转换继续。" : "源文件始终保留；同名输出自动使用新名称。").FontSize(12)).Grid(column: 0),
            Button(snapshot.Paused ? "继续队列" : "暂停后续任务", () => Run(store.TogglePauseAsync))
                .AutomationName("队列控制").IsEnabled(snapshot.Loaded && !snapshot.Busy && !exiting).Margin(Space.Sm, 0).Grid(column: 1),
            Button(snapshot.Busy ? "正在加入…" : $"开始转换 {startItems.Length} 项", () => Run(() => store.StartAsync(startItems.Select(i => i.Id).ToArray())))
                .AccentButton().AutomationName("开始转换").IsEnabled(startItems.Length > 0 && !snapshot.Busy && !exiting).Grid(column: 2)).Margin(0, Space.Lg, 0, 0);
        var banners = new List<Element>();
        if (snapshot.Error is { } error) banners.Add(InfoBar("提示", error).Warning().IsClosable(true).Closed(() => store.SetError(null)));
        if (snapshot.CanUndo) banners.Add(HStack(Space.Sm, Ui.Dim("已移除项目，源文件和输出文件均保留。"), Button("撤销移除", () => Run(store.UndoAsync)).SubtleButton()));
        var content = Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            VStack(Space.Sm, banners.ToArray()).Grid(row: 0).Margin(0, 0, 0, Space.Sm), main.Grid(row: 1), footer.Grid(row: 2)).Padding(Space.Lg);
        var top = TitleBarView.Render(new TitleBarProps(() => OpenDialog("settings"), () => OpenDialog("about"), () => OpenDialog("history")));
        Element overlay = Empty();
        if (dialog == "settings") overlay = Component<SettingsDialog, SettingsDialogProps>(new(services, settings, SaveSettingsAsync, () => setDialog(null))).WithKey("settings-" + dialogKey);
        if (dialog == "about") overlay = Component<AboutDialog, AboutDialogProps>(new(() => setDialog(null))).WithKey("about-" + dialogKey);
        if (dialog == "history") overlay = Component<HistoryDialog, HistoryDialogProps>(new(services,
            async path => await ImportAsync([path]) > 0, () => setDialog(null))).WithKey("history-" + dialogKey);
        if (detailsId is Guid detail && snapshot.Items.FirstOrDefault(i => i.Id == detail) is { } entry)
        {
            var row = entry.Execution;
            var failure = row?.Failure ?? entry.Draft.Failure;
            overlay = DialogChrome.Render("技术详情", VStack(Space.Md,
                TextBlock(entry.Draft.FileName).SemiBold(),
                Ui.Dim(row?.Attempt?.PlanSnapshot.CandidatePaths.FirstOrDefault()?.Rationale ?? "尚未生成执行计划").TextWrapping(TextWrapping.Wrap),
                TextBlock(failure?.TechnicalDetail ?? row?.Attempt?.StderrTail ?? row?.Output?.VerificationSummary ?? "没有额外错误信息。")
                    .TextWrapping(TextWrapping.Wrap).Set(t => t.IsTextSelectionEnabled = true)), "关闭", null,
                _ => DialogChrome.DeferClose(() => setDetailsId(null)), contentWidth: 620);
        }
        if (exitOpen) overlay = DialogChrome.Render("退出转换器？", VStack(Space.Md,
            TextBlock(exiting ? "正在停止任务并安全保存，请稍候…" : "仍有任务在执行。取消后临时输出会清理，源文件和已完成结果均保留。")
                .TextWrapping(TextWrapping.Wrap),
            Button("取消任务并退出", () => Run(CancelAndExitAsync)).IsEnabled(!exiting)), "继续转换", null,
            _ => { if (!exiting) DialogChrome.DeferClose(() => setExitOpen(false)); }, contentWidth: 480, primaryEnabled: !exiting);
        return Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star()],
            top.Grid(row: 0), content.Grid(row: 1), overlay.Grid(row: 1)).RequestedTheme(AppTheme.Resolve(settings.Theme)).Background(Theme.SolidBackground);
    }

    private void ConfigureDrop(FrameworkElement target)
    {
        target.AllowDrop = true;
        target.DragOver -= DragOver; target.DragOver += DragOver;
        target.Drop -= Drop; target.Drop += Drop;
    }
    private static void DragOver(object sender, DragEventArgs e)
    { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; }
    private async void Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var item in items)
                if (item is StorageFile file) paths.Add(file.Path);
                else if (item is StorageFolder folder)
                    paths.AddRange(await Task.Run(() => FilePickerInterop.EnumerateMediaFiles(folder.Path, true)));
            if (_import is not null) await _import(paths);
        }
        catch (Exception ex) { _error?.Invoke("导入失败：" + ex.Message); }
        finally { deferral.Complete(); }
    }
}
