using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;

namespace TransFlux.Application.Contracts;

public enum ResultKind { Success = 0, ValidationFailure = 1, OperationalFailure = 2, Canceled = 3 }

/// <summary>Discriminated outcome: never use null as a universal failure.</summary>
public readonly record struct Result<T>
{
    public ResultKind Kind { get; private init; }
    public T? Value { get; private init; }
    public FailureInfo? Failure { get; private init; }

    public bool IsSuccess => Kind == ResultKind.Success;
    public bool IsCanceled => Kind == ResultKind.Canceled;

    public static Result<T> Ok(T value) => new() { Kind = ResultKind.Success, Value = value };
    public static Result<T> Validation(FailureInfo f) => new() { Kind = ResultKind.ValidationFailure, Failure = f };
    public static Result<T> Operational(FailureInfo f) => new() { Kind = ResultKind.OperationalFailure, Failure = f };
    public static Result<T> Canceled() => new() { Kind = ResultKind.Canceled };
}

public sealed record SourceReference
{
    public required string AbsolutePath { get; init; }
    public Guid MediaId { get; init; } = Guid.NewGuid();
}

public sealed record CapabilityQuery
{
    public bool ForceRefresh { get; init; }
    public bool IncludeTrialRun { get; init; } = true;
    public IReadOnlyList<string> TargetEncoders { get; init; } = Array.Empty<string>();
}
