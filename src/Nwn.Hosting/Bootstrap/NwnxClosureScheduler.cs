using NWNX.NET;

namespace Nwn.Hosting.Bootstrap;

/// <summary>Schedules closures through NWNX DotNET, which calls the host's closure handler back with the event id.</summary>
internal sealed class NwnxClosureScheduler : INativeClosureScheduler
{
    public static NwnxClosureScheduler Instance { get; } = new();

    private NwnxClosureScheduler()
    {
    }

    public int ActionDoCommand(uint obj, ulong eventId) => NWNXAPI.ClosureActionDoCommand(obj, eventId);

    public int AssignCommand(uint obj, ulong eventId) => NWNXAPI.ClosureAssignCommand(obj, eventId);

    public int DelayCommand(uint obj, float duration, ulong eventId) => NWNXAPI.ClosureDelayCommand(obj, duration, eventId);
}
