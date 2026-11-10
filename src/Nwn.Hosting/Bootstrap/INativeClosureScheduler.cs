namespace Nwn.Hosting.Bootstrap;

/// <summary>
/// Hands a closure's event id to the engine, which later calls the host's closure callback with it
/// (<see cref="CoreFunctionHandler.Dispatch"/>). A <see cref="CoreFunctionHandler"/> uses NWNX DotNET unless it is
/// given another scheduler; a test passes a recording one (<c>RecordingClosureScheduler</c> in
/// <c>Nwn.Hosting.Testing</c>) to drive closures without a running server. Each method returns 0 when the engine
/// refused the closure and any other value when it accepted it.
/// </summary>
public interface INativeClosureScheduler
{
    /// <summary>Queues the closure as an action of <paramref name="obj"/>.</summary>
    int ActionDoCommand(uint obj, ulong eventId);

    /// <summary>Runs the closure with <paramref name="obj"/> as <c>OBJECT_SELF</c> once the current script ends.</summary>
    int AssignCommand(uint obj, ulong eventId);

    /// <summary>Runs the closure with <paramref name="obj"/> as <c>OBJECT_SELF</c> after <paramref name="duration"/> seconds.</summary>
    int DelayCommand(uint obj, float duration, ulong eventId);
}
