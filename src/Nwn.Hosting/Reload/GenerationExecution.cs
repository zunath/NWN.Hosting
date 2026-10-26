using Nwn.Hosting.Contracts.Generations;

namespace Nwn.Hosting.Reload;

internal sealed class GenerationExecution : IGenerationExecution
{
    private readonly object _gate = new();
    private TaskCompletionSource _idle = Completed();
    private int _executing;
    private bool _admissionClosed;

    public IDisposable? TryEnter()
    {
        lock (_gate)
        {
            if (_admissionClosed) return null;
            if (_executing++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Lease(this);
        }
    }

    public void CloseAdmission()
    {
        lock (_gate) _admissionClosed = true;
    }

    public async ValueTask DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task idle;
        lock (_gate) idle = _idle.Task;
        await idle.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private void Exit()
    {
        lock (_gate)
        {
            if (--_executing == 0) _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class Lease(GenerationExecution owner) : IDisposable
    {
        private GenerationExecution? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}
