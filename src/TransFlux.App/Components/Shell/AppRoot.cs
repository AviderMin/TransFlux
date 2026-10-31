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
    private Func<IReadOnlyList<string>, Task<int>>? _addPaths;
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
        var (defaultQuality, setDefaultQuality) = UseState(QualityTier.Balanced);
        var (defaultOutputDir, setDefaultOutputDir) = UseState<string?>(null);
        var (filter, setFilter) = UseState(0);
        var (notice, setNotice) = UseState<NoticeModel?>(null);
        var (busyEnqueue, setBusyEnqueue) = UseState(false);
        var (paramRequest, setParamRequest) = UseState<ParamRequest?>(null);
        var (batchRequest, setBatchRequest) = UseState<BatchRequest?>(null);
        var (settingsOpen, setSettingsOpen) = UseState(false);
        var (settingsReq, setSettingsReq) = UseState(0);
        var (aboutOpen, setAboutOpen) = UseState(false);
        var (aboutReq, setAboutReq) = UseState(0);
        var (undoBatch, setUndoBatch) = UseState<IReadOnlyList<MediaDraft>?>(null);
        var (completion, setCompletion) = UseState<CompletionNotice?>(null);
        var (historyOpen, setHistoryOpen) = UseState(false);
        var (historyReq, setHistoryReq) = UseState(0);
        var settingsInitialized = UseRef(false);
        var seenSucceeded = UseRef<HashSet<Guid>?>(null);
        var completionBaseline = UseRef(false);

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
                try
                {
                    var loaded = await services.Settings.GetAsync(cts.Token);
                    setSettings(loaded);
                    if (settingsInitialized.Current) return;
                    settingsInitialized.Current = true;
                    if (TargetFormats.Find(loaded.LastTargetFormatKey ?? string.Empty) is { Enabled: true } rememberedFormat)
                        setTargetKey(rememberedFormat.Key);
                    if (Enum.TryParse<QualityTier>(loaded.LastQuality, ignoreCase: true, out var rememberedQuality)
                        && Enum.IsDefined(rememberedQuality))
                        setDefaultQuality(rememberedQuality);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { setSettingsError(ex.Message); }
            });
            return () => cts.Cancel();
        }, services);

        UseEffect(() =>
        {
            var succeeded = snapshot.Rows
                .Where(r => r.State == JobState.Succeeded && r.Output is not null)
                .ToList();
            var seen = seenSucceeded.Current ??= new HashSet<Guid>();
            if (!completionBaseline.Current)
            {
                // The first non-empty projection may contain jobs from an earlier
                // session: baseline them so only *new* completions raise a banner.
                if (snapshot.Rows.Count == 0) return;
                foreach (var row in succeeded) seen.Add(row.Job.JobId);
                completionBaseline.Current = true;
                return;
            }
            QueueRow? latest = null;
            foreach (var row in succeeded)
                if (seen.Add(row.Job.JobId)) latest = row;
            if (latest?.Output?.FinalPath is { } finalPath)
                setCompletion(new CompletionNotice(
                    System.IO.Path.GetFileName(finalPath),
                    System.IO.Path.GetDirectoryName(finalPath)));
        }, snapshot);

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
            Quality = defaultQuality,
            MetadataPolicy = effectiveSettings.PreserveTitleAndChaptersByDefault
                ? MetadataPolicy.PreserveTitleAndChapters
                : MetadataPolicy.StripPersonalTags,
            OutputDirectory = defaultOutputDir ?? effectiveSettings.DefaultOutputDirectory,
        });

        void ClearUndo()
        {
            if (undoBatch is not null) setUndoBatch(null);
        }

        // Every explicit user action other than 撤销 invalidates the delete buffer.
        void UserDispatch(WorkbenchAction action)
        {
            ClearUndo();
            dispatch(action);
        }

        void PersistLastUsed(Func<AppSettings, AppSettings> mutate)
        {
            if (settings is not { } current) return;
            var next = mutate(current);
            setSettings(next);
            _ = SaveSettingsAsync(next);
        }

        void SelectQuality(QualityTier quality)
        {
            ClearUndo();
            setDefaultQuality(quality);
            ApplyQualityToSelected(quality);
            if (!string.Equals(settings?.LastQuality, quality.ToString(), StringComparison.Ordinal))
                PersistLastUsed(s => s with { LastQuality = quality.ToString() });
        }

        void ApplyQualityToRow(Guid mediaId, QualityTier quality)
        {
            ClearUndo();
            dispatch(new UpdateOptions(new[] { mediaId }, o => o with { Quality = quality }));
        }

        void UndoDelete()
        {
            if (undoBatch is not { Count: > 0 } batch) return;
            setUndoBatch(null);
            dispatch(new AddDrafts(batch));
            foreach (var draft in batch.Where(d => d.Status == DraftStatus.Probing))
                ProbeOne(draft.MediaId, draft.Path);
        }

        void OpenCompletionFolder()
        {
            if (completion?.OutputDirectory is { } dir) _ = LauncherInterop.OpenFolderAsync(dir);
        }

        void OpenHistory()
        {
            ClearUndo();
            setHistoryReq(historyReq + 1);
            setHistoryOpen(true);
        }

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

        async Task<int> AddPathsAsync(IReadOnlyList<string> rawPaths)
        {
            ClearUndo();
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
                return 0;
            }
            dispatch(new AddDrafts(drafts));
            foreach (var d in drafts) ProbeOne(d.MediaId, d.Path);
            return drafts.Count;
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
            ClearUndo();
            setTargetKey(key);
            if (!string.Equals(settings?.LastTargetFormatKey, key, StringComparison.Ordinal))
                PersistLastUsed(s => s with { LastTargetFormatKey = key });
        }

        void ApplyQualityToSelected(QualityTier quality)
        {
            if (workbench.Selected.Count == 0) return;
            dispatch(new UpdateOptions(workbench.Selected, o => o with { Quality = quality }));
        }

        async Task PickDefaultOutputAsync()
        {
            ClearUndo();
            try
            {
                var folder = await FilePickerInterop.PickFolderAsync(reactorWindow?.NativeWindow);
                if (folder is null) return;
                setDefaultOutputDir(folder);
                if (workbench.Selected.Count > 0)
                    dispatch(new UpdateOptions(workbench.Selected, o => o with { OutputDirectory = folder }));
            }
            catch (Exception ex) { setNotice(NoticeModel.Error("选择输出位置失败", ex.Message)); }
        }

        void OpenOutputDir()
        {
            var dir = defaultOutputDir ?? effectiveSettings.DefaultOutputDirectory;
            if (string.IsNullOrWhiteSpace(dir))
            {
                setNotice(NoticeModel.Info("输出位置", "默认输出到源文件旁的 TransFlux Output 文件夹。"));
                return;
            }
            _ = LauncherInterop.OpenFolderAsync(dir);
        }

        void ClearCompleted()
        {
            var done = workbench.Items
                .Where(d => JobFor(d.MediaId) is { State: JobState.Succeeded })
                .Select(d => d.MediaId)
                .ToList();
            if (done.Count > 0) UserDispatch(new RemoveDrafts(done));
        }

        HashSet<Guid> EnqueuedIds() => snapshot.Rows
            .Where(r => r.Job.State is not (JobState.Failed or JobState.Canceled or JobState.Interrupted))
            .Select(r => r.Job.RequestSnapshot.SourceSnapshotId)
            .ToHashSet();

        async Task StartAsync()
        {
            if (busyEnqueue) return;
            ClearUndo();
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
            ClearUndo();
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
            if (ids.Count == 0) return;
            var removed = workbench.Items.Where(i => ids.Contains(i.MediaId)).ToList();
            if (removed.Count == 0) return;
            ClearUndo();
            dispatch(new RemoveDrafts(ids));
            setUndoBatch(removed);
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
            () => { ClearUndo(); setSettingsReq(settingsReq + 1); setSettingsOpen(true); },
            () => { ClearUndo(); setAboutReq(aboutReq + 1); setAboutOpen(true); }));

        // ---- left output settings ----
        var selectedCount = workbench.Selected.Count;
        var outputDirectoryText = defaultOutputDir ?? effectiveSettings.DefaultOutputDirectory ?? "源文件旁的 TransFlux Output";
        var catalog = OutputSettingsPanel.Render(new OutputSettingsProps(
            targetKey, defaultQuality, selectedCount, outputDirectoryText,
            SelectFormat,
            SelectQuality,
            () => ApplyQualityToSelected(defaultQuality),
            () => _ = PickDefaultOutputAsync(),
            OpenOutputDir));

        // ---- right task list ----
        bool MatchesFilter(MediaDraft d)
        {
            var job = JobFor(d.MediaId);
            var done = job is { State: JobState.Succeeded };
            var failed = d.Status == DraftStatus.Invalid
                || job is { State: JobState.Failed or JobState.Interrupted or JobState.Canceled };
            return filter switch
            {
                1 => !done && !failed,
                2 => done,
                3 => failed,
                _ => true,
            };
        }

        Element listArea;
        if (workbench.Items.Count == 0)
        {
            listArea = Border(VStack(Space.Lg,
                    Border(Icon(SymbolIcon("Video")).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                        .Width(64).Height(64).CornerRadius(16).Background(Theme.ControlFillSecondary).HAlign(HorizontalAlignment.Center),
                    TextBlock(UiText.NoFilesTitle).SemiBold().FontSize(20).HAlign(HorizontalAlignment.Center),
                    TextBlock("拖入文件即可开始（使用左侧当前输出设置）")
                        .Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap).TextAlignment(TextAlignment.Center),
                    Button(UiText.AddFiles, () => _ = ImportFilesAsync()).AccentButton()
                        .HAlign(HorizontalAlignment.Center).AutomationName(UiText.AddFiles))
                .VerticalAlignment(VerticalAlignment.Center))
                .Padding(Space.Lg);
        }
        else
        {
            var visible = workbench.Items.Where(MatchesFilter).ToList();
            if (visible.Count == 0)
            {
                listArea = Border(Ui.Dim("当前筛选下没有任务。").HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)).Padding(Space.Xl);
            }
            else
            {
                var rows = visible.Select((draft, index) =>
                    TaskRow.Render(
                        draft,
                        workbench.Selected.Contains(draft.MediaId),
                        workbench.FocusedId == draft.MediaId || (workbench.FocusedId is null && index == 0),
                        JobFor(draft.MediaId),
                        id => UserDispatch(new ToggleSelect(id)),
                        id => UserDispatch(new FocusDraft(id)),
                        id => UserDispatch(new RemoveDrafts(new[] { id })),
                        id => { ClearUndo(); setParamRequest(new ParamRequest(Guid.NewGuid(), id)); },
                        OpenOutput,
                        RevealOutput,
                        ApplyQualityToRow)
                    .WithKey(draft.MediaId.ToString())).ToArray();

                listArea = ScrollViewer(VStack(0, rows))
                    .HorizontalScrollMode(ScrollMode.Disabled)
                    .VerticalScrollMode(ScrollMode.Auto)
                    .Set(s =>
                    {
                        s.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        s.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    });
            }
        }

        Element selectionGroup = HStack(Space.Xs,
            Button(UiText.SelectAll, () => UserDispatch(new SelectAllDrafts())).SubtleButton()
                .IsEnabled(workbench.Items.Count > 0).AutomationName(UiText.SelectAll),
            Button(UiText.DeleteSelected, DeleteSelected).SubtleButton()
                .IsEnabled(selectedCount > 0 || workbench.FocusedId is not null).AutomationName(UiText.DeleteSelected),
            Button(selectedCount > 0 ? string.Format(UiText.BatchTitleFormat, selectedCount) : UiText.BatchSettings,
                    () => { ClearUndo(); setBatchRequest(new BatchRequest(Guid.NewGuid())); })
                .SubtleButton().IsEnabled(selectedCount > 0).AutomationName(UiText.BatchSettings));

        Element queueGroup = HStack(Space.Sm,
            Button("开始全部", () => _ = StartAsync()).AccentButton().MinWidth(96).AutomationName("开始全部"),
            Button(snapshot.Paused ? UiText.Resume : UiText.Pause, () => _ = TogglePauseAsync())
                .AutomationName(UiText.Pause).ToolTip("仅暂停启动新任务，当前任务继续"));

        var toolbar = FlexRow(
                HStack(Space.Sm,
                    Button(UiText.AddFiles, () => _ = ImportFilesAsync()).AccentButton().AutomationName(UiText.AddFiles),
                    Button(UiText.AddFolder, () => _ = ImportFolderAsync()).AutomationName(UiText.AddFolder)),
                Empty().Flex(grow: 1, basis: 0),
                selectionGroup,
                queueGroup)
            .Set(f => { f.ColumnGap = Space.Md; f.AlignItems = Microsoft.UI.Reactor.Layout.FlexAlign.Center; });

        var filterBar = SelectorBar(new[]
        {
            SelectorBarItem("全部"), SelectorBarItem("进行中"), SelectorBarItem("已完成"), SelectorBarItem("失败"),
        }, filter, i => { ClearUndo(); setFilter(i); });
        var filterRow = Grid(columns: [GridSize.Auto, GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
            filterBar.Grid(column: 0),
            Empty().Grid(column: 1),
            HStack(Space.Xs,
                Button(UiText.HistoryRecords, OpenHistory).SubtleButton()
                    .AutomationName(UiText.HistoryRecords).ToolTip("查看最近的转换记录"),
                Button("清除已完成", ClearCompleted).SubtleButton()).Grid(column: 2));

        var batchBar = selectedCount > 0
            ? Border(Grid(columns: [GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto],
                    rows: [GridSize.Auto],
                    TextBlock(string.Format(UiText.SelectedCountFormat, selectedCount)).SemiBold()
                        .VAlign(VerticalAlignment.Center).Grid(column: 0),
                    Empty().Grid(column: 1),
                    Button(UiText.BatchSettings, () => { ClearUndo(); setBatchRequest(new BatchRequest(Guid.NewGuid())); })
                        .SubtleButton().AutomationName(UiText.BatchSettings).Grid(column: 2),
                    Button(UiText.DeleteSelected, DeleteSelected).SubtleButton()
                        .AutomationName(UiText.DeleteSelected).Grid(column: 3),
                    Button(UiText.ClearSelection, () => UserDispatch(new ClearDraftSelection())).SubtleButton()
                        .AutomationName(UiText.ClearSelection).Grid(column: 4))
                .Padding(Space.Md, Space.Sm))
                .Background(Theme.SubtleFill)
                .CornerRadius(6)
                .WithBorder(Theme.CardStroke, 1)
                .WithKey("batch-bar")
            : Empty();

        var outputBar = Grid(columns: [GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto], rows: [GridSize.Auto],
            Ui.FieldLabel("输出到").VAlign(VerticalAlignment.Center).Grid(column: 0),
            TextBlock(outputDirectoryText).FontSize(12).Foreground(Theme.SecondaryText)
                .TextTrimming(TextTrimming.CharacterEllipsis).MaxLines(1).VAlign(VerticalAlignment.Center)
                .Margin(Space.Sm, 0, Space.Sm, 0).Grid(column: 1),
            Button("更改", () => _ = PickDefaultOutputAsync()).SubtleButton().Grid(column: 2),
            Button("打开", OpenOutputDir).SubtleButton().Grid(column: 3)) with { ColumnSpacing = Space.Xs };

        var rightGrid = Grid(columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
            toolbar.Grid(row: 0).Margin(0, 0, 0, Space.Sm),
            filterRow.Grid(row: 1).Margin(0, 0, 0, Space.Xs),
            batchBar.Grid(row: 2).Margin(0, 0, 0, Space.Xs),
            TaskRow.Header().Grid(row: 3),
            listArea.Grid(row: 4),
            outputBar.Grid(row: 5).Margin(0, Space.Sm, 0, 0));


        var rightSurface = Border(rightGrid)
            .Padding(Space.Lg)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .Set(ConfigureDrop);

        var workbenchArea = Grid(columns: [GridSize.Px(280), GridSize.Star()], rows: [GridSize.Star()],
            catalog.Grid(column: 0),
            rightSurface.Grid(column: 1)) with { ColumnSpacing = Space.Lg };

        var banners = new List<Element>();
        if (settingsError is { } err)
            banners.Add(InfoBar("设置读取失败", err).Warning().IsClosable(false));
        if (notice is { } n)
            banners.Add(InfoBar(n.Title, n.Message)
                .Severity(n.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error)
                .IsClosable(true)
                .Closed(() => setNotice(null)));
        if (undoBatch is { Count: > 0 } deleted)
            banners.Add((InfoBar(string.Format(UiText.DeletedCountFormat, deleted.Count), UiText.UndoDeleteHint)
                    .Warning()
                    .IsClosable(true) with
                {
                    ActionButtonContent = UiText.UndoDelete,
                    OnActionButtonClick = UndoDelete,
                }).Closed(() => setUndoBatch(null)));
        if (completion is { } done)
            banners.Add((InfoBar(string.Format(UiText.ConversionCompleteFormat, done.FileName))
                    .Success()
                    .IsClosable(true) with
                {
                    ActionButtonContent = UiText.OpenOutputFolder,
                    OnActionButtonClick = OpenCompletionFolder,
                }).Closed(() => setCompletion(null)));

        var content = Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star()],
            VStack(Space.Sm, banners.ToArray()).Grid(row: 0).Margin(0, 0, 0, Space.Sm),
            workbenchArea.Grid(row: 1))
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
                            (opts, applyToSame) =>
                            {
                                dispatch(new UpdateOptions(new[] { draft.MediaId }, _ => opts));
                                if (!applyToSame) return;
                                var same = workbench.Items
                                    .Where(i => i.MediaId != draft.MediaId
                                        && i.Options.Mode == draft.Options.Mode
                                        && i.Options.Container == draft.Options.Container
                                        && i.Options.AudioCodec == draft.Options.AudioCodec)
                                    .Select(i => i.MediaId).ToList();
                                if (same.Count > 0) dispatch(new UpdateOptions(same, _ => opts));
                            },
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

        var historyOverlay = historyOpen
            ? Component<HistoryDialog, HistoryDialogProps>(
                    new HistoryDialogProps(services,
                        async path => await AddPathsAsync(new[] { path }) > 0,
                        () => setHistoryOpen(false)))
                .WithKey("history-" + historyReq)
            : Empty();

        var root = Grid(columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star()],
            titleBar.Grid(row: 0),
            content.Grid(row: 1),
            paramOverlay.Grid(row: 1),
            batchOverlay.Grid(row: 1),
            settingsOverlay.Grid(row: 1),
            aboutOverlay.Grid(row: 1),
            historyOverlay.Grid(row: 1));

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