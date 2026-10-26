using NWN.Core;
using NWNX.NET;

namespace Nwn.Hosting.Bootstrap;

/// <summary>
/// NWN.Core's closure seam for the permanent host: every <c>AssignCommand</c>, <c>DelayCommand</c>
/// and <c>ActionDoCommand</c> closure is kept here under an event id until NWNX calls it back on the
/// engine thread (<see cref="Dispatch"/>), and <c>OBJECT_SELF</c> follows the callback's object.
/// </summary>
public sealed class CoreFunctionHandler : ICoreFunctionHandler, IDisposable
{
    private const int MaximumPendingClosures = 64;
    private readonly object _gate = new();
    private readonly int _engineThreadId;
    private readonly Dictionary<ulong, Action> _closures = [];
    private ulong _nextEventId = 1;
    private uint _objectSelf = NWScript.OBJECT_INVALID;
    private bool _disposed;

    public CoreFunctionHandler(int engineThreadId) => _engineThreadId = engineThreadId;

    public uint ObjectSelf => _objectSelf;

    public int PendingCount { get { lock (_gate) return _closures.Count; } }

    public void ClosureActionDoCommand(uint obj, Action action) =>
        Schedule(obj, action, eventId => NWNXAPI.ClosureActionDoCommand(obj, eventId), "ActionDoCommand");

    public void ClosureAssignCommand(uint obj, Action action) =>
        Schedule(obj, action, eventId => NWNXAPI.ClosureAssignCommand(obj, eventId), "AssignCommand");

    public void ClosureDelayCommand(uint obj, float duration, Action action)
    {
        if (!float.IsFinite(duration) || duration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        Schedule(obj, action, eventId => NWNXAPI.ClosureDelayCommand(obj, duration, eventId), "DelayCommand");
    }

    public IDisposable EnterObjectSelf(uint objectSelf)
    {
        AssertEngineThread();
        var previous = _objectSelf;
        _objectSelf = objectSelf;
        return new ObjectSelfScope(this, previous);
    }

    public void Dispatch(ulong eventId, uint objectSelf)
    {
        AssertEngineThread();
        Action? action;
        lock (_gate)
        {
            if (_disposed || !_closures.Remove(eventId, out action))
            {
                NativeHost.Log("closure", $"ignored stale event={eventId} object=0x{objectSelf:X8}");
                return;
            }
        }
        using var scope = EnterObjectSelf(objectSelf);
        action();
    }

    public void Dispose()
    {
        AssertEngineThread();
        lock (_gate)
        {
            _disposed = true;
            _closures.Clear();
        }
        _objectSelf = NWScript.OBJECT_INVALID;
    }

    /// <summary>Drops every closure still waiting for its callback and refuses new ones; returns how many were dropped.</summary>
    public int CloseAndCancel()
    {
        AssertEngineThread();
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }
            var cancelled = _closures.Count;
            _closures.Clear();
            _disposed = true;
            return cancelled;
        }
    }

    private void Schedule(uint obj, Action action, Func<ulong, int> nativeSchedule, string operation)
    {
        AssertEngineThread();
        ArgumentNullException.ThrowIfNull(action);
        ulong eventId;
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CoreFunctionHandler));
            }
            if (_closures.Count >= MaximumPendingClosures)
            {
                throw new InvalidOperationException($"NWN closure capacity ({MaximumPendingClosures}) was exhausted.");
            }
            eventId = _nextEventId++;
            _closures.Add(eventId, action);
        }
        if (nativeSchedule(eventId) != 0)
        {
            return;
        }
        lock (_gate) _closures.Remove(eventId);
        throw new InvalidOperationException($"NWNX rejected {operation} closure event {eventId} for object 0x{obj:X8}.");
    }

    private void RestoreObjectSelf(uint value)
    {
        AssertEngineThread();
        _objectSelf = value;
    }

    private void AssertEngineThread()
    {
        if (Environment.CurrentManagedThreadId != _engineThreadId)
        {
            throw new InvalidOperationException("NWN closure scheduling escaped the engine thread.");
        }
    }

    private sealed class ObjectSelfScope(CoreFunctionHandler owner, uint previous) : IDisposable
    {
        private CoreFunctionHandler? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RestoreObjectSelf(previous);
    }
}
