using Nwn.Hosting.Development;

namespace Nwn.Hosting.Tests.Support;

/// <summary>
/// Ticks only when told to; <see cref="TickAndWaitAsync"/> also waits for the resulting poll iteration to
/// finish before returning, so a test drives a background loop one event-driven step at a time.
/// </summary>
internal sealed class FakePeriodicTimer : IPeriodicTimer
{
    private TaskCompletionSource<bool>? _pending;
    private TaskCompletionSource? _iterationComplete;
    private readonly object _gate = new();

    public async Task TickAndWaitAsync()
    {
        TaskCompletionSource<bool> completion;
        TaskCompletionSource iterationComplete;
        lock (_gate)
        {
            completion = _pending ?? throw new InvalidOperationException("No pending WaitForNextTickAsync call to release.");
            _pending = null;
            iterationComplete = _iterationComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        completion.TrySetResult(true);
        await iterationComplete.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _iterationComplete?.TrySetResult();
            _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = _pending.Task;
            return new ValueTask<bool>(task.WaitAsync(cancellationToken));
        }
    }

    public void Dispose() { }
}
