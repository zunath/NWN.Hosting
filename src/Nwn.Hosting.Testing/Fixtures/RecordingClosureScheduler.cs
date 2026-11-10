using Nwn.Hosting.Bootstrap;

namespace Nwn.Hosting.Testing.Fixtures;

/// <summary>
/// A stand-in for the engine's closure scheduling: a <c>CoreFunctionHandler</c> created with it records every closure
/// instead of calling NWNX, and the test delivers the engine's callback itself by passing a recorded
/// <see cref="ScheduledClosure.EventId"/> to <c>CoreFunctionHandler.Dispatch</c> on the engine thread.
/// </summary>
public sealed class RecordingClosureScheduler : INativeClosureScheduler
{
    private readonly object _gate = new();
    private readonly List<ScheduledClosure> _scheduled = [];

    /// <summary>When true, every later closure is refused as the engine refuses one it cannot schedule.</summary>
    public bool Refuse { get; set; }

    /// <summary>Every closure accepted so far, in scheduling order.</summary>
    public IReadOnlyList<ScheduledClosure> Scheduled
    {
        get
        {
            lock (_gate) return _scheduled.ToArray();
        }
    }

    public int ActionDoCommand(uint obj, ulong eventId) => Record(new ScheduledClosure(nameof(ActionDoCommand), obj, 0, eventId));

    public int AssignCommand(uint obj, ulong eventId) => Record(new ScheduledClosure(nameof(AssignCommand), obj, 0, eventId));

    public int DelayCommand(uint obj, float duration, ulong eventId) => Record(new ScheduledClosure(nameof(DelayCommand), obj, duration, eventId));

    private int Record(ScheduledClosure closure)
    {
        if (Refuse)
        {
            return 0;
        }
        lock (_gate) _scheduled.Add(closure);
        return 1;
    }
}
