using System.Text.Json.Serialization;

namespace Nwn.Hosting.Contracts.Generations;

public sealed record DevelopmentReloadRequest(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] Guid GenerationId)
{
    public const string GenerationsDirectoryName = "generations";
    public const string GameAssemblyFileName = "Xenomech.Game.dll";

    public string GetAssemblyRelativePath() =>
        Path.Combine(GenerationsDirectoryName, GenerationId.ToString("N"), GameAssemblyFileName);
}
