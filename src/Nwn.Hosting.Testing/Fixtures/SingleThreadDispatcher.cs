using System.Collections.Concurrent;

namespace Nwn.Hosting.Testing.Fixtures;

/// <summary>
/// A dedicated thread that stands in for the NWN engine thread in the managed reload proofs. Like the
/// engine thread it never exits: once disposed it parks for the rest of the process, because a thread
/// that ran generation code and exits after a later generation reused its thread-static slots makes the
/// runtime free that generation's objects (docs/threads-outlive-generations.md in the Nwn.Hosting
/// repository).
/// </summary>
public sealed class SingleThreadDispatcher : IAsyncDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _threadId;

    public SingleThreadDispatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "reload-fixture-engine" };
        _thread.Start();
        _started.Task.GetAwaiter().GetResult();
    }

    public int ThreadId => Volatile.Read(ref _threadId);

    public ValueTask InvokeAsync(Func<ValueTask> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem(action, cancellationToken, completion));
        return new ValueTask(completion.Task);
    }

    public void AssertEngineThread()
    {
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref _threadId))
        {
            throw new InvalidOperationException("Native registration or removal did not execute on the dedicated engine thread.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _queue.CompleteAdding();
        if (_drained.Task.Wait(DrainTimeout))
        {
            _queue.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void Run()
    {
        Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
        _started.SetResult();
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work.CancellationToken.ThrowIfCancellationRequested();
                work.Action().AsTask().GetAwaiter().GetResult();
                work.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                work.Completion.TrySetException(exception);
            }
        }

        _drained.SetResult();
        Thread.Sleep(Timeout.Infinite);
    }

    private sealed record WorkItem(Func<ValueTask> Action, CancellationToken CancellationToken, TaskCompletionSource Completion);
}
