using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
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
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;
using TransFlux.Domain.Rules;

namespace TransFlux.App.Components.Shell;

/// <summary>Identity for one open parameter dialog; a fresh id remounts a fresh ContentDialog.</summary>
public sealed record ParamRequest(Guid RequestId, Guid MediaId);

/// <summary>Identity for one open batch dialog.</summary>
public sealed record BatchRequest(Guid RequestId);

/// <summary>
/// Root component (Format-Factory style): custom title bar with 设置 / 关于 on the
/// right, a left target-format catalogue (视频 / 音频 / 图片), and a right task list
/// with 添加文件 / 添加文件夹 / 开始 / 暂停 / 删除 / 批处理设置. Parameter / batch /
/// settings / about are ContentDialog overlays mounted only while open, keyed by a
/// request id (never as closed layout children). No NavigationView.
/// </summary>
public sealed class AppRoot : Component<AppServices>
{
    // Latest closures captured for the native drop handlers (stable across renders).
    private Func<IReadOnlyList<string>, Task>? _addPaths;
    private Action<NoticeModel?>? _setNotice;

    public override Element Render()
    {
        var services = Props;

        var (workbench, dispatch) = UseReducer<WorkbenchState, WorkbenchAction>(
            WorkbenchReducer.Reduce, WorkbenchState.Empty, threadSafe: true);
        var (settings, setSettings) = UseState<AppSettings?>(null);
        var (settingsError, setSettingsError) = UseState<string?>(null);
        var (presets, setPresets) = UseState<IReadOnlyList<Preset>>(Array.Empty<Preset>());
        var (targetKey, setTargetKey) = UseState(TargetFormats.Default.Key);
        var (notice, setNotice) = UseState<NoticeModel?>(null);
        var (busyEnqueue, setBusyEnqueue) = UseState(false);
        var (paramRequest, setParamRequest) = UseState<ParamRequest?>(null);
        var (batchRequest, setBatchRequest) = UseState<BatchRequest?>(null);
        var (settingsOpen, setSettingsOpen) = UseState(false);
        var (settingsReq, setSettingsReq) = UseState(0);
        var (aboutOpen, setAboutOpen) = UseState(false);
        var (aboutReq, setAboutReq) = UseState(0);

        var reactorWindow = UseWindow();
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var queue = UseMemo(() => new JobQueueStore(services.Jobs, dispatcher), services);
        var snapshot = UseExternalStore(queue.Subscribe, queue.GetSnapshot);
        var tokenSource = UseRef<CancellationTokenSource?>(null);
        _setNotice = setNotice;

        UseEffect(() => { queue.Start(); return () => queue.Dispose(); }, queue);

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            tokenSource.Current = cts;
            return () => { try { cts.Cancel(); } catch { } };
        }, Array.Empty<object>());

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try { setSettings(await services.Settings.GetAsync(cts.Token)); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { setSettingsError(ex.Message); }
            });
            return () => cts.Cancel();
        }, services);

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try { setPresets(await services.Presets.GetAllAsync(cts.Token)); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { setNotice(NoticeModel.Error("预设读取失败", ex.Message)); }
            });
            return () => cts.Cancel();
        }, services);

        var effectiveSettings = settings ?? new AppSettings();
        var selectedFormat = TargetFormats.Find(targetKey) ?? TargetFormats.Default;
        var defaultOptions = selectedFormat.ApplyTo(ConversionOptions.Default with
        {
            MetadataPolicy = effectiveSettings.PreserveTitleAndChaptersByDefault
                ? MetadataPolicy.PreserveTitleAndChapters
                : MetadataPolicy.StripPersonalTags,
            OutputDirectory = effectiveSettings.DefaultOutputDirectory,
        });

        // ---- import / probe ----
        void ProbeOne(Guid mediaId, string path)
        {
            _ = Task.Run(async () =>
            {
                var ct = tokenSource.Current?.Token ?? CancellationToken.None;
                try
                {
                    var result = await services.Probe.ProbeAsync(new SourceReference { AbsolutePath = path, MediaId = mediaId }, ct);
                    if (result.IsSuccess && result.Value is not null)
                        dispatch(new ProbeCompleted(mediaId, result.Value, null, null));
                    else
                        dispatch(new ProbeCompleted(mediaId, null, result.Failure ?? FailureInfo.Of(FailureCode.ProbeFailed, "读取失败"), result.Failure?.TechnicalDetail));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    dispatch(new ProbeCompleted(mediaId, null, FailureInfo.Of(FailureCode.ProbeFailed, "读取失败", ex.Message, retryable: true), ex.Message));
                }
            });
        }

        async Task AddPathsAsync(IReadOnlyList<string> rawPaths)
        {
            var existing = workbench.Items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var drafts = rawPaths
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(x => !existing.Contains(x) && File.Exists(x))
                .Select(x => new MediaDraft { MediaId = Guid.NewGuid(), Path = x, Status = DraftStatus.Probing, Options = defaultOptions })
                .ToList();
            if (drafts.Count == 0)
            {
                setNotice(NoticeModel.Error("未导入文件", "所选文件已在列表中或不是可读取的本地媒体文件。"));
                return;
            }
            dispatch(new AddDrafts(drafts));
            foreach (var d in drafts) ProbeOne(d.MediaId, d.Path);
        }
        _addPaths = AddPathsAsync;

        async Task ImportFilesAsync()
        {
            try
            {
                var files = await FilePickerInterop.PickFilesAsync(reactorWindow?.NativeWindow);
                if (files.Count > 0) await AddPathsAsync(files);
            }
            catch (Exception ex) { setNotice(NoticeModel.Error("添加文件失败", ex.Message)); }
        }

        async Task ImportFolderAsync()
        {
            try
            {
                var folder = await FilePickerInterop.PickFolderAsync(reactorWindow?.NativeWindow);
                if (folder is not null) await AddPathsAsync(FilePickerInterop.EnumerateMediaFiles(folder, recursive: true));
            }
            catch (Exception ex) { setNotice(NoticeModel.Error("添加文件夹失败", ex.Message)); }
        }

        void SelectFormat(string key)
        {
            var fmt = TargetFormats.Find(key);
            if (fmt is null || !fmt.Enabled) return;
            setTargetKey(key);
            if (workbench.Selected.Count > 0)
                dispatch(new UpdateOptions(workbench.Selected, o => fmt.ApplyTo(o)));
        }

        HashSet<Guid> EnqueuedIds() => snapshot.Rows
            .Where(r => r.Job.State is not (JobState.Failed or JobState.Canceled or JobState.Interrupted))
            .Select(r => r.Job.RequestSnapshot.SourceSnapshotId)
            .ToHashSet();

        async Task StartAsync()
        {
            if (busyEnqueue) return;
            var enqueued = EnqueuedIds();
            var ready = workbench.ReadyItems.Where(d => !enqueued.Contains(d.MediaId)).ToList();
            if (ready.Count == 0)
            {
                setNotice(NoticeModel.Error("没有可转换的项目", "请先导入文件，或等待正在转换的任务完成。"));
                return;
            }
            setBusyEnqueue(true);
            int ok = 0, blocked = 0, failed = 0;
            string? firstError = null;
            try
            {
                foreach (var draft in ready)
                {
                    if (draft.Snapshot is null) continue;
                    var request = draft.Options.ToRequest(draft.MediaId, draft.Options.OutputDirectory ?? effectiveSettings.DefaultOutputDirectory);
                    IReadOnlyList<CompatibilityIssue> issues;
                    try { issues = MediaCompatibility.Validate(request, draft.Snapshot); }
                    catch { issues = Array.Empty<CompatibilityIssue>(); }
                    if (issues.Any(i => i.Blocking))
                    {
                        blocked++;
                        firstError ??= issues.First(i => i.Blocking).Message;
                        continue;
                    }
                    try
                    {
                        await services.Jobs.EnqueueAsync(new EnqueueRequest { Request = request, Source = draft.Snapshot }, CancellationToken.None);
                        ok++;
                    }
                    catch (Exception ex) { failed++; firstError ??= ex.Message; }
                }
                if (ok > 0)
                {
                    try { await services.Jobs.ResumeQueueAsync(CancellationToken.None); queue.RefreshControl(); }
                    catch (Exception ex) { firstError ??= ex.Message; }
                }
            }
            finally { setBusyEnqueue(false); }

            var msg = $"已开始 {ok} 项" + (blocked > 0 ? $"，{blocked} 项需处理" : string.Empty) + (failed > 0 ? $"，{failed} 项失败" : string.Empty);
            setNotice(failed == 0 && blocked == 0
                ? NoticeModel.Info("开始转换", msg)
                : NoticeModel.Error("开始转换", firstError is null ? msg : msg + "。" + firstError));
        }

        async Task TogglePauseAsync()
        {
            try
            {
                if (snapshot.Paused) { await services.Jobs.ResumeQueueAsync(CancellationToken.None); queue.SetPaused(false); }
                else { await services.Jobs.PauseQueueAsync(CancellationToken.None); queue.SetPaused(true); }
                queue.RefreshControl();
            }
            catch (Exception ex) { setNotice(NoticeModel.Error("队列暂停失败", ex.Message)); }
        }

        void DeleteSelected()
        {
            var ids = workbench.Selected.Count > 0
                ? workbench.Selected
                : workbench.FocusedId is Guid f ? new[] { f } : Array.Empty<Guid>();
            if (ids.Count > 0) dispatch(new RemoveDrafts(ids));
        }

        async Task SaveSettingsAsync(AppSettings next)
        {
            setSettings(next);
            try { await services.Settings.SaveAsync(next, CancellationToken.None); }
            catch (Exception ex) { setSettingsError(ex.Message); }
        }

        QueueRow? JobFor(Guid mediaId) => snapshot.Rows
            .Where(r => r.Job.RequestSnapshot.SourceSnapshotId == mediaId)
            .OrderByDescending(r => r.Job.EnqueueOrder)
            .FirstOrDefault();

        void OpenOutput(Guid mediaId)
        {
            var row = JobFor(mediaId);
            if (row?.Output?.FinalPath is { } path) _ = LauncherInterop.OpenAsync(path);
        }

        void RevealOutput(Guid mediaId)
        {
            var row = JobFor(mediaId);
            if (row?.Output?.FinalPath is { } path) LauncherInterop.RevealInExplorer(path);
        }

        // ---- title bar ----
        var titleBar = TitleBarView.Render(new TitleBarProps(
            () => { setSettingsReq(settingsReq + 1); setSettingsOpen(true); },
            () => { setAboutReq(aboutReq + 1); setAboutOpen(true); }));

        // ---- left catalogue ----
        var catalog = FormatCatalog.Render(targetKey, SelectFormat, () => _ = ImportFilesAsync(), () => _ = ImportFolderAsync());

        // ---- right task list ----
        var selectedCount = workbench.Selected.Count;
        Element listArea;
        if (workbench.Items.Count == 0)
        {
            listArea = Border(VStack(Space.Lg,
                    Border(Icon(SymbolIcon("Video")).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                        .Width(56).Height(56).CornerRadius(16).Background(Theme.ControlFillSecondary).HAlign(HorizontalAlignment.Center),
                    VStack(Space.Sm,
                        TextBlock(UiText.NoFilesTitle).SemiBold().FontSize(20).HAlign(HorizontalAlignment.Center),
                        TextBlock(UiText.NoFilesHint).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap).TextAlignment(TextAlignment.Center)),
                    HStack(Space.Sm,
                            Button(UiText.AddFiles, () => _ = ImportFilesAsync()).AccentButton().AutomationName(UiText.AddFiles),
                            Button(UiText.AddFolder, () => _ = ImportFolderAsync()).AutomationName(UiText.AddFolder))
                        .HAlign(HorizontalAlignment.Center))
                .VerticalAlignment(VerticalAlignment.Center))
                .Padding(Space.Lg);
        }
        else
        {
            var rows = workbench.Items.Select((draft, index) =>
                TaskRow.Render(
                    draft,
                    workbench.Selected.Contains(draft.MediaId),
                    workbench.FocusedId == draft.MediaId || (workbench.FocusedId is null && index == 0),
                    JobFor(draft.MediaId),
                    id => dispatch(new ToggleSelect(id)),
                    id => dispatch(new FocusDraft(id)),
                    id => dispatch(new RemoveDrafts(new[] { id })),
                    id => setParamRequest(new ParamRequest(Guid.NewGuid(), id)),
                    OpenOutput,
                    RevealOutput)
                .WithKey(draft.MediaId.ToString())).ToArray();

            listArea = ScrollViewer(VStack(Space.Xs, rows))
                .HorizontalScrollMode(ScrollMode.Disabled)
                .VerticalScrollMode(ScrollMode.Auto)
                .Set(s =>
                {
                    s.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    s.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                });
        }

        var toolbar = HStack(Space.Sm,
            Button(UiText.AddFiles, () => _ = ImportFilesAsync()).AutomationName(UiText.AddFiles),
            Button(UiText.AddFolder, () => _ = ImportFolderAsync()).AutomationName(UiText.AddFolder),
            Button(UiText.Start, () => _ = StartAsync()).AccentButton().MinWidth(96).AutomationName(UiText.Start),
            Button(snapshot.Paused ? UiText.Resume : UiText.Pause, () => _ = TogglePauseAsync()).AutomationName(UiText.Pause),
            Button(UiText.SelectAll, () => dispatch(new SelectAllDrafts())).IsEnabled(workbench.Items.Count > 0).AutomationName(UiText.SelectAll),
            Button(UiText.DeleteSelected, DeleteSelected).IsEnabled(selectedCount > 0 || workbench.FocusedId is not null).AutomationName(UiText.DeleteSelected),
            Button(selectedCount > 0 ? string.Format(UiText.BatchTitleFormat, selectedCount) : UiText.BatchSettings,
                    () => setBatchRequest(new BatchRequest(Guid.NewGuid())))
                .IsEnabled(selectedCount > 0).AutomationName(UiText.BatchSettings));

        var summary = workbench.Items.Count == 0
            ? "支持本地视频与音频文件"
            : $"{workbench.Items.Count} 个文件 · {workbench.ReadyItems.Count} 项已读取 · {Fmt.Size(workbench.TotalReadyBytes)} · 已选 {selectedCount} 项";

        var rightGrid = Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            toolbar.Grid(row: 0).Margin(0, 0, 0, Space.Md),
            listArea.Grid(row: 1),
            Ui.Dim(summary).FontSize(12).Margin(0, Space.Md, 0, 0).Grid(row: 2));

        var rightSurface = Border(rightGrid)
            .Padding(Space.Lg)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .Set(ConfigureDrop);

        var catalogScroll = ScrollViewer(catalog)
            .VerticalScrollMode(ScrollMode.Auto)
            .HorizontalScrollMode(ScrollMode.Disabled)
            .Set(s => { s.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; s.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; });
        var workbenchArea = Grid(columns: [GridSize.Px(280), GridSize.Star()], rows: [GridSize.Star()],
            catalogScroll.Grid(column: 0),
            rightSurface.Grid(column: 1)) with { ColumnSpacing = Space.Lg };

        var noticeRow = notice is { } n
            ? InfoBar(n.Title, n.Message)
                .Severity(n.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error)
                .IsClosable(true)
                .Closed(() => setNotice(null))
            : Empty();

        Element settingsErrorRow = settingsError is { } err
            ? InfoBar("设置读取失败", err).Warning().IsClosable(false)
            : Empty();

        var content = Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            settingsErrorRow.Grid(row: 0).Margin(0, 0, 0, Space.Sm),
            noticeRow.Grid(row: 1).Margin(0, 0, 0, Space.Sm),
            workbenchArea.Grid(row: 2))
            .Padding(Space.PagePadding);

        // ---- overlays (mount only while open; keyed by request id) ----
        Element paramOverlay = Empty();
        if (paramRequest is { } req)
        {
            var draft = workbench.Items.FirstOrDefault(i => i.MediaId == req.MediaId);
            if (draft is not null)
            {
                paramOverlay = Component<ParameterDialog, ParameterDialogProps>(
                        new ParameterDialogProps(services, draft, presets,
                            opts => dispatch(new UpdateOptions(new[] { draft.MediaId }, _ => opts)),
                            () => setParamRequest(null)))
                    .WithKey(req.RequestId.ToString("n"));
            }
        }

        Element batchOverlay = Empty();
        if (batchRequest is { } breq && selectedCount > 0)
        {
            var ids = workbench.Selected;
            var first = workbench.Items.FirstOrDefault(i => i.MediaId == ids[0]);
            if (first is not null)
            {
                var mixed = workbench.Items.Where(i => ids.Contains(i.MediaId)).Select(i => i.Options).Distinct().Count() > 1;
                batchOverlay = Component<BatchDialog, BatchDialogProps>(
                        new BatchDialogProps(services, ids, first.Options, mixed, presets,
                            (targets, mutate) => dispatch(new UpdateOptions(targets, mutate)),
                            () => setBatchRequest(null)))
                    .WithKey(breq.RequestId.ToString("n"));
            }
        }

        var settingsOverlay = settingsOpen
            ? Component<SettingsDialog, SettingsDialogProps>(
                    new SettingsDialogProps(services, effectiveSettings, SaveSettingsAsync, () => setSettingsOpen(false)))
                .WithKey("settings-" + settingsReq)
            : Empty();

        var aboutOverlay = aboutOpen
            ? Component<AboutDialog, AboutDialogProps>(new AboutDialogProps(() => setAboutOpen(false)))
                .WithKey("about-" + aboutReq)
            : Empty();

        var root = Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star()],
            titleBar.Grid(row: 0),
            content.Grid(row: 1),
            paramOverlay.Grid(row: 1),
            batchOverlay.Grid(row: 1),
            settingsOverlay.Grid(row: 1),
            aboutOverlay.Grid(row: 1));

        return root
            .RequestedTheme(AppTheme.Resolve(effectiveSettings.Theme))
            .Background(Theme.SolidBackground);
    }

    private void ConfigureDrop(Border border)
    {
        border.AllowDrop = true;
        border.DragOver -= OnDragOver;
        border.DragOver += OnDragOver;
        border.Drop -= OnDrop;
        border.Drop += OnDrop;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null) e.DragUIOverride.Caption = "导入到 TransFlux";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var item in items)
            {
                if (item is StorageFile file) paths.Add(file.Path);
                else if (item is StorageFolder folder) paths.AddRange(FilePickerInterop.EnumerateMediaFiles(folder.Path, recursive: true));
            }
            if (_addPaths is not null) await _addPaths(paths);
        }
        catch (Exception ex)
        {
            _setNotice?.Invoke(NoticeModel.Error("拖放导入失败", ex.Message));
        }
    }
}