using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using TransFlux.App.Components.Convert;
using TransFlux.App.Components.Presets;
using TransFlux.App.Components.Queue;
using TransFlux.App.Components.Settings;
using TransFlux.App.Interop;
using TransFlux.App.Resources;
using TransFlux.App.State;
using TransFlux.App.Theming;
using TransFlux.Application.Contracts;

namespace TransFlux.App.Components.Shell;

/// <summary>Root component: owns the navigation, the workbench session state, the
/// queue bridge, and the persisted settings. Pages are re-created from state so
/// navigation never drops the draft (docs/03 主流程细节 #2).</summary>
public sealed class AppRoot : Component<AppServices>
{
    public override Element Render()
    {
        var services = Props;

        var (page, setPage) = UseState("convert");
        var (workbench, dispatch) = UseReducer<WorkbenchState, WorkbenchAction>(
            WorkbenchReducer.Reduce, WorkbenchState.Empty, threadSafe: true);
        var (settings, setSettings) = UseState<AppSettings?>(null);
        var (settingsError, setSettingsError) = UseState<string?>(null);
        var reactorWindow = UseWindow();

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var queue = UseMemo(() => new JobQueueStore(services.Jobs, dispatcher), services);

        // Start the queue bridge, load settings, and size the window once.
        UseEffect(() =>
        {
            queue.Start();
            return () => queue.Dispose();
        }, queue);

        UseEffect(() =>
        {
            WindowInterop.SetTitle(reactorWindow?.NativeWindow, UiText.AppTitle);
            WindowInterop.ApplyInitialSize(reactorWindow?.NativeWindow);
        }, reactorWindow);

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    var loaded = await services.Settings.GetAsync(cts.Token);
                    setSettings(loaded);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { setSettingsError(ex.Message); }
            });
            return () => cts.Cancel();
        }, services);

        async Task SaveSettingsAsync(AppSettings next)
        {
            setSettings(next);
            try { await services.Settings.SaveAsync(next, CancellationToken.None); }
            catch (Exception ex) { setSettingsError(ex.Message); }
        }

        var effectiveSettings = settings ?? new AppSettings();

        var navItems = new[]
        {
            NavItem(UiText.NavConvert, "\uE8AB", "convert"),
            NavItem(UiText.NavTasks, "\uE823", "tasks"),
            NavItem(UiText.NavPresets, "\uE8FD", "presets"),
            NavItem(UiText.NavSettings, "\uE713", "settings"),
        };

        Element content = page switch
        {
            "tasks" => Component<QueuePage, QueuePageProps>(new QueuePageProps(services, queue)),
            "presets" => Component<PresetsPage, PresetsPageProps>(new PresetsPageProps(services)),
            "settings" => Component<SettingsPage, SettingsPageProps>(new SettingsPageProps(services, effectiveSettings, SaveSettingsAsync)),
            _ => Component<ConvertPage, ConvertPageProps>(new ConvertPageProps(services, workbench, dispatch, effectiveSettings, setPage)),
        };

        var shell = new ShellContext
        {
            Services = services,
            Workbench = workbench,
            Dispatch = dispatch,
            Queue = queue,
            Settings = effectiveSettings,
            SaveSettings = SaveSettingsAsync,
            Navigate = setPage,
        };

        var navigation = (NavigationView(navItems, content) with { SelectedTag = page })
            .PaneDisplayMode(NavigationViewPaneDisplayMode.Auto)
            .OpenPaneLength(220)
            .CompactModeThresholdWidth(Space.WindowMinWidth)
            .ExpandedModeThresholdWidth(1200)
            .PaneTitle(UiText.AppTitle)
            .SelectedTagChanged(tag => { if (!string.IsNullOrEmpty(tag)) setPage(tag); })
            .Provide(UiContext.Shell, shell);

        Element root = VStack(0,
            settingsError is string err ? InfoBar("设置读取失败", err).Warning().IsClosable(false).Margin(Space.Lg, Space.Sm) : null,
            navigation.Flex(grow: 1, basis: 0));

        return root
            .RequestedTheme(AppTheme.Resolve(effectiveSettings.Theme))
            .Background(Theme.SolidBackground);
    }
}
