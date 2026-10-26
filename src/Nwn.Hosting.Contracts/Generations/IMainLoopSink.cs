namespace Nwn.Hosting.Contracts.Generations;

/// <summary>
/// Optional per-generation main-loop tick sink, driven by the host's native main-loop
/// signal. See <see cref="IScriptDispatcher"/> for the identical opt-in shape.
/// </summary>
public interface IMainLoopSink
{
    /// <summary>Must never throw; an internal failure is this generation's own concern to log and swallow.</summary>
    void Tick();
}
