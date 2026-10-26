namespace Nwn.Hosting.Contracts.Generations;

/// <summary>
/// Identifies this server process's own row in the host's persistence layer. Wrapped in its own type
/// -- rather than a bare <see cref="Guid"/> -- so <see cref="IHostServices.Resolve"/>, which is keyed by
/// CLR type, can never be asked to resolve an unrelated <see cref="Guid"/> singleton by mistake.
/// </summary>
public readonly record struct ServerInstanceId(Guid Value);
