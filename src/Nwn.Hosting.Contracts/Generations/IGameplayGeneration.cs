namespace Nwn.Hosting.Contracts.Generations;

/// <summary>
/// A reloadable generation's entry point: the one concrete implementation in the assembly
/// <c>ReloadHost</c> loads into a collectible context. It must remain in the permanent host context;
/// values crossing this boundary are primitives, strings, or host-owned services, never gameplay
/// object graphs.
/// </summary>
public interface IGameplayGeneration : IAsyncDisposable
{
    string BuildId { get; }

    /// <summary>Supplies the candidate's own directory before validation; paths remain specific to each generation.</summary>
    void ConfigureSourceDirectory(string directoryPath) { }

    /// <summary>Runs off the engine thread before the previous generation is touched; a throw rejects the candidate.</summary>
    ValueTask ValidateAsync(CancellationToken cancellationToken);

    /// <summary>Runs on the engine thread once the previous generation is gone.</summary>
    ValueTask ActivateAsync(GenerationActivation activation, CancellationToken cancellationToken);

    /// <summary>Runs on the engine thread when this generation is being replaced: stop accepting callbacks and cancel owned work.</summary>
    ValueTask BeginQuiescenceAsync(CancellationToken cancellationToken);

    /// <summary>Waits, within <paramref name="timeout"/>, for work this generation still owns to finish before it is disposed.</summary>
    ValueTask DrainAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Non-null once activated when this generation wants the host to forward engine RunScript
    /// calls to it. A generation that does not opt in is never asked for a script.
    /// </summary>
    IScriptDispatcher? Scripts => null;

    /// <summary>
    /// Non-null once activated when this generation wants the host to forward native main-loop
    /// ticks to it. A generation that does not opt in is never ticked.
    /// </summary>
    IMainLoopSink? MainLoop => null;

    /// <summary>
    /// The persistence schema version this generation requires before it may be activated for real
    /// gameplay. Defaults to 0 -- "no requirement" -- which test fixture generations use; a gameplay
    /// generation declares a real value. <c>ReloadHost.ReloadAsync</c>'s optional schema-gate
    /// callback reads this from the freshly loaded candidate before deciding whether a RELOAD (never
    /// the very first activation, which has no previous generation to protect) may proceed.
    /// </summary>
    int RequiredSchemaVersion => 0;
}
