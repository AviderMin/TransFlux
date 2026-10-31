namespace TransFlux.Application.Contracts;

public sealed class SchedulerOptions
{
    public int MaxConcurrentTranscodes { get; set; } = 1;
    public int MaxConcurrentProbes { get; set; } = 2;
    public bool AllowAutomaticFallback { get; set; } = true;
    public int MaxAutoFallbackAttemptsPerJob { get; set; } = 1;
    public TimeSpan ProgressThrottle { get; set; } = TimeSpan.FromMilliseconds(500);
    public bool FullVerification { get; set; }
}
