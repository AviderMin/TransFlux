using TransFlux.Domain.Jobs;
using TransFlux.Domain.Plans;

namespace TransFlux.App.Components.Convert;

/// <summary>Result of asynchronously asking the planner for an explainable plan.</summary>
public sealed record PlanPreview(ValidatedPlan? Plan, FailureInfo? Failure, string? Error)
{
    public static readonly PlanPreview Loading = new(null, null, null);
    public bool HasPlan => Plan is not null;
}
