using System.Collections.Concurrent;
using Nwn.Hosting.Contracts.Generations;

namespace Nwn.Hosting.Testing.Fixtures;

/// <summary>
/// Managed stand-in for the permanent NWN adapter. It owns registrations, pending entries, and
/// execution accounting; an actual adapter must map these handles to real NWNX/NWN registrations.
/// It implements <see cref="IFixtureCallbackBindings"/> so fixture generations can register callbacks the test then drives.
/// </summary>
public sealed class FixtureNativeBindings : IPermanentNativeBindings, IFixtureCallbackBindings, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<CallbackRegistration> _registrations = [];
    private readonly Queue<CallbackRegistration> _pending = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _reports = new();
    private readonly SingleThreadDispatcher _engine = new();
    private TaskCompletionSource? _blockedCallbackEntered;
    private ManualResetEventSlim? _releaseBlockedCallback;
    private int _blockNextCallback;
    private int _executing;

    public ValueTask InvokeOnEngineThreadAsync(Func<ValueTask> action, CancellationToken cancellationToken) =>
        _engine.InvokeAsync(action, cancellationToken);

    public int CallbackCount => Volatile.Read(ref _executing) + PendingCount;
    private int PendingCount { get { lock (_gate) return _pending.Count; } }
    public int ActiveRegistrationCount { get { lock (_gate) return _registrations.Count(item => item.Active); } }

    public IDisposable SubscribeEvent(string eventName, Action callback) => Register("event", eventName, callback);
    public IDisposable SubscribeTimer(string timerName, Action callback) => Register("timer", timerName, callback);
    public IDisposable SubscribeUi(string windowId, Action callback) => Register("ui", windowId, callback);

    public void Report(string category, string message)
    {
        _reports.GetOrAdd(category, _ => new()).Enqueue(message);
        if (category == "callback-entered" && Interlocked.Exchange(ref _blockNextCallback, 0) != 0)
        {
            _blockedCallbackEntered!.TrySetResult();
            _releaseBlockedCallback!.Wait();
        }
    }

    public void BlockNextCallbackBody()
    {
        _blockedCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _releaseBlockedCallback = new ManualResetEventSlim(false);
        Interlocked.Exchange(ref _blockNextCallback, 1);
    }

    public Task WaitForBlockedCallbackAsync() => _blockedCallbackEntered?.Task ?? throw new InvalidOperationException("No callback block was armed.");
    public void ReleaseBlockedCallback() => _releaseBlockedCallback?.Set();

    public void QueueEveryKind()
    {
        lock (_gate)
        {
            foreach (var registration in _registrations.Where(x => x.Active)) _pending.Enqueue(registration);
        }
    }

    public ValueTask ExecutePendingAsync() => _engine.InvokeAsync(() =>
    {
        while (ExecuteOnePending()) { }
        return ValueTask.CompletedTask;
    }, CancellationToken.None);

    public ValueTask ExecuteOnePendingAsync() => _engine.InvokeAsync(() =>
    {
        _ = ExecuteOnePending();
        return ValueTask.CompletedTask;
    }, CancellationToken.None);

    public int Count(string category) => _reports.TryGetValue(category, out var messages) ? messages.Count : 0;
    public IReadOnlyCollection<string> Messages(string category) => _reports.TryGetValue(category, out var messages) ? messages.ToArray() : [];

    public async ValueTask DisposeAsync()
    {
        _releaseBlockedCallback?.Set();
        _releaseBlockedCallback?.Dispose();
        await _engine.DisposeAsync();
    }

    private bool ExecuteOnePending()
    {
        CallbackRegistration? registration;
        lock (_gate) registration = _pending.Count == 0 ? null : _pending.Dequeue();
        if (registration is null || !registration.Active) return false;
        Interlocked.Increment(ref _executing);
        try { registration.Callback(); }
        finally { Interlocked.Decrement(ref _executing); }
        return true;
    }

    private IDisposable Register(string kind, string name, Action callback)
    {
        _engine.AssertEngineThread();
        var registration = new CallbackRegistration(kind, name, callback, this);
        lock (_gate) _registrations.Add(registration);
        return registration;
    }

    private void Remove(CallbackRegistration registration)
    {
        _engine.AssertEngineThread();
        lock (_gate)
        {
            _registrations.Remove(registration);
            var survivors = _pending.Where(candidate => !ReferenceEquals(candidate, registration)).ToArray();
            _pending.Clear();
            foreach (var survivor in survivors) _pending.Enqueue(survivor);
        }
    }

    private sealed class CallbackRegistration(string kind, string name, Action callback, FixtureNativeBindings owner) : IDisposable
    {
        private int _active = 1;
        public string Kind { get; } = kind;
        public string Name { get; } = name;
        public Action Callback { get; } = callback;
        public bool Active => Volatile.Read(ref _active) != 0;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) != 0) owner.Remove(this);
        }
    }
}
