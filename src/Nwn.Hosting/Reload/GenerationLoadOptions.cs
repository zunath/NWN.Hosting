namespace Nwn.Hosting.Reload;

/// <summary>How a generation's collectible load context is named and which assemblies it shares with the host.</summary>
public sealed record GenerationLoadOptions
{
    /// <summary>The context name prefix used when a consumer gives none.</summary>
    public const string DefaultContextNamePrefix = "nwn-hosting-gameplay";

    /// <param name="sharedAssemblyNames">
    /// Simple names of assemblies the host owns for the life of the process and every generation must use
    /// instead of loading its own copy, in addition to the ones that are always shared
    /// (<c>NWN.Core</c>, <c>NWN.Native</c>, <c>NWNX.NET</c> and the contracts assembly).
    /// </param>
    /// <param name="contextNamePrefix">The prefix of each collectible context's name; a GUID follows it.</param>
    public GenerationLoadOptions(IReadOnlyCollection<string> sharedAssemblyNames, string contextNamePrefix = DefaultContextNamePrefix)
    {
        ArgumentNullException.ThrowIfNull(sharedAssemblyNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextNamePrefix);
        SharedAssemblyNames = sharedAssemblyNames;
        ContextNamePrefix = contextNamePrefix;
    }

    /// <summary>The options a host uses when it shares nothing beyond the assemblies that are always shared.</summary>
    public static GenerationLoadOptions Default { get; } = new([]);

    /// <summary>Simple names of the consumer's assemblies that resolve to the host's copy.</summary>
    public IReadOnlyCollection<string> SharedAssemblyNames { get; }

    /// <summary>The prefix of each collectible context's name.</summary>
    public string ContextNamePrefix { get; }
}
