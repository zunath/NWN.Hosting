using Nwn.Hosting.Contracts.Generations;

namespace Nwn.Hosting.Bootstrap;

/// <summary>
/// The permanent host's <see cref="IPermanentNativeBindings"/>. Work submitted on the engine thread
/// runs inline; work submitted from any other thread waits in a bounded queue until a main-loop
/// frame runs it (<see cref="Pump"/>). <see cref="BeginShutdown"/> cancels everything still queued.
/// </summary>
public sealed class NativeBindings : IPermanentNativeBindings
{
    private readonly object _queueGate = new();
    private readonly int _engineThreadId;
    private readonly int _maxPendingOperations;
    private readonly Queue<EngineOperation> _pendingEngineWork = [];
    private bool _stopping;

    public NativeBindings(int engineThreadId, int maxPendingOperations = 64)
    {
        if (maxPendingOperations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingOperations));
        }
        _engineThreadId = engineThreadId;
        _maxPendingOperations = maxPendingOperations;
    }

    public ValueTask InvokeOnEngineThreadAsync(Func<ValueTask> action, CancellationToken cancellationToken)
    {
        lock (_queueGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping)
            {
                return ValueTask.FromCanceled(new CancellationToken(true));
            }

            if (Environment.CurrentManagedThreadId == _engineThreadId)
            {
                return action();
            }

            if (_pendingEngineWork.Count >= _maxPendingOperations)
            {
                return ValueTask.FromException(new NativeDispatchRejectedException(_maxPendingOperations));
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingEngineWork.Enqueue(new EngineOperation(action, cancellationToken, completion));
            return new ValueTask(completion.Task);
        }
    }

    /// <summary>Runs up to <paramref name="maxOperations"/> queued operations on the engine thread.</summary>
    public void Pump(int maxOperations)
    {
        AssertEngineThread();
        for (var completed = 0; completed < maxOperations; completed++)
        {
            lock (_queueGate)
            {
                if (_stopping || _pendingEngineWork.Count == 0)
                {
                    return;
                }
                var operation = _pendingEngineWork.Dequeue();
                if (operation.CancellationToken.IsCancellationRequested)
                {
                    operation.Completion.TrySetCanceled(operation.CancellationToken);
                    continue;
                }

                try
                {
                    var work = operation.Action().AsTask();
                    if (work.IsCompleted)
                    {
                        Complete(operation.Completion, work);
                    }
                    else
                    {
                        _ = CompleteAsync(operation.Completion, work);
                    }
                }
                catch (Exception exception)
                {
                    operation.Completion.TrySetException(exception);
                }
            }
        }
    }

    /// <summary>Cancels every queued operation and refuses new ones, including inline engine-thread work.</summary>
    public void BeginShutdown()
    {
        lock (_queueGate)
        {
            _stopping = true;
            while (_pendingEngineWork.Count != 0) _pendingEngineWork.Dequeue().Completion.TrySetCanceled();
        }
    }

    public void Report(string category, string message) => NativeHost.Log(category, message);

    private void AssertEngineThread()
    {
        if (Environment.CurrentManagedThreadId != _engineThreadId)
        {
            throw new InvalidOperationException("Native engine work escaped the NWN main-loop thread.");
        }
    }

    private static void Complete(TaskCompletionSource completion, Task work)
    {
        if (work.IsCanceled) completion.TrySetCanceled();
        else if (work.IsFaulted) completion.TrySetException(work.Exception!.GetBaseException());
        else completion.TrySetResult();
    }

    private static async Task CompleteAsync(TaskCompletionSource completion, Task work)
    {
        try
        {
            await work.ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private sealed record EngineOperation(Func<ValueTask> Action, CancellationToken CancellationToken, TaskCompletionSource Completion);
}
