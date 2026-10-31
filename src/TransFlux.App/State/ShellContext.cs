using Microsoft.UI.Reactor.Core;
using TransFlux.Application.Contracts;

namespace TransFlux.App.State;

/// <summary>Ambient shell state shared by the pages (services, workbench, queue, settings).</summary>
public sealed class ShellContext
{
    public required AppServices Services { get; init; }
    public required WorkbenchState Workbench { get; init; }
    public required Action<WorkbenchAction> Dispatch { get; init; }
    public required JobQueueStore Queue { get; init; }
    public required AppSettings Settings { get; init; }
    public required Func<AppSettings, Task> SaveSettings { get; init; }
    public required Action<string> Navigate { get; init; }
}

public static class UiContext
{
    public static readonly Context<ShellContext> Shell = new(null!, "TransFluxShell");
}
