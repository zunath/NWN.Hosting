using Nwn.Hosting.Bootstrap;

namespace Nwn.Hosting.Tests.Support;

/// <summary>An <see cref="IHostMode"/> that appends every call it receives to a shared event list.</summary>
internal sealed class RecordingHostMode(List<string> events) : IHostMode
{
    public bool RunsFrames { get; set; }

    public Func<string>? ObserveOnStart { get; set; }

    public Exception? LastCallbackFailure { get; private set; }

    public Exception? LastFrameScheduleFailure { get; private set; }

    public string? CallbackFailurePhase { get; private set; }

    public void Start() => events.Add($"mode.Start{(ObserveOnStart is null ? string.Empty : $"[{ObserveOnStart()}]")}");

    public bool BeforeFrame()
    {
        events.Add("mode.BeforeFrame");
        return RunsFrames;
    }

    public void RunFrame(ulong frame) => events.Add($"mode.RunFrame:{frame}");

    public bool OnRunScript(string script, uint objectSelf)
    {
        events.Add($"mode.OnRunScript:{script}");
        return false;
    }

    public void OnSignal(string signal) => events.Add($"mode.OnSignal:{signal}");

    public void OnDestroyServer() => events.Add("mode.OnDestroyServer");

    public void OnCallbackFailure(string phase, Exception exception)
    {
        events.Add($"mode.OnCallbackFailure:{phase}");
        CallbackFailurePhase = phase;
        LastCallbackFailure = exception;
    }

    public void OnFrameScheduleFailure(Exception exception)
    {
        events.Add("mode.OnFrameScheduleFailure");
        LastFrameScheduleFailure = exception;
    }
}
