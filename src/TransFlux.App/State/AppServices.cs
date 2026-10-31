using TransFlux.Application.Contracts;

namespace TransFlux.App.State;

/// <summary>
/// Composition root handed to the UI by the host (see docs/04). The UI depends
/// only on the frozen <c>TransFlux.Application.Contracts</c> interfaces plus the
/// scheduler options record; it never touches SQLite, FFmpeg, or processes.
/// </summary>
public sealed record AppServices(
    IMediaProbe Probe,
    IJobService Jobs,
    ICapabilityService Capabilities,
    IExecutionPlanner Planner,
    IPresetService Presets,
    ISettingsService Settings,
    IEngineCapabilities Core,
    SchedulerOptions Options,
    IHistoryService History);