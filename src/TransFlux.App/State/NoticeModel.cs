namespace TransFlux.App.State;

/// <summary>Transient status banner for import / enqueue / save feedback.</summary>
public sealed record NoticeModel(bool Success, string Title, string Message)
{
    public static NoticeModel Info(string title, string message) => new(true, title, message);
    public static NoticeModel Error(string title, string message) => new(false, title, message);
}

/// <summary>Banner payload for a job that just reached the succeeded state.</summary>
public sealed record CompletionNotice(string FileName, string? OutputDirectory);
