using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nwn.Hosting.Bootstrap;
using Nwn.Hosting.Contracts.Generations;

namespace Nwn.Hosting.Reload;

/// <summary>
/// Permanent, single-thread-affine coordinator. It deliberately has no DI container or serializer:
/// those are common unintended roots for collectible gameplay assemblies.
/// </summary>
public sealed class ReloadHost : IGenerationLiveness, IAsyncDisposable
{
    private readonly IPermanentNativeBindings _native;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _retiredGate = new();
    private readonly List<RetiredGenerationContext> _retiredContexts = [];
    private LoadedGeneration? _active;
    private long _nextGeneration;
    private bool _restartRequired;
    private bool _singleBuildLoaded;

    private readonly IHostServices? _hostServices;
    private readonly GenerationLoadOptions? _loadOptions;
    private readonly CoreFunctionHandler? _closures;

    public ReloadHost(IPermanentNativeBindings native, IHostServices? hostServices = null, GenerationLoadOptions? loadOptions = null)
        : this(native, hostServices, loadOptions, closures: null)
    {
    }

    /// <param name="native">The engine-thread seam.</param>
    /// <param name="hostServices">Process-lifetime singletons handed to every generation.</param>
    /// <param name="loadOptions">How each generation loads; <see cref="GenerationLoadOptions.Default"/> when null.</param>
    /// <param name="closures">
    /// The host's closure handler (the one <see cref="NativeHost"/> hands the mode). When given, every closure scheduled
    /// while a generation is active belongs to that generation, and the generation's pending closures are dropped
    /// without running when it is disposed (<see cref="CoreFunctionHandler.RetireGeneration"/>), so a retired
    /// generation's <c>DelayCommand</c>, <c>AssignCommand</c> or <c>ActionDoCommand</c> neither runs its code after
    /// the dispose nor keeps its context loaded. Null keeps every closure as the host's, as the other constructor does.
    /// </param>
    public ReloadHost(IPermanentNativeBindings native, IHostServices? hostServices, GenerationLoadOptions? loadOptions, CoreFunctionHandler? closures)
    {
        ThreadPoolKeepAlive.Require();
        VirtualDispatchCacheSentinel.Seal();
        _native = native;
        _hostServices = hostServices;
        _loadOptions = loadOptions;
        _closures = closures;
    }

    public long Generation => Volatile.Read(ref _nextGeneration);

    public bool IsCurrent(long generation) => !_restartRequired && _active?.Generation == generation;

    /// <summary>
    /// Forwards one engine RunScript call to whichever generation is active at this instant.
    /// Reads <see cref="_active"/> the same unsynchronized way <see cref="IsCurrent"/> already
    /// does: every real caller only ever calls this from the single engine thread that also drives
    /// activation/retirement, so a swap and a dispatch never race in practice. Returns false ("not
    /// handled") when there is no active generation, it does not expose <see cref="IScriptDispatcher"/>,
    /// or it does not recognize the name -- the caller then falls back to the module's compiled
    /// script. Never throws: a dispatch failure is reported through the native bindings and treated
    /// as unhandled, so a bug in one generation's gameplay code cannot break every other script.
    /// </summary>
    public bool TryDispatchScript(string scriptName, uint objectSelf) => TryDispatchScript(scriptName, objectSelf, out _);

    /// <summary>Same as <see cref="TryDispatchScript(string, uint)"/>, also surfacing a conditional (StartingConditional-style) handler's result.</summary>
    public bool TryDispatchScript(string scriptName, uint objectSelf, out bool conditionalResult)
    {
        conditionalResult = false;
        var dispatcher = _active?.Gameplay.Scripts;
        if (dispatcher is null) return false;
        try
        {
            return dispatcher.TryHandle(scriptName, objectSelf, out conditionalResult);
        }
        catch (Exception exception)
        {
            // The full ToString() (not just type+message) is deliberate: this is the only place a
            // dispatch failure is ever recorded, and a bare exception message with no stack trace
            // does not say where it happened.
            _native.Report("script-dispatch-failure", $"{scriptName}: {exception}");
            return false;
        }
    }

    /// <summary>
    /// The run-script result of the active generation for <paramref name="scriptName"/>, for
    /// <see cref="IHostMode.OnRunScriptResult"/>: <see cref="NativeHost.ScriptNotHandled"/> when
    /// <see cref="TryDispatchScript(string, uint, out bool)"/> did not handle it, <see cref="NativeHost.ScriptConditionTrue"/>
    /// when it handled it with a true conditional result, and <see cref="NativeHost.ScriptHandled"/> otherwise. Same
    /// thread and failure rules as <see cref="TryDispatchScript(string, uint)"/>.
    /// </summary>
    public int DispatchScriptResult(string scriptName, uint objectSelf)
    {
        if (!TryDispatchScript(scriptName, objectSelf, out var conditionalResult))
        {
            return NativeHost.ScriptNotHandled;
        }
        return conditionalResult ? NativeHost.ScriptConditionTrue : NativeHost.ScriptHandled;
    }

    /// <summary>
    /// Forwards one native main-loop tick to the active generation's <see cref="IMainLoopSink"/>,
    /// if it exposes one. See <see cref="TryDispatchScript(string, uint)"/> for the
    /// identical thread and failure-isolation rules.
    /// </summary>
    public void PumpMainLoop()
    {
        var sink = _active?.Gameplay.MainLoop;
        if (sink is null) return;
        try
        {
            sink.Tick();
        }
        catch (Exception exception)
        {
            // Full ToString() (with stack trace), not just type+message, for the same reason as in
            // TryDispatchScript.
            _native.Report("main-loop-dispatch-failure", exception.ToString());
        }
    }

    /// <summary>
    /// Loads the gameplay assembly at <paramref name="assemblyPath"/> into a new collectible context, validates it, and
    /// replaces the running generation with it; the report says what happened and whether the old context was collected.
    /// </summary>
    /// <param name="assemblyPath">The candidate gameplay assembly.</param>
    /// <param name="drainTimeout">How long the running generation may take to drain its callbacks and owned work.</param>
    /// <param name="verifyRequiredSchema">
    /// The reload gate. When given, it is called
    /// with the freshly loaded candidate's <see cref="IGameplayGeneration.RequiredSchemaVersion"/>
    /// right after validation succeeds. A candidate that reports incompatible is rejected -- exactly
    /// like a validation failure, with the candidate unloaded and probed -- ONLY when a previous
    /// generation is running to protect; the very first activation of a process (nothing running yet
    /// to fall back to) always proceeds so the host can at least come up and refuse players by itself.
    /// This method calls the callback in both cases
    /// (compatible or not) either way; what the callback itself does with an incompatible answer is
    /// its own decision -- a host that gates player admission on the schema should only move its live
    /// admission gate up to a REJECTED candidate's requirement on the very first activation (nothing
    /// else is running for admission to track), never on a later rejected reload, where the OLD
    /// generation is what is actually still serving players.
    /// </param>
    /// <param name="cancellationToken">Cancels the reload before the running generation is touched or while it drains.</param>
    public async ValueTask<ReloadReport> ReloadAsync(
        string assemblyPath,
        TimeSpan drainTimeout,
        Func<int, CancellationToken, ValueTask<ReloadSchemaGateResult>>? verifyRequiredSchema = null,
        CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_restartRequired)
            {
                return Rejected("The previous post-teardown failure requires supervisor restart.");
            }
            if (_singleBuildLoaded)
            {
                return Rejected("This host loads one non-collectible build for the life of the process (GenerationLoadOptions.Collectible is false); restart the server to load another.");
            }

            var validationWatch = Stopwatch.StartNew();
            LoadedGeneration? candidate = null;
            try
            {
                // A non-collectible build can never be unloaded, so this host never loads a second one, even when this
                // one fails.
                _singleBuildLoaded = _loadOptions is { Collectible: false };
                candidate = LoadedGeneration.Load(assemblyPath, Interlocked.Increment(ref _nextGeneration), _loadOptions);
                await candidate.Gameplay.ValidateAsync(cancellationToken).ConfigureAwait(false);
                validationWatch.Stop();
            }
            catch (Exception exception)
            {
                validationWatch.Stop();
                var collected = candidate is not null && await TryUnloadAndProbeAsync(candidate).ConfigureAwait(false);
                return new ReloadReport(
                    ReloadOutcome.RejectedBeforeTeardown,
                    _active?.Generation,
                    _active?.Generation,
                    validationWatch.Elapsed,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    collected,
                    $"Candidate validation failed; the running generation was left active: {exception}");
            }

            if (verifyRequiredSchema is not null)
            {
                var candidateRequiredSchemaVersion = candidate.Gameplay.RequiredSchemaVersion;
                var schemaGate = await verifyRequiredSchema(candidateRequiredSchemaVersion, cancellationToken).ConfigureAwait(false);
                if (!schemaGate.Compatible && _active is not null)
                {
                    // Capture everything from the candidate and _active BEFORE unloading: TryUnloadAndProbeAsync
                    // disposes the candidate, and _active could in principle change once this method
                    // gives up the engine thread again.
                    var activeGeneration = _active.Generation;
                    var collected = await TryUnloadAndProbeAsync(candidate).ConfigureAwait(false);
                    return new ReloadReport(
                        ReloadOutcome.RejectedBeforeTeardown,
                        activeGeneration,
                        activeGeneration,
                        validationWatch.Elapsed,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        collected,
                        $"Schema gate rejected the candidate (requires schema version {candidateRequiredSchemaVersion}); the running generation was left active: {schemaGate.Detail}");
                }
            }

            var previous = _active;
            var previousGeneration = previous?.Generation;
            var drainWatch = Stopwatch.StartNew();
            if (previous is not null)
            {
                try
                {
                    previous.Execution.CloseAdmission();
                    await _native.InvokeOnEngineThreadAsync(
                        () => previous.Gameplay.BeginQuiescenceAsync(cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    await previous.Execution.DrainAsync(drainTimeout, cancellationToken).ConfigureAwait(false);
                    await previous.Gameplay.DrainAsync(drainTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    drainWatch.Stop();
                    var candidateCollected = await TryUnloadAndProbeAsync(candidate).ConfigureAwait(false);
                    // Admission has already been closed and callbacks have been removed. The old
                    // generation cannot be honestly resumed, even if a deadline expired.
                    _active = null;
                    _restartRequired = true;
                    return new ReloadReport(
                        ReloadOutcome.RestartRequired,
                        previousGeneration,
                        null,
                        validationWatch.Elapsed,
                        drainWatch.Elapsed,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        candidateCollected,
                        $"Drain failed after quiescence began; supervisor restart is required: {exception}");
                }
            }

            drainWatch.Stop();
            var applyWatch = Stopwatch.StartNew();
            WeakReference? previousContext = null;
            try
            {
                if (previous is not null)
                {
                    _active = null;
                    previousContext = await DisposeWithoutRetainingAsync(previous, _native, _closures).ConfigureAwait(false);
                    previous = null;
                }

                await _native.InvokeOnEngineThreadAsync(
                    () =>
                    {
                        // Closures the candidate schedules from its activation on are its own.
                        _closures?.BeginGeneration(candidate.Generation);
                        return candidate.Gameplay.ActivateAsync(
                            new GenerationActivation(candidate.Generation, _native, this, candidate.Execution, _hostServices), cancellationToken);
                    },
                    cancellationToken).ConfigureAwait(false);
                _active = candidate;
                applyWatch.Stop();
                if (previousContext is not null)
                {
                    lock (_retiredGate)
                    {
                        _retiredContexts.Add(new RetiredGenerationContext(previousGeneration!.Value, previousContext));
                    }
                }

                return new ReloadReport(
                    ReloadOutcome.Applied,
                    previousGeneration,
                    candidate.Generation,
                    validationWatch.Elapsed,
                    drainWatch.Elapsed,
                    applyWatch.Elapsed,
                    TimeSpan.Zero,
                    previousContext is null,
                    "Candidate activated. The old context is probed after this reload returns, separately from reload pause.");
            }
            catch (Exception exception)
            {
                applyWatch.Stop();
                // The full ToString() for the same reason TryDispatchScript gives: a failure here forces a
                // restart, and its type and message alone do not say where it happened.
                _native.Report("activation-failure", exception.ToString());
                _active = null;
                _restartRequired = true;
                var candidateCollectible = candidate.IsCollectible;
                var candidateContext = await TryDisposeAndUnloadAsync(candidate).ConfigureAwait(false);
                var unloadWatch = Stopwatch.StartNew();
                var collected = candidateCollectible && await LoadedGeneration.ProbeCollectedAsync(candidateContext).ConfigureAwait(false);
                unloadWatch.Stop();
                return new ReloadReport(
                    ReloadOutcome.RestartRequired,
                    previousGeneration,
                    null,
                    validationWatch.Elapsed,
                    drainWatch.Elapsed,
                    applyWatch.Elapsed,
                    unloadWatch.Elapsed,
                    collected,
                    $"Failure after teardown; do not revive the old generation: {exception}");
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_active is not null)
            {
                _active.Execution.CloseAdmission();
                await _native.InvokeOnEngineThreadAsync(
                    () => _active.Gameplay.BeginQuiescenceAsync(CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
                await _active.Execution.DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                await _active.Gameplay.DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                var collectible = _active.IsCollectible;
                var context = await _active.DisposeAndUnloadAsync(_native, _closures).ConfigureAwait(false);
                _active = null;
                if (collectible)
                {
                    _ = await LoadedGeneration.ProbeCollectedAsync(context).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _serial.Release();
            _serial.Dispose();
        }
    }

    /// <summary>
    /// Call from a later server-loop opportunity or the test harness. Running this probe inside the
    /// reload state machine would retain its own locals and produce a false unload failure.
    /// </summary>
    public async ValueTask<RetiredContextProbe> ProbeRetiredContextsAsync(Action<string>? diagnostic = null)
    {
        RetiredGenerationContext[] candidates;
        lock (_retiredGate)
        {
            candidates = _retiredContexts.ToArray();
        }

        var watch = Stopwatch.StartNew();
        var collected = 0;
        var samples = new List<RetiredContextSample>(candidates.Length);
        foreach (var candidate in candidates)
        {
            diagnostic?.Invoke("collectible-context probe begin");
            var sampleWatch = Stopwatch.StartNew();
            var wasCollected = await LoadedGeneration.ProbeCollectedAsync(candidate.Context, diagnostic).ConfigureAwait(false);
            sampleWatch.Stop();
            if (wasCollected)
            {
                collected++;
            }
            samples.Add(new RetiredContextSample(candidate.Generation, wasCollected, sampleWatch.Elapsed));
            diagnostic?.Invoke($"collectible-context probe complete generation={candidate.Generation} alive={candidate.Context.IsAlive}");
        }

        lock (_retiredGate)
        {
            _retiredContexts.RemoveAll(context => !context.Context.IsAlive);
            var retained = _retiredContexts.Count;
            watch.Stop();
            return new RetiredContextProbe(candidates.Length, collected, retained, watch.Elapsed, samples);
        }
    }

    private ReloadReport Rejected(string detail) => new(
        ReloadOutcome.RejectedBeforeTeardown,
        _active?.Generation,
        _active?.Generation,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        true,
        detail);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async ValueTask<WeakReference> DisposeWithoutRetainingAsync(
        LoadedGeneration generation, IPermanentNativeBindings native, CoreFunctionHandler? closures)
    {
        return await generation.DisposeAndUnloadAsync(native, closures).ConfigureAwait(false);
    }

    private async ValueTask<bool> TryUnloadAndProbeAsync(LoadedGeneration generation)
    {
        try
        {
            var collectible = generation.IsCollectible;
            var context = await generation.DisposeAndUnloadAsync(_native, _closures).ConfigureAwait(false);
            // A non-collectible context is never collected; probing it would only spend ten blocking collections.
            return collectible && await LoadedGeneration.ProbeCollectedAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _native.Report("reload-cleanup-failure", exception.ToString());
            return false;
        }
    }

    private async ValueTask<WeakReference> TryDisposeAndUnloadAsync(LoadedGeneration generation)
    {
        try
        {
            return await generation.DisposeAndUnloadAsync(_native, _closures).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _native.Report("reload-cleanup-failure", exception.ToString());
            // A weak reference to a non-collectible sentinel preserves the structured failure result.
            return new WeakReference(_native, trackResurrection: false);
        }
    }

    private sealed record RetiredGenerationContext(long Generation, WeakReference Context);
}
