namespace Nwn.Hosting.Contracts.Generations;

/// <summary>What the permanent host hands a generation when it activates it.</summary>
/// <param name="Generation">This activation's generation number, unique for the process.</param>
/// <param name="Native">The engine-thread seam.</param>
/// <param name="Liveness">Answers whether a generation number is still the active one.</param>
/// <param name="Execution">Tracks this generation's callback bodies so a reload can drain them.</param>
/// <param name="HostServices">Process-lifetime singletons the host shares with every generation.</param>
public sealed record GenerationActivation(
    long Generation,
    IPermanentNativeBindings Native,
    IGenerationLiveness Liveness,
    IGenerationExecution Execution,
    IHostServices? HostServices = null);
