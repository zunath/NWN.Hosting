namespace Nwn.Hosting.Contracts.Generations;

/// <summary>Tracks callback bodies separately from gameplay-owned asynchronous work.</summary>
public interface IGenerationExecution
{
    IDisposable? TryEnter();
    void CloseAdmission();
    ValueTask DrainAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
