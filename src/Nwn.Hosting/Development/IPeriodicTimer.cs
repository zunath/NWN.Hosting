namespace Nwn.Hosting.Development;

/// <summary>
/// The slice of <see cref="PeriodicTimer"/> the host's background loops need, so a test can supply
/// a manually ticked fake instead of waiting on real wall-clock time.
/// </summary>
public interface IPeriodicTimer : IDisposable
{
    ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken);
}
