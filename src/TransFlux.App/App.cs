using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using TransFlux.App.Components;
using TransFlux.App.Hosting;

// TransFlux entry point. The composition root is built before the window opens; the
// Reactor shell is the single root component (docs/04: Reactor is the UI core).
TransFluxAppHost.Initialize();
ReactorApp.Run<TransFluxRoot>("TransFlux", 1280, 820);

sealed class TransFluxRoot : Component
{
    public override Element Render()
        => AppShell.Create(TransFluxAppHost.Services!);
}
