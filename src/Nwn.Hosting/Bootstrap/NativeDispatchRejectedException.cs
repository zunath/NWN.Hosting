namespace Nwn.Hosting.Bootstrap;

/// <summary>Thrown into a task submitted to the engine-thread queue when the queue is at capacity.</summary>
public sealed class NativeDispatchRejectedException(int capacity) : InvalidOperationException($"Native engine dispatch queue capacity ({capacity}) was exhausted.");
