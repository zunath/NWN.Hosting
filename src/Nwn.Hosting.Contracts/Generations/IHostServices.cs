namespace Nwn.Hosting.Contracts.Generations;

/// <summary>
/// Type-keyed lookup for host-owned singletons that must cross into a collectible generation
/// without forcing a package dependency (e.g. a logging library) onto this contracts assembly. The
/// permanent host implements this once; a generation's composition root resolves what it needs by type.
/// </summary>
public interface IHostServices
{
    /// <summary>Returns the host-owned singleton for <paramref name="serviceType"/>, or null if none is registered.</summary>
    object? Resolve(Type serviceType);
}
