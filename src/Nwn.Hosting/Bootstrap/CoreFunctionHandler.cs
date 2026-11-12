using NWN.Core;

namespace Nwn.Hosting.Bootstrap;

/// <summary>
/// NWN.Core's closure seam for the permanent host: every <c>AssignCommand</c>, <c>DelayCommand</c>
/// and <c>ActionDoCommand</c> closure is kept here under an event id until NWNX calls it back on the
/// engine thread (<see cref="Dispatch"/>), and <c>OBJECT_SELF</c> follows the callback's object.
/// </summary>
/// <remarks>
/// <para>
/// A pending closure holds a delegate of the code that scheduled it, so each closure belongs to a generation: the
/// one <see cref="BeginGeneration"/> named when it was scheduled, or the host (<see cref="HostGeneration"/>) before
/// any generation began. <see cref="RetireGeneration"/> drops every pending closure of a retired generation without
/// running it, so retired code neither runs after its generation was disposed nor keeps its load context alive; the
/// engine's later callback for a dropped closure is ignored. The host's own frame closure always belongs to the
/// host. A consumer that never calls <see cref="BeginGeneration"/> keeps every closure as the host's, as earlier
/// versions did.
/// </para>
/// <para>
/// At most <see cref="MaximumPendingClosures"/> closures may wait for their callback at once
/// (<see cref="DefaultMaximumPendingClosures"/> unless the consumer chose another limit or none).
/// </para>
/// <para>
/// A closure the engine refuses (the scheduler returns 0) is forgotten at once: it never runs, holds no capacity and
/// keeps no generation loaded. <see cref="RejectedClosureHandling"/> decides whether the scheduling call then throws
/// (<see cref="Bootstrap.RejectedClosureHandling.Throw"/>, the default) or returns normally
/// (<see cref="Bootstrap.RejectedClosureHandling.Drop"/>); <see cref="RejectedClosureCount"/> counts refusals either way.
/// The host's own frame closure always throws when refused.
/// </para>
/// </remarks>
public sealed class CoreFunctionHandler : ICoreFunctionHandler, IDisposable
{
    /// <summary>The pending-closure limit of a handler created without one.</summary>
    public const int DefaultMaximumPendingClosures = 64;

    /// <summary>The owner of closures scheduled while no generation has begun, and of the host's own frame closure.</summary>
    public const long HostGeneration = 0;

    private readonly object _gate = new();
    private readonly int _engineThreadId;
    private readonly int? _maximumPendingClosures;
    private readonly INativeClosureScheduler _scheduler;
    private readonly RejectedClosureHandling _rejectedClosureHandling;
    private readonly Dictionary<ulong, PendingClosure> _closures = [];
    // The event ids of dropped closures whose callback has not arrived yet, so it is ignored quietly. An action the
    // engine discards never calls back and keeps its id here: eight bytes, and only on a host that retires generations.
    private readonly HashSet<ulong> _dropped = [];
    private ulong _nextEventId = 1;
    private uint _objectSelf = NWScript.OBJECT_INVALID;
    private long _schedulingGeneration = HostGeneration;
    private bool _schedulingGenerationRetired;
    private long _droppedClosures;
    private long _rejectedClosures;
    private bool _disposed;

    /// <summary>Creates a handler that allows <see cref="DefaultMaximumPendingClosures"/> pending closures.</summary>
    public CoreFunctionHandler(int engineThreadId)
        : this(engineThreadId, DefaultMaximumPendingClosures)
    {
    }

    /// <param name="engineThreadId">The managed id of the NWN engine thread; scheduling, dispatch and the generation calls must run on it.</param>
    /// <param name="maximumPendingClosures">How many closures may wait for their callback at once; null for no limit.</param>
    public CoreFunctionHandler(int engineThreadId, int? maximumPendingClosures)
        : this(engineThreadId, maximumPendingClosures, NwnxClosureScheduler.Instance)
    {
    }

    /// <param name="engineThreadId">The managed id of the NWN engine thread; scheduling, dispatch and the generation calls must run on it.</param>
    /// <param name="maximumPendingClosures">How many closures may wait for their callback at once; null for no limit.</param>
    /// <param name="scheduler">Hands each closure's event id to the engine; the other constructors use NWNX DotNET.</param>
    public CoreFunctionHandler(int engineThreadId, int? maximumPendingClosures, INativeClosureScheduler scheduler)
        : this(engineThreadId, maximumPendingClosures, scheduler, RejectedClosureHandling.Throw)
    {
    }

    /// <param name="engineThreadId">The managed id of the NWN engine thread; scheduling, dispatch and the generation calls must run on it.</param>
    /// <param name="maximumPendingClosures">How many closures may wait for their callback at once; null for no limit.</param>
    /// <param name="scheduler">Hands each closure's event id to the engine; the other constructors use NWNX DotNET.</param>
    /// <param name="rejectedClosureHandling">
    /// Whether scheduling a closure the engine refuses throws (<see cref="Bootstrap.RejectedClosureHandling.Throw"/>, what the
    /// other constructors choose) or returns normally (<see cref="Bootstrap.RejectedClosureHandling.Drop"/>).
    /// </param>
    public CoreFunctionHandler(
        int engineThreadId, int? maximumPendingClosures, INativeClosureScheduler scheduler, RejectedClosureHandling rejectedClosureHandling)
    {
        if (maximumPendingClosures is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPendingClosures), maximumPendingClosures, "The limit must be positive, or null for no limit.");
        }
        ArgumentNullException.ThrowIfNull(scheduler);
        if (!Enum.IsDefined(rejectedClosureHandling))
        {
            throw new ArgumentOutOfRangeException(nameof(rejectedClosureHandling), rejectedClosureHandling, "Not a defined rejected-closure handling.");
        }
        _engineThreadId = engineThreadId;
        _maximumPendingClosures = maximumPendingClosures;
        _scheduler = scheduler;
        _rejectedClosureHandling = rejectedClosureHandling;
    }

    public uint ObjectSelf => _objectSelf;

    public int PendingCount { get { lock (_gate) return _closures.Count; } }

    /// <summary>How many closures may wait for their callback at once; null when there is no limit.</summary>
    public int? MaximumPendingClosures => _maximumPendingClosures;

    /// <summary>The generation that owns closures scheduled now; <see cref="HostGeneration"/> before any generation began.</summary>
    public long SchedulingGeneration { get { lock (_gate) return _schedulingGeneration; } }

    /// <summary>
    /// How many closures were dropped without running since the handler was created: those pending when their
    /// generation retired, and those a retired generation scheduled afterwards.
    /// </summary>
    public long DroppedClosures { get { lock (_gate) return _droppedClosures; } }

    /// <summary>What scheduling a closure the engine refuses does: throw (the default) or return normally.</summary>
    public RejectedClosureHandling RejectedClosureHandling => _rejectedClosureHandling;

    /// <summary>
    /// How many closures the engine refused since the handler was created, the host's own included. None of them ran or
    /// is still held; under <see cref="Bootstrap.RejectedClosureHandling.Throw"/> each scheduling call also threw.
    /// </summary>
    public long RejectedClosureCount { get { lock (_gate) return _rejectedClosures; } }

    public void ClosureActionDoCommand(uint obj, Action action) =>
        Schedule(obj, action, ClosureKind.ActionDoCommand, 0, hostOwned: false);

    public void ClosureAssignCommand(uint obj, Action action) =>
        Schedule(obj, action, ClosureKind.AssignCommand, 0, hostOwned: false);

    public void ClosureDelayCommand(uint obj, float duration, Action action)
    {
        if (!float.IsFinite(duration) || duration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        Schedule(obj, action, ClosureKind.DelayCommand, duration, hostOwned: false);
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
        Action action;
        lock (_gate)
        {
            if (_disposed || !_closures.Remove(eventId, out var pending))
            {
                // The callback of a closure dropped with its retired generation is expected; it was counted when dropped.
                if (!_disposed && _dropped.Remove(eventId))
                {
                    return;
                }
                NativeHost.Log("closure", $"ignored stale event={eventId} object=0x{objectSelf:X8}");
                return;
            }
            action = pending.Action;
        }
        using var scope = EnterObjectSelf(objectSelf);
        action();
    }

    /// <summary>
    /// Makes <paramref name="generation"/> the owner of every closure scheduled from now on, until another generation
    /// begins. <see cref="Reload.ReloadHost"/> calls it on the engine thread right before it activates a generation.
    /// </summary>
    public void BeginGeneration(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        AssertEngineThread();
        lock (_gate)
        {
            _schedulingGeneration = generation;
            _schedulingGenerationRetired = false;
        }
    }

    /// <summary>
    /// Drops every pending closure of <paramref name="generation"/> without running it and returns how many were
    /// dropped; the engine's later callback for each is ignored. Closures of other generations and of the host stay
    /// pending. When <paramref name="generation"/> is the <see cref="SchedulingGeneration"/>, closures scheduled after
    /// this call are dropped too, until <see cref="BeginGeneration"/> names the next generation: only the retired
    /// generation's leftover code schedules them. <see cref="Reload.ReloadHost"/> calls it on the engine thread when it
    /// disposes a generation.
    /// </summary>
    public int RetireGeneration(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        AssertEngineThread();
        lock (_gate)
        {
            if (_schedulingGeneration == generation)
            {
                _schedulingGenerationRetired = true;
            }
            if (_disposed)
            {
                return 0;
            }
            var retired = new List<ulong>();
            foreach (var (eventId, pending) in _closures)
            {
                if (pending.Generation == generation)
                {
                    retired.Add(eventId);
                }
            }
            foreach (var eventId in retired)
            {
                _closures.Remove(eventId);
                _dropped.Add(eventId);
            }
            _droppedClosures += retired.Count;
            return retired.Count;
        }
    }

    public void Dispose()
    {
        AssertEngineThread();
        lock (_gate)
        {
            _disposed = true;
            _closures.Clear();
            _dropped.Clear();
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
            _dropped.Clear();
            _disposed = true;
            return cancelled;
        }
    }

    /// <summary>Schedules a closure that belongs to the host whichever generation is active, such as the frame closure.</summary>
    internal void HostAssignCommand(uint obj, Action action) =>
        Schedule(obj, action, ClosureKind.AssignCommand, 0, hostOwned: true);

    private void Schedule(uint obj, Action action, ClosureKind kind, float duration, bool hostOwned)
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
            var generation = hostOwned ? HostGeneration : _schedulingGeneration;
            if (generation != HostGeneration && _schedulingGenerationRetired)
            {
                // Leftover code of a retired generation: the closure would run retired code, so it is never scheduled.
                _droppedClosures++;
                return;
            }
            if (_maximumPendingClosures is { } maximum && _closures.Count >= maximum)
            {
                throw new InvalidOperationException($"NWN closure capacity ({maximum}) was exhausted.");
            }
            eventId = _nextEventId++;
            _closures.Add(eventId, new PendingClosure(action, generation));
        }
        var scheduled = kind switch
        {
            ClosureKind.ActionDoCommand => _scheduler.ActionDoCommand(obj, eventId),
            ClosureKind.AssignCommand => _scheduler.AssignCommand(obj, eventId),
            _ => _scheduler.DelayCommand(obj, duration, eventId),
        };
        if (scheduled != 0)
        {
            return;
        }
        lock (_gate)
        {
            // The engine never calls back for a refused closure, so nothing keeps its event id or its delegate.
            _closures.Remove(eventId);
            _rejectedClosures++;
        }
        // The host's own closures (the frame closure) always throw: dropping one silently would stop every later frame.
        if (_rejectedClosureHandling == RejectedClosureHandling.Drop && !hostOwned)
        {
            return;
        }
        throw new InvalidOperationException($"NWNX rejected {kind} closure event {eventId} for object 0x{obj:X8}.");
    }

    /// <summary>
    /// Makes <paramref name="objectSelf"/> the current <c>OBJECT_SELF</c> and returns the previous one, without the
    /// allocation of <see cref="EnterObjectSelf"/>; the caller restores the previous value the same way.
    /// </summary>
    internal uint ExchangeObjectSelf(uint objectSelf)
    {
        AssertEngineThread();
        var previous = _objectSelf;
        _objectSelf = objectSelf;
        return previous;
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

    private enum ClosureKind
    {
        ActionDoCommand,
        AssignCommand,
        DelayCommand,
    }

    private readonly record struct PendingClosure(Action Action, long Generation);

    private sealed class ObjectSelfScope(CoreFunctionHandler owner, uint previous) : IDisposable
    {
        private CoreFunctionHandler? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RestoreObjectSelf(previous);
    }
}
