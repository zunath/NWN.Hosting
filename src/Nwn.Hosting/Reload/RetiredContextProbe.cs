namespace Nwn.Hosting.Reload;

public sealed record RetiredContextProbe(
    int ProbedContexts,
    int CollectedContexts,
    int RetainedContexts,
    TimeSpan Duration,
    IReadOnlyList<RetiredContextSample> Samples);
