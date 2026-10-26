namespace Nwn.Hosting.Contracts.Generations;

/// <summary>
/// The permanent host's engine-thread seam, handed to every generation in
/// <see cref="GenerationActivation.Native"/>. The permanent host implements it once for the process.
/// </summary>
public interface IPermanentNativeBindings
{
    /// <summary>
    /// Runs <paramref name="action"/> on the NWN engine thread: inline when the caller is already on
    /// it, otherwise on a later main-loop frame.
    /// </summary>
    ValueTask InvokeOnEngineThreadAsync(Func<ValueTask> action, CancellationToken cancellationToken);

    /// <summary>Thread-safe logging only; it must not call into NWN from a background continuation.</summary>
    void Report(string category, string message);
}
