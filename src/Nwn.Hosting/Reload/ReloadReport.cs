namespace Nwn.Hosting.Reload;

public sealed record ReloadReport(
    ReloadOutcome Outcome,
    long? PreviousGeneration,
    long? ActiveGeneration,
    TimeSpan CandidateValidation,
    TimeSpan Drain,
    TimeSpan ApplyPause,
    TimeSpan UnloadProbe,
    bool PreviousContextCollected,
    string Detail);
