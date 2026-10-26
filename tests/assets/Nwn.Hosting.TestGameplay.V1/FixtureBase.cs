using Nwn.Hosting.Contracts.Generations;
using Nwn.Hosting.Testing.Fixtures;

namespace Nwn.Hosting.Reload.Gameplay;

public abstract class FixtureBase : IGameplayGeneration
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _workGate = new();
    private TaskCompletionSource _idle = CompletedIdle();
    private int _executingWork;
    private GenerationActivation? _activation;
    private int _quiescing;
    private int _disposed;

    public abstract string BuildId { get; }

    public ValueTask ValidateAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("NWN_HOSTING_RELOAD_VALIDATE_FAIL") == BuildId)
        {
            throw new InvalidOperationException($"Injected validation failure for {BuildId}.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(GenerationActivation activation, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("NWN_HOSTING_RELOAD_ACTIVATE_FAIL") == BuildId)
        {
            throw new InvalidOperationException($"Injected post-teardown activation failure for {BuildId}.");
        }

        _activation = activation;
        if (activation.Native is IFixtureCallbackBindings callbacks)
        {
            _subscriptions.Add(callbacks.SubscribeEvent("fixture-event", () => DispatchCallback("event")));
            _subscriptions.Add(callbacks.SubscribeTimer("fixture-timer", () => DispatchCallback("timer")));
            _subscriptions.Add(callbacks.SubscribeUi("fixture-ui", () => DispatchCallback("ui")));
        }
        activation.Native.Report("activate", $"{BuildId}:{activation.Generation}");
        return ValueTask.CompletedTask;
    }

    public ValueTask BeginQuiescenceAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _quiescing, 1);
        _stopping.Cancel();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("NWN_HOSTING_RELOAD_DRAIN_FAIL") == BuildId)
        {
            throw new TimeoutException($"Injected drain failure for {BuildId}.");
        }
        Task idle;
        lock (_workGate)
        {
            idle = _idle.Task;
        }
        await idle.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await BeginQuiescenceAsync(CancellationToken.None).ConfigureAwait(false);
        await DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        _stopping.Dispose();
        _activation = null;
    }

    private void DispatchCallback(string source)
    {
        var activation = _activation;
        using var callback = activation?.Execution.TryEnter();
        if (callback is not null)
        {
            activation!.Native.Report("callback-entered", $"{BuildId}:{activation.Generation}");
            Dispatch(source);
        }
    }

    private void Dispatch(string source)
    {
        var activation = _activation;
        if (activation is null || Volatile.Read(ref _quiescing) != 0 || !activation.Liveness.IsCurrent(activation.Generation))
        {
            return;
        }

        activation.Native.Report(source, $"{BuildId}:{activation.Generation}");
        StartOwnedAsync(activation);
    }

    private void StartOwnedAsync(GenerationActivation activation)
    {
        lock (_workGate)
        {
            if (_executingWork++ == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        _ = CompleteAsync(activation, _stopping.Token);
    }

    private async Task CompleteAsync(GenerationActivation activation, CancellationToken cancellationToken)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("NWN_HOSTING_RELOAD_HOLD_WORK_UNTIL_CANCEL") == "1")
            {
                // Held until quiescence cancels it, so only replacement, never scheduler timing, releases it.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken).ConfigureAwait(false);
            }
            // This second liveness test rejects a completion that raced with replacement.
            if (!cancellationToken.IsCancellationRequested && activation.Liveness.IsCurrent(activation.Generation))
            {
                activation.Native.Report("async", $"{BuildId}:{activation.Generation}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activation.Native.Report("cancelled", $"{BuildId}:{activation.Generation}");
        }
        finally
        {
            lock (_workGate)
            {
                if (--_executingWork == 0) _idle.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }
}
