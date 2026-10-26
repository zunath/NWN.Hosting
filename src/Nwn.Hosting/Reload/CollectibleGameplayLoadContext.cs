using System.Reflection;
using System.Runtime.Loader;
using Nwn.Hosting.Contracts.Generations;

namespace Nwn.Hosting.Reload;

internal sealed class CollectibleGameplayLoadContext : AssemblyLoadContext
{
    // NWN.Core/NWN.Native/NWNX.NET: these live in the permanent, non-collectible host context (a
    // permanent extension's load context shares them for the same reason). A generation-local copy
    // would run against its own, never-initialized static engine-binding state instead of the host's
    // real one. Sharing them is safe for unload: they are reference targets FROM the collectible
    // context, never the other way around, so they cannot root a retired generation.
    //
    // GenerationLoadOptions.SharedAssemblyNames: the consumer's own process-lifetime assemblies follow
    // the same rule. A root logger the host hands a generation through
    // GenerationActivation.HostServices must be the exact same CLR type on both sides of the boundary,
    // or every cast/interface check on it fails; a connection pool or an engine wrapper the host
    // initialized once must not be reopened or re-bound per generation, and a generation-local copy
    // would defeat unload for the same reason. The host's own copy is a reference target FROM the
    // collectible context, never the other way around.
    //
    // Anything not listed - a dependency-injection container, for one - loads fresh inside this
    // collectible context on every generation, or its own static/compiled-expression caches would pin
    // the context and defeat unload.
    private static readonly string[] AlwaysSharedAssemblyNames = ["NWN.Core", "NWN.Native", "NWNX.NET"];

    private readonly AssemblyDependencyResolver _resolver;
    private readonly HashSet<string> _sharedAssemblyNames;

    public CollectibleGameplayLoadContext(string gameplayAssemblyPath, GenerationLoadOptions options)
        : base(ContextName(options), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(gameplayAssemblyPath);
        _sharedAssemblyNames = new HashSet<string>(AlwaysSharedAssemblyNames.Concat(options.SharedAssemblyNames), StringComparer.Ordinal);
    }

    /// <summary>Resolves <paramref name="assemblyName"/> exactly as the runtime does when a generation references it.</summary>
    internal Assembly? ResolveForTest(AssemblyName assemblyName) => Load(assemblyName);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == typeof(IGameplayGeneration).Assembly.GetName().Name)
        {
            // Do not depend on Default: NWNX can host the permanent layer in another ALC.
            return typeof(IGameplayGeneration).Assembly;
        }

        if (assemblyName.Name is { } name && _sharedAssemblyNames.Contains(name))
        {
            var hostContext = GetLoadContext(typeof(CollectibleGameplayLoadContext).Assembly)
                ?? throw new InvalidOperationException("The permanent host has no assembly load context.");
            var loaded = hostContext.Assemblies.SingleOrDefault(
                assembly => StringComparer.Ordinal.Equals(assembly.GetName().Name, name));
            // Not yet loaded (e.g. the very first generation of a process): ask the HOST context to
            // resolve it by name, via its own probing, so it lands there rather than in this
            // collectible context.
            return loaded ?? hostContext.LoadFromAssemblyName(assemblyName);
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    private static string ContextName(GenerationLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"{options.ContextNamePrefix}-{Guid.NewGuid():N}";
    }
}
