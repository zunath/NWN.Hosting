namespace Nwn.Hosting.Development;

/// <summary>The production <see cref="IPeriodicTimer"/>: a real <see cref="PeriodicTimer"/>.</summary>
public sealed class PeriodicTimerAdapter(TimeSpan interval) : IPeriodicTimer
{
    private readonly PeriodicTimer _inner = new(interval);

    public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken) => _inner.WaitForNextTickAsync(cancellationToken);

    public void Dispose() => _inner.Dispose();
}
