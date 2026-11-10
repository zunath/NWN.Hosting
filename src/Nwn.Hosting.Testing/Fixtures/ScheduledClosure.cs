namespace Nwn.Hosting.Testing.Fixtures;

/// <summary>One closure a <see cref="RecordingClosureScheduler"/> was asked to schedule.</summary>
/// <param name="Kind"><c>ActionDoCommand</c>, <c>AssignCommand</c> or <c>DelayCommand</c>.</param>
/// <param name="Object">The object the closure runs as.</param>
/// <param name="Duration">The delay in seconds; 0 unless <paramref name="Kind"/> is <c>DelayCommand</c>.</param>
/// <param name="EventId">The id the engine would call the closure handler back with.</param>
public sealed record ScheduledClosure(string Kind, uint Object, float Duration, ulong EventId);
