using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using TransFlux.App.Components;
using TransFlux.App.Hosting;
using TransFlux.App.Theming;

// TransFlux entry point. The composition root is built before the window opens; the
// Reactor shell is the single root component (docs/04: Reactor is the UI core).
TransFluxAppHost.Initialize();
ReactorApp.Run<TransFluxRoot>(
    new WindowSpec
    {
        Title = "TransFlux",
        Width = 1280,
        Height = 820,
        MinWidth = Space.WindowMinWidth,
        MinHeight = Space.WindowMinHeight,
        ExtendsContentIntoTitleBar = true,
        Backdrop = BackdropChoice.Of(AppChrome.MicaEnabled ? BackdropKind.Mica : BackdropKind.None),
    },
    configure: _ => { });

sealed class TransFluxRoot : Component
{
    public override Element Render()
        => AppShell.Create(TransFluxAppHost.Services!);
}