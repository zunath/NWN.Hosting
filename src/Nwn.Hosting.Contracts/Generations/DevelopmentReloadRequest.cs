using System.Text.Json.Serialization;

namespace Nwn.Hosting.Contracts.Generations;

public sealed record DevelopmentReloadRequest(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] Guid GenerationId)
{
    public const string GenerationsDirectoryName = "generations";

    /// <summary>
    /// The candidate's gameplay assembly relative to the generation root. The consumer names its own
    /// gameplay assembly file; the request carries only the generation identity.
    /// </summary>
    public string GetAssemblyRelativePath(string gameAssemblyFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameAssemblyFileName);
        return Path.Combine(GenerationsDirectoryName, GenerationId.ToString("N"), gameAssemblyFileName);
    }
}
