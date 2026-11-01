namespace TransFlux.App.State;

/// <summary>Severity of a transient banner; maps 1:1 onto WinUI InfoBar severity.</summary>
public enum NoticeLevel { Info, Success, Warning, Error }

/// <summary>Transient status banner for import / enqueue / save feedback.</summary>
public sealed record Notice(NoticeLevel Level, string Title, string Message)
{
    public static Notice Info(string title, string message) => new(NoticeLevel.Info, title, message);
    public static Notice Success(string title, string message) => new(NoticeLevel.Success, title, message);
    public static Notice Warning(string title, string message) => new(NoticeLevel.Warning, title, message);
    public static Notice Error(string title, string message) => new(NoticeLevel.Error, title, message);
}

/// <summary>
/// Canonical banner messages with their semantic severity. Success feedback is green,
/// actionable problems are red, recoverable situations are yellow and neutral outcomes
/// are informational — one classification shared by every screen (UI review fix round).
/// </summary>
public static class Notices
{
    public static Notice Prompt(string message) => Notice.Info(Resources.UiText.PromptTitle, message);

    public static Notice SettingsStillLoading() => Notice.Warning(Resources.UiText.PromptTitle, Resources.UiText.SettingsStillLoading);
    public static Notice NothingImported() => Notice.Warning(Resources.UiText.PromptTitle, Resources.UiText.NothingImported);
    public static Notice ScanBusy() => Notice.Warning(Resources.UiText.PromptTitle, Resources.UiText.ScanBusy);
    public static Notice NoMediaFound() => Notice.Info(Resources.UiText.PromptTitle, Resources.UiText.NoMediaFound);
    public static Notice NoNewMedia() => Notice.Info(Resources.UiText.PromptTitle, Resources.UiText.NoNewMedia);
    public static Notice ScanCanceled(int imported) => Notice.Info(Resources.UiText.PromptTitle,
        imported > 0 ? string.Format(Resources.UiText.ScanCanceledFormat, imported) : Resources.UiText.ScanCanceled);
    public static Notice DiagnosticSaved() => Notice.Success(Resources.UiText.PromptTitle, Resources.UiText.DiagnosticSaved);
    public static Notice CloseDialogFirst() => Notice.Warning(Resources.UiText.PromptTitle, Resources.UiText.CloseDialogFirst);
    public static Notice CancelRequested() => Notice.Info(Resources.UiText.PromptTitle, Resources.UiText.CancelRequestedInfo);
    public static Notice PresetSaved() => Notice.Success(Resources.UiText.PresetTitle, Resources.UiText.PresetSaved);
    public static Notice PresetNameRequired() => Notice.Warning(Resources.UiText.PresetTitle, Resources.UiText.PresetNameRequired);
    public static Notice PresetCustom() => Notice.Info(Resources.UiText.PresetTitle, Resources.UiText.PresetCustomNotice);
    public static Notice Rejected(string message) => Notice.Warning(Resources.UiText.PromptTitle, message);

    public static Notice SettingsLoadFailed(string detail)
        => Notice.Warning(Resources.UiText.SettingsLoadFailedTitle, Resources.UiText.SettingsLoadFailedBody + detail);
    public static Notice ImportFailed(string detail)
        => Notice.Error(Resources.UiText.ImportFailedTitle, Resources.UiText.ImportFailedPrefix + detail);
    public static Notice FolderScanFailed(string detail)
        => Notice.Error(Resources.UiText.FolderScanFailedTitle, Resources.UiText.FolderScanFailedPrefix + detail);
    public static Notice ExitStopFailed(string detail)
        => Notice.Error(Resources.UiText.ExitStopFailedTitle, Resources.UiText.ExitStopFailedPrefix + detail);
    public static Notice ExitTimeout()
        => Notice.Error(Resources.UiText.ExitStopFailedTitle, Resources.UiText.ExitTimeout);
    public static Notice SaveFailed(string detail)
        => Notice.Error(Resources.UiText.SaveFailedTitle, Resources.UiText.SaveFailedPrefix + detail);
    public static Notice OperationFailed(string detail)
        => Notice.Error(Resources.UiText.ErrorTitle, detail);
    public static Notice WorkbenchRestoreFailed(string detail)
        => Notice.Error(Resources.UiText.WorkbenchRestoreFailedTitle, Resources.UiText.WorkbenchRestoreFailedPrefix + detail);
    public static Notice JobWatchFailed(string detail)
        => Notice.Error(Resources.UiText.JobWatchFailedTitle, Resources.UiText.JobWatchFailedPrefix + detail);
    public static Notice EnqueueFailed(string detail)
        => Notice.Error(Resources.UiText.EnqueueFailedTitle, Resources.UiText.EnqueueFailedPrefix + detail);
    public static Notice PersistFailed(string detail)
        => Notice.Error(Resources.UiText.PersistFailedTitle, Resources.UiText.PersistFailedPrefix + detail);
    public static Notice CancelRejected()
        => Notice.Warning(Resources.UiText.CancelRejectedTitle, Resources.UiText.CancelRejected);
    public static Notice EditQueuedRejected()
        => Notice.Warning(Resources.UiText.EditQueuedRejectedTitle, Resources.UiText.EditQueuedRejected);
}
