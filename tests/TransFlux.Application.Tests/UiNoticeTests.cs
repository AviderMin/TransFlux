using TransFlux.App.State;
using TransFlux.Application.Contracts;

namespace TransFlux.Application.Tests;

/// <summary>UI review fix round: the four-level notification channel keeps its severity semantics.</summary>
public sealed class UiNoticeTests
{
    [Fact]
    public void SuccessFeedbackIsGreen()
    {
        Assert.Equal(NoticeLevel.Success, Notices.DiagnosticSaved().Level);
        Assert.Equal(NoticeLevel.Success, Notices.PresetSaved().Level);
    }

    [Fact]
    public void FailuresAreErrors()
    {
        foreach (var notice in new[]
        {
            Notices.OperationFailed("x"), Notices.SaveFailed("x"), Notices.ImportFailed("x"),
            Notices.FolderScanFailed("x"), Notices.ExitStopFailed("x"), Notices.ExitTimeout(),
            Notices.WorkbenchRestoreFailed("x"), Notices.JobWatchFailed("x"), Notices.EnqueueFailed("x"),
            Notices.PersistFailed("x"),
        })
            Assert.Equal(NoticeLevel.Error, notice.Level);
    }

    [Fact]
    public void RecoverableSituationsAreWarnings()
    {
        foreach (var notice in new[]
        {
            Notices.SettingsStillLoading(), Notices.NothingImported(), Notices.ScanBusy(),
            Notices.CancelRejected(), Notices.EditQueuedRejected(), Notices.CloseDialogFirst(),
            Notices.SettingsLoadFailed("x"), Notices.PresetNameRequired(),
        })
            Assert.Equal(NoticeLevel.Warning, notice.Level);
    }

    [Fact]
    public void NeutralOutcomesAreInformational()
    {
        foreach (var notice in new[]
        {
            Notices.NoMediaFound(), Notices.NoNewMedia(), Notices.ScanCanceled(0),
            Notices.PresetCustom(), Notices.CancelRequested(), Notices.Prompt("hello"),
        })
            Assert.Equal(NoticeLevel.Info, notice.Level);
    }

    [Fact]
    public void ScanCanceledReportsKeptItems()
    {
        Assert.Contains("3", Notices.ScanCanceled(3).Message);
        Assert.Contains("已取消文件夹扫描", Notices.ScanCanceled(0).Message);
        Assert.DoesNotContain("已添加", Notices.ScanCanceled(0).Message);
    }

    [Fact]
    public void EveryNoticeCarriesTitleAndMessage()
    {
        var all = new[]
        {
            Notices.DiagnosticSaved(), Notices.PresetSaved(), Notices.OperationFailed("x"),
            Notices.SettingsStillLoading(), Notices.NoMediaFound(), Notices.CancelRequested(),
        };
        Assert.All(all, n => Assert.False(string.IsNullOrWhiteSpace(n.Title)));
        Assert.All(all, n => Assert.False(string.IsNullOrWhiteSpace(n.Message)));
    }

    [Fact]
    public async Task WorkbenchStoreNoticeChannelCarriesSeverity()
    {
        await using var h = new SchedulerHarness();
        using var store = new WorkbenchStore(h.Scheduler, h.Probe, new MemoryWorkspace(), a => a());
        store.SetNotice(Notices.DiagnosticSaved());
        Assert.Equal(NoticeLevel.Success, store.GetSnapshot().Notice?.Level);
        store.SetNotice(Notices.CancelRejected());
        Assert.Equal(NoticeLevel.Warning, store.GetSnapshot().Notice?.Level);
        store.SetNotice(null);
        Assert.Null(store.GetSnapshot().Notice);
    }

    private sealed class MemoryWorkspace : IWorkspacePersistence
    {
        public Task<IReadOnlyList<WorkspaceEntry>?> LoadAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WorkspaceEntry>?>([]);
        public Task SaveAsync(IReadOnlyList<WorkspaceEntry> entries, CancellationToken ct)
            => Task.CompletedTask;
    }
}
