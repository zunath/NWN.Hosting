using System.Collections.Concurrent;
using Nwn.Hosting.Bootstrap;
using Nwn.Hosting.Testing.Support;

namespace Nwn.Hosting.Tests.Bootstrap;

[TestClass]
[TestCategory("Unit")]
public sealed class NativeDispatchTests
{
    [TestMethod]
    public void DefersNativeEngineWorkToABoundedMainLoopPump()
    {
        var binding = new NativeBindings(Environment.CurrentManagedThreadId);
        var ran = 0;
        var queued = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            var work = binding.InvokeOnEngineThreadAsync(() =>
            {
                Interlocked.Exchange(ref ran, 1);
                return ValueTask.CompletedTask;
            }, CancellationToken.None).AsTask();
            queued.TrySetResult(work);
            await work;
        });

        TestAssertions.Require(SpinWait.SpinUntil(() => queued.Task.IsCompleted, TimeSpan.FromSeconds(1)), "Background work did not queue for the engine thread.");
        TestAssertions.Require(Volatile.Read(ref ran) == 0, "Off-thread work executed before a main-loop pump.");
        binding.Pump(maxOperations: 1);
        TestAssertions.Require(queued.Task.Result.IsCompletedSuccessfully && Volatile.Read(ref ran) == 1, "Queued work did not complete on the pump.");
        binding.BeginShutdown();
    }

    [TestMethod]
    public void RejectsEngineWorkWhenBoundedAdmissionIsFull()
    {
        var binding = new NativeBindings(Environment.CurrentManagedThreadId, maxPendingOperations: 1);
        var submitted = new ConcurrentBag<Task>();
        using var ready = new CountdownEvent(2);
        for (var index = 0; index < 2; index++)
        {
            _ = Task.Run(() =>
            {
                var work = binding.InvokeOnEngineThreadAsync(() => ValueTask.CompletedTask, CancellationToken.None).AsTask();
                submitted.Add(work);
                ready.Signal();
            });
        }

        TestAssertions.Require(ready.Wait(TimeSpan.FromSeconds(1)), "Background work did not reach bounded admission.");
        TestAssertions.Require(submitted.Count(task => !task.IsCompleted) == 1, "Exactly one work item should be admitted at capacity one.");
        TestAssertions.Require(submitted.Count(task => task.IsFaulted && task.Exception?.GetBaseException() is NativeDispatchRejectedException) == 1,
            "Capacity exhaustion must produce an explicit rejection.");
        binding.Pump(maxOperations: 1);
        TestAssertions.Require(submitted.All(task => task.IsCompleted), "Admitted and rejected work must both become terminal.");
        binding.BeginShutdown();
    }

    [TestMethod]
    public void TerminatesQueuedAndFastPathWorkAfterNativeShutdown()
    {
        var binding = new NativeBindings(Environment.CurrentManagedThreadId, maxPendingOperations: 32);
        var ran = 0;
        var submitted = new ConcurrentBag<Task>();
        using var ready = new CountdownEvent(16);
        for (var index = 0; index < 16; index++)
        {
            _ = Task.Run(() =>
            {
                var work = binding.InvokeOnEngineThreadAsync(() =>
                {
                    Interlocked.Increment(ref ran);
                    return ValueTask.CompletedTask;
                }, CancellationToken.None).AsTask();
                submitted.Add(work);
                ready.Signal();
            });
        }

        TestAssertions.Require(ready.Wait(TimeSpan.FromSeconds(1)), "Background work did not queue before shutdown.");
        binding.BeginShutdown();
        binding.Pump(maxOperations: 32);
        TestAssertions.Require(submitted.All(task => task.IsCompleted), "Shutdown must terminally resolve every admitted operation.");
        TestAssertions.Require(Volatile.Read(ref ran) == 0, "No queued operation may run after shutdown.");
        var fastPath = binding.InvokeOnEngineThreadAsync(() =>
        {
            Interlocked.Increment(ref ran);
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask();
        TestAssertions.Require(fastPath.IsCanceled && Volatile.Read(ref ran) == 0, "The engine-thread fast path must reject after shutdown.");
    }
}
