using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.Storage;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Shared;
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

namespace TransFlux.App.Components.Convert;

public sealed record ConvertPageProps(
    AppServices Services,
    WorkbenchState Workbench,
    Action<WorkbenchAction> Dispatch,
    AppSettings Settings,
    Action<string> Navigate);

public sealed record NoticeModel(bool Success, string Title, string Message);

/// <summary>Convert workbench: import empty state, media rows, output settings,
/// advanced panel, plan summary, and 加入队列/开始转换 (docs/03).</summary>
public sealed class ConvertPage : Component<ConvertPageProps>
{
    // Latest closures captured for the native drop handlers (stable across renders).
    private Func<IReadOnlyList<string>, Task>? _addPaths;
    private Action<NoticeModel>? _setNotice;

    public override Element Render()
    {
        var p = Props;
        var workbench = p.Workbench;
        var dispatch = p.Dispatch;
        var window = UseWindow()?.NativeWindow;

        var (importing, setImporting) = UseState(false);
        var (advancedOpen, setAdvancedOpen) = UseState(false);
        var (presets, setPresets) = UseState<IReadOnlyList<Preset>>(Array.Empty<Preset>());
        var (selectedPresetId, setSelectedPresetId) = UseState(BuiltInPresets.GeneralVideoBalancedId);
        var (capabilities, setCapabilities) = UseState<CapabilitySnapshot?>(null);
        var (capabilityText, setCapabilityText) = UseState("正在检测硬件能力…");
        var (preview, setPreview) = UseState<PlanPreview?>(null);
        var (notice, setNotice) = UseState<NoticeModel?>(null);
        var (busyEnqueue, setBusyEnqueue) = UseState(false);
        var (detail, setDetail) = UseState<MediaDraft?>(null);

        var tokenSource = UseRef<CancellationTokenSource?>(null);

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
                try { var all = await p.Services.Presets.GetAllAsync(cts.Token); setPresets(all); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { setNotice(new NoticeModel(false, "预设读取失败", ex.Message)); }
            });
            return () => cts.Cancel();
        }, p.Services);

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    var snap = await p.Services.Capabilities.GetAsync(new CapabilityQuery(), cts.Token);
                    setCapabilities(snap);
                    setCapabilityText(DescribeCapabilities(snap));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { setCapabilityText("硬件能力检测失败，将使用 CPU 编码：" + ex.Message); }
            });
            return () => cts.Cancel();
        }, p.Services);

        PresetRequestTemplate TemplateFor(Guid id)
            => presets.FirstOrDefault(x => x.PresetId == id)?.Template
               ?? BuiltInPresets.Get(BuiltInPresets.GeneralVideoBalancedId).Template;

        var selectedSet = workbench.Selected;
        var targets = selectedSet.Count > 0
            ? workbench.Items.Where(i => selectedSet.Contains(i.MediaId)).ToList()
            : workbench.Items.ToList();
        var current = targets.FirstOrDefault()?.Options ?? ConversionOptions.FromTemplate(TemplateFor(selectedPresetId));
        var mixed = targets.Select(t => t.Options).Distinct().Count() > 1;

        var focused = workbench.Focused;
        var focusSnapshot = focused?.Snapshot;
        var optionsForPreview = focused?.Options ?? current;

        var optionsKey = string.Join("|", new object?[]
        {
            optionsForPreview.Mode, optionsForPreview.Container, optionsForPreview.Quality, optionsForPreview.Speed,
            optionsForPreview.ResizeMode, optionsForPreview.AllowUpscale, optionsForPreview.AudioCodec,
            optionsForPreview.AudioBitRateKbps, optionsForPreview.MetadataPolicy, optionsForPreview.Hardware,
            string.Join(",", optionsForPreview.SelectedStreams), optionsForPreview.TrimStart?.Ticks, optionsForPreview.TrimEnd?.Ticks,
        });
        var planKey = $"{focused?.MediaId}|{optionsKey}|{capabilities?.CapabilityHash}";

        UseEffect(() =>
        {
            if (focused is null || focusSnapshot is null || capabilities is null)
            {
                setPreview(null);
                return static () => { };
            }
            var cts = new CancellationTokenSource();
            var request = optionsForPreview.ToRequest(focused.MediaId, p.Settings.DefaultOutputDirectory);
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await p.Services.Planner.BuildAsync(request, focusSnapshot, capabilities, cts.Token);
                    if (result.IsSuccess && result.Value is not null) setPreview(new PlanPreview(result.Value, null, null));
                    else setPreview(new PlanPreview(null, result.Failure, result.Failure?.TechnicalDetail));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { setPreview(new PlanPreview(null, null, ex.Message)); }
            });
            return () => cts.Cancel();
        }, planKey);

        IReadOnlyList<CompatibilityIssue> issues = Array.Empty<CompatibilityIssue>();
        if (focused is not null && focusSnapshot is not null)
        {
            try { issues = MediaCompatibility.Validate(optionsForPreview.ToRequest(focused.MediaId, null), focusSnapshot); }
            catch (Exception) { issues = Array.Empty<CompatibilityIssue>(); }
        }

        void ApplyToTargets(Func<ConversionOptions, ConversionOptions> mutate)
        {
            var ids = selectedSet.Count > 0 ? selectedSet : workbench.Items.Select(i => i.MediaId).ToList();
            if (ids.Count == 0) return;
            dispatch(new UpdateOptions(ids, mutate));
        }

        async Task ProbeOneAsync(Guid mediaId, string path)
        {
            var ct = tokenSource.Current?.Token ?? CancellationToken.None;
            try
            {
                var result = await p.Services.Probe.ProbeAsync(new SourceReference { AbsolutePath = path, MediaId = mediaId }, ct);
                if (result.IsSuccess && result.Value is not null)
                    dispatch(new ProbeCompleted(mediaId, result.Value, null, null));
                else
                    dispatch(new ProbeCompleted(mediaId, null,
                        result.Failure ?? FailureInfo.Of(FailureCode.ProbeFailed, "读取失败"),
                        result.Failure?.TechnicalDetail));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                dispatch(new ProbeCompleted(mediaId, null,
                    FailureInfo.Of(FailureCode.ProbeFailed, "读取失败", ex.Message, retryable: true), ex.Message));
            }
        }

        async Task AddPathsAsync(IReadOnlyList<string> rawPaths)
        {
            var existing = workbench.Items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var template = TemplateFor(selectedPresetId);
            var drafts = rawPaths
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(x => !existing.Contains(x) && File.Exists(x))
                .Select(x => new MediaDraft
                {
                    MediaId = Guid.NewGuid(),
                    Path = x,
                    Status = DraftStatus.Probing,
                    Options = ConversionOptions.FromTemplate(template),
                })
                .ToList();

            if (drafts.Count == 0)
            {
                setNotice(new NoticeModel(false, "未导入文件", "所选文件已在列表中或不是可读取的本地媒体文件。"));
                return;
            }
            dispatch(new AddDrafts(drafts));
            foreach (var draft in drafts) _ = ProbeOneAsync(draft.MediaId, draft.Path);
        }

        _addPaths = AddPathsAsync;
        _setNotice = setNotice;

        async Task ImportFilesAsync()
        {
            if (importing) return;
            setImporting(true);
            try { await AddPathsAsync(await FilePickerInterop.PickFilesAsync(window)); }
            catch (Exception ex) { setNotice(new NoticeModel(false, "添加文件失败", ex.Message)); }
            finally { setImporting(false); }
        }

        async Task ImportFolderAsync()
        {
            if (importing) return;
            setImporting(true);
            try
            {
                var folder = await FilePickerInterop.PickFolderAsync(window);
                if (folder is not null)
                    await AddPathsAsync(FilePickerInterop.EnumerateMediaFiles(folder, recursive: true));
            }
            catch (Exception ex) { setNotice(new NoticeModel(false, "添加文件夹失败", ex.Message)); }
            finally { setImporting(false); }
        }

        void RemoveSelected()
        {
            var ids = selectedSet.Count > 0
                ? selectedSet
                : workbench.FocusedId is Guid f ? new[] { f } : Array.Empty<Guid>();
            if (ids.Count > 0) dispatch(new RemoveDrafts(ids));
        }

        void ApplyPreset(Guid id)
        {
            setSelectedPresetId(id);
            var template = TemplateFor(id);
            ApplyToTargets(o => o with
            {
                Mode = template.Mode,
                Container = template.Container,
                Quality = template.Quality,
                Speed = template.Speed,
                ResizeMode = template.ResizeMode,
                AllowUpscale = template.AllowUpscale,
                AudioCodec = template.AudioCodec,
                AudioBitRateKbps = template.AudioBitRateKbps,
                MetadataPolicy = template.MetadataPolicy,
            });
        }

        async Task PickOutputAsync()
        {
            try
            {
                var folder = await FilePickerInterop.PickFolderAsync(window);
                if (folder is not null) ApplyToTargets(o => o with { OutputDirectory = folder });
            }
            catch (Exception ex) { setNotice(new NoticeModel(false, "选择输出位置失败", ex.Message)); }
        }

        async Task EnqueueAsync(bool start)
        {
            if (busyEnqueue) return;
            var ready = workbench.ReadyItems;
            if (ready.Count == 0)
            {
                setNotice(new NoticeModel(false, "没有可转换的项目", "请先导入文件并等待媒体信息读取完成。"));
                return;
            }
            setBusyEnqueue(true);
            var ok = 0; var blocked = 0; var failed = 0; string? firstError = null;
            var enqueued = new List<Guid>();
            try
            {
                foreach (var draft in ready)
                {
                    if (draft.Snapshot is null) continue;
                    var request = draft.Options.ToRequest(draft.MediaId);
                    IReadOnlyList<CompatibilityIssue> draftIssues;
                    try { draftIssues = MediaCompatibility.Validate(request, draft.Snapshot); }
                    catch { draftIssues = Array.Empty<CompatibilityIssue>(); }
                    if (draftIssues.Any(i => i.Blocking))
                    {
                        blocked++;
                        firstError ??= draftIssues.First(i => i.Blocking).Message;
                        continue;
                    }
                    try
                    {
                        await p.Services.Jobs.EnqueueAsync(new EnqueueRequest { Request = request, Source = draft.Snapshot }, CancellationToken.None);
                        ok++;
                        enqueued.Add(draft.MediaId);
                    }
                    catch (Exception ex) { failed++; firstError ??= ex.Message; }
                }
                if (start && ok > 0)
                {
                    try { await p.Services.Jobs.ResumeQueueAsync(CancellationToken.None); }
                    catch (Exception ex) { firstError ??= ex.Message; }
                }
            }
            finally { setBusyEnqueue(false); }

            var message = $"已加入 {ok} 项" + (blocked > 0 ? $"，{blocked} 项需处理" : string.Empty) + (failed > 0 ? $"，{failed} 项失败" : string.Empty);
            setNotice(new NoticeModel(failed == 0 && blocked == 0, start ? "已开始转换" : "已加入队列",
                firstError is null ? message : message + "。" + firstError));

            if (start && ok > 0)
            {
                dispatch(new RemoveDrafts(enqueued));
                p.Navigate("tasks");
            }
        }

        void CopyPlanDetails()
        {
            if (preview?.Plan is not { } plan) return;
            var lines = plan.CandidatePaths.Select(c => $"{c.EncoderName} [{c.Backend}]: {string.Join(' ', c.Arguments)}");
            LauncherInterop.CopyText($"plan={plan.PlanHash}\n" + string.Join("\n", lines));
            setNotice(new NoticeModel(true, "已复制", "转换计划技术详情已复制到剪贴板。"));
        }

        // ---- layout ----
        var outputDirText = targets
            .Select(t => t.Options.OutputDirectory)
            .FirstOrDefault(d => !string.IsNullOrEmpty(d))
            ?? p.Settings.DefaultOutputDirectory
            ?? "TransFlux Output（源文件所在文件夹）";

        Element header = HStack(Space.Sm,
            Heading(UiText.NavConvert),
            Ui.Spacer(),
            Ui.WithAccelerator((ButtonElement)Button(UiText.AddFiles, () => _ = ImportFilesAsync()).AccentButton(), VirtualKey.O, VirtualKeyModifiers.Control).AutomationName(UiText.AddFiles),
            Ui.WithAccelerator((ButtonElement)Button(UiText.AddFolder, () => _ = ImportFolderAsync()), VirtualKey.O, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift).AutomationName(UiText.AddFolder),
            Ui.WithAccelerator((ButtonElement)Button(UiText.RemoveSelected, RemoveSelected).IsEnabled(workbench.Items.Count > 0), VirtualKey.Delete).AutomationName(UiText.RemoveSelected),
            Ui.WithAccelerator((ButtonElement)Button(UiText.SelectAll, () => dispatch(new SelectAllDrafts())).IsEnabled(workbench.Items.Count > 0), VirtualKey.A, VirtualKeyModifiers.Control).AutomationName(UiText.SelectAll),
            Ui.WithAccelerator((ButtonElement)Button("详情", () => setDetail(workbench.Focused)).IsEnabled(workbench.Focused?.Snapshot is not null), VirtualKey.Enter, VirtualKeyModifiers.Menu).AutomationName("查看详情"));

        Element listArea;
        if (workbench.Items.Count == 0)
        {
            var dropBorder = Border(EmptyImport.Render(() => _ = ImportFilesAsync(), () => _ = ImportFolderAsync(), importing))
                .Padding(Space.Xl)
                .CornerRadius(12)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1)
                .MinHeight(260)
                .Set(ConfigureDrop);
            listArea = dropBorder.Flex(grow: 1, basis: 0);
        }
        else
        {
            var rows = workbench.Items.Select((draft, index) =>
                MediaListRow.Render(
                    draft,
                    selectedSet.Contains(draft.MediaId),
                    workbench.FocusedId == draft.MediaId || (workbench.FocusedId is null && index == 0),
                    id => dispatch(new ToggleSelect(id)),
                    id => dispatch(new FocusDraft(id)),
                    id => dispatch(new RemoveDrafts(new[] { id })))
                .WithKey(draft.MediaId.ToString())).ToArray();

            listArea = ScrollView(VStack(Space.Xs, rows)).Flex(grow: 1, basis: 0);
        }

        var summaryLine = $"{workbench.Items.Count} 项 · 已选 {selectedSet.Count} 项 · 约 {Fmt.Size(workbench.TotalReadyBytes)}";

        Element advanced = Expander(UiText.AdvancedSettings,
                AdvancedPanel.Render(current, targets.FirstOrDefault(t => t.Snapshot is not null)?.Snapshot, ApplyToTargets),
                advancedOpen,
                v => setAdvancedOpen(v));

        Element rightPanel = VStack(Space.Md,
            Ui.Card(VStack(Space.Md,
                TextBlock(capabilityText).Foreground(Theme.SecondaryText).FontSize(12).TextWrapping(TextWrapping.Wrap),
                OutputSettingsPanel.Render(current, mixed, presets, selectedPresetId, ApplyPreset, ApplyToTargets, outputDirText, () => _ = PickOutputAsync()),
                advanced,
                Ui.Divider(),
                PlanSummary.Render(preview, issues, workbench.ReadyItems.Count > 0, CopyPlanDetails),
                HStack(Space.Sm,
                    Button(UiText.AddToQueue, () => _ = EnqueueAsync(false)).IsEnabled(workbench.ReadyItems.Count > 0 && !busyEnqueue).AutomationName(UiText.AddToQueue),
                    Button(UiText.StartConvert, () => _ = EnqueueAsync(true)).AccentButton().IsEnabled(workbench.ReadyItems.Count > 0 && !busyEnqueue).AutomationName(UiText.StartConvert).Flex(grow: 1, basis: 0)))))
            .Width(Space.SettingsPanelMax)
            .Flex(shrink: 0);

        var body = FlexRow(VStack(Space.Sm, header, Ui.Dim(summaryLine), listArea).Flex(grow: 1, basis: 0), rightPanel).Set(f => f.ColumnGap = Space.Lg);

        var noticeBar = notice is { } n
            ? InfoBar(n.Title, n.Message)
                .Severity(n.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error)
                .IsClosable(true)
                .Closed(() => setNotice(null))
                .Margin(0, 0, 0, Space.Sm)
            : null;

        Element detailDialog = ContentDialog("文件详情",
                DetailContent(detail),
                primaryButtonText: "关闭") with
        {
            IsOpen = detail is not null,
            OnClosed = _ => setDetail(null),
        };

        return VStack(0, noticeBar, body.Flex(grow: 1, basis: 0), detailDialog).Padding(Space.PagePadding);
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
            _setNotice?.Invoke(new NoticeModel(false, "拖放导入失败", ex.Message));
        }
    }

    private static Element DetailContent(MediaDraft? draft)
    {
        if (draft is null) return Empty();
        var snapshot = draft.Snapshot;
        if (snapshot is null)
            return VStack(4, TextBlock(draft.FileName).SemiBold(), Ui.Dim(draft.ErrorText ?? "尚未读取完成"));

        var lines = new List<Element>
        {
            TextBlock(draft.FileName).SemiBold(),
            Ui.Dim(draft.Path),
            Ui.KeyValue("时长", Fmt.DurationMicros(snapshot.DurationUs)),
            Ui.KeyValue("大小", Fmt.Size(snapshot.SizeBytes)),
            Ui.KeyValue("色彩", Fmt.ColorText(snapshot.ColorClassification)),
            Ui.KeyValue("容器", snapshot.Format.FormatName),
            Ui.Divider(),
        };
        foreach (var stream in snapshot.Streams)
        {
            var kind = stream.Kind switch { MediaKind.Video => "视频", MediaKind.Audio => "音频", MediaKind.Subtitle => "字幕", _ => stream.Kind.ToString() };
            var extra = stream.Kind switch
            {
                MediaKind.Video => $"{stream.Width}×{stream.Height} · {stream.PixelFormat}",
                MediaKind.Audio => $"{stream.ChannelLayout ?? stream.Channels?.ToString()} · {stream.SampleRate}Hz",
                _ => stream.Language,
            };
            lines.Add(Ui.KeyValue($"#{stream.SourceIndex} {kind}", $"{Fmt.CodecName(stream.Codec)} · {extra}"));
        }
        return ScrollView(VStack(Space.Sm, lines.ToArray())).MaxHeight(420);
    }

    private static string DescribeCapabilities(CapabilitySnapshot snapshot)
    {
        var gpu = snapshot.Candidates.Where(c => c.Available && c.Backend != "cpu").Select(c => c.EncoderName).ToList();
        return gpu.Count > 0
            ? $"已检测可用硬件编码：{string.Join("、", gpu)}（使用前经过试跑验证）"
            : "未检测到通过试跑的硬件编码，将使用 CPU 软件编码。";
    }
}
