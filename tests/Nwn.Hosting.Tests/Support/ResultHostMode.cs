using Nwn.Hosting.Bootstrap;

namespace Nwn.Hosting.Tests.Support;

/// <summary>
/// An <see cref="IHostMode"/> that answers run-script calls with an integer result
/// (<see cref="IHostMode.OnRunScriptResult"/>) and records each call with the <c>OBJECT_SELF</c> the closure handler
/// held during it. Its boolean <see cref="OnRunScript"/> must never be called.
/// </summary>
internal sealed class ResultHostMode(CoreFunctionHandler coreFunctions, List<string> events) : IHostMode
{
    public Dictionary<string, int> Results { get; } = [];

    public string? ThrowsFor { get; init; }

    public void Start()
    {
    }

    public bool BeforeFrame() => false;

    public void RunFrame(ulong frame)
    {
    }

    public bool OnRunScript(string script, uint objectSelf) =>
        throw new InvalidOperationException("NativeHost must prefer OnRunScriptResult when the mode implements it.");

    public int OnRunScriptResult(string script, uint objectSelf)
    {
        events.Add($"mode.OnRunScriptResult:{script}@0x{coreFunctions.ObjectSelf:X8}");
        if (script == ThrowsFor)
        {
            throw new InvalidOperationException("Injected run-script failure.");
        }
        return Results.TryGetValue(script, out var result) ? result : NativeHost.ScriptNotHandled;
    }

    public void OnSignal(string signal)
    {
    }

    public void OnDestroyServer()
    {
    }

    public void OnCallbackFailure(string phase, Exception exception) => events.Add($"mode.OnCallbackFailure:{phase}");

    public void OnFrameScheduleFailure(Exception exception)
    {
    }
}
