namespace Nwn.Hosting.Contracts.Generations;

/// <summary>
/// Optional per-generation script router. The permanent host calls
/// <see cref="TryHandle"/> for a RunScript signal it does not itself recognize; a generation that
/// exposes one through <see cref="IGameplayGeneration.Scripts"/> gets first refusal on every
/// script name the host does not hard-code.
/// </summary>
public interface IScriptDispatcher
{
    /// <summary>
    /// Runs every handler this generation registered for <paramref name="scriptName"/>. Returns
    /// false ("not handled") when the name is unknown to this generation -- the caller must then
    /// fall back to the module's own compiled script. <paramref name="conditionalResult"/> carries
    /// a StartingConditional-style handler's boolean result when the route has one; it is false and
    /// meaningless otherwise. Must never throw: an internal failure is this generation's own
    /// concern to log and swallow, so one broken script can never break every other script in the
    /// module.
    /// </summary>
    bool TryHandle(string scriptName, uint objectSelf, out bool conditionalResult);
}
