using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using TransFlux.App.State;
using static Microsoft.UI.Reactor.Factories;

namespace TransFlux.App.Components;

/// <summary>
/// Public root factory called by the host (src/TransFlux.App/App.cs + Hosting).
/// Returns the full Reactor component tree for the application shell.
/// </summary>
public static class AppShell
{
    public static Element Create(AppServices services)
        => Component<Shell.AppRoot, AppServices>(services);
}
