using System.Text.Json.Serialization;

namespace Nwn.Hosting.Contracts.Generations;

public sealed record DevelopmentReloadResult(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] Guid GenerationId,
    [property: JsonRequired] DevelopmentReloadStatus Status,
    [property: JsonRequired] string Detail);
