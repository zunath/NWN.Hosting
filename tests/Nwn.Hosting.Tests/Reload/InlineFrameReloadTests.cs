using System.Collections.Concurrent;
using System.Diagnostics;
using NWN.Core;
using Nwn.Hosting.Bootstrap;
using Nwn.Hosting.Contracts.Generations;
using Nwn.Hosting.Reload;
using Nwn.Hosting.Reload.ScriptGameplay;
using Nwn.Hosting.Testing.Fixtures;
using Nwn.Hosting.Tests.Support;
using static Nwn.Hosting.Testing.Support.TestAssertions;

namespace Nwn.Hosting.Tests.Reload;

/// <summary>
/// A reload while <see cref="NativeHost"/> delivers frames inline (<see cref="FrameDelivery.Inline"/>). The test thread
/// is the engine thread and plays NWNX's main loop, calling the host's main-loop handler once per frame; each reload runs
/// on another thread and reaches the engine thread only through <see cref="NativeBindings"/>, which the mode pumps inside
/// its frame before it ticks the active generation, as a server's mode does.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class InlineFrameReloadTests
{
    private const string LogPrefix = "TESTHOST_NATIVE";
    private const uint ObjectSelf = 0x02000000;
    private const string FrameBegin = "frame-begin";
    private const string FrameEnd = "frame-end";
    private const string Tick = "tick";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<string> _events = new();
    private TextWriter? _originalOut;
    private ulong _nextFrame;

    [TestInitialize]
    public void RedirectConsole()
    {
        NativeHost.ResetForTest();
        _originalOut = Console.Out;
        Console.SetOut(new StringWriter());
    }

    [TestCleanup]
    public void RestoreConsole()
    {
        Console.SetOut(_originalOut!);
        NativeHost.ResetForTest();
    }

    [TestMethod]
    public void AReloadSwapsGenerationsOnlyInsideTheFramesEngineWork_AndARetiredGenerationIsNeverTickedAgain()
    {
        var bindings = new NativeBindings(Environment.CurrentManagedThreadId);
        var native = new RecordingBindings(bindings, _events);
        var scheduler = new RecordingClosureScheduler();
        var mode = new PumpingHostMode(bindings, _events);
        CoreFunctionHandler? core = null;
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "inline", created =>
        {
            core = created;
            return mode;
        }) { FrameDelivery = FrameDelivery.Inline }, _ => { }, scheduler);
        var host = new ReloadHost(native, new TestHostServices().Add(typeof(ICoreFunctionHandler), core!), null, core);
        mode.Host = host;
        try
        {
            // Frames arrive before the module has loaded and before any generation is active.
            RunFrames(2);
            var first = RunFramesUntil(() => host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout).AsTask());
            Require(first.Outcome == ReloadOutcome.Applied, first.Detail);
            RunFrames(3);
            // The retired generation leaves one closure pending; it is dropped with it, and no frame closure exists.
            Require(host.TryDispatchScript(ScriptGameplayGeneration.ScheduleClosureScriptName, ObjectSelf), "The first generation did not schedule.");
            var second = RunFramesUntil(() => host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout).AsTask());
            Require(second.Outcome == ReloadOutcome.Applied, second.Detail);
            RunFrames(3);

            var retired = first.ActiveGeneration!.Value;
            var active = second.ActiveGeneration!.Value;
            var events = _events.ToArray();
            Require(!events.Any(entry => entry.StartsWith("failure", StringComparison.Ordinal)), string.Join(Environment.NewLine, events));
            var frames = SplitIntoFrames(events);
            var disposed = Array.IndexOf(events, $"{ScriptGameplayGeneration.DisposedCategory}:{retired}");
            var activated = Array.IndexOf(events, $"{ScriptGameplayGeneration.ActivatedCategory}:{active}");

            Require(disposed >= 0 && activated > disposed, "The retired generation must be disposed before its successor activates.");
            Require(events[disposed + 1] == $"closures-dropped:generation={retired} dropped=1",
                "The retired generation's pending closure must be dropped in the same engine call as its dispose.");
            Require(!events.Skip(disposed).Contains($"{Tick}:{retired}"), "A retired generation's frame sink must never run after its dispose.");
            Require(!events.Take(activated).Contains($"{Tick}:{active}"), "A generation's frame sink must not run before it activates.");
            Require(events.Contains($"{Tick}:{retired}") && events.Contains($"{Tick}:{active}"), "Both generations must have ticked while active.");
            Require(frames.All(frame => frame.Count(entry => entry.StartsWith($"{Tick}:", StringComparison.Ordinal)) <= 1),
                "A frame ticks at most one generation.");
            Require(frames.Sum(frame => frame.Count) == events.Count(entry => !entry.StartsWith("frame-", StringComparison.Ordinal)),
                "Every activation, dispose, drop and tick must happen inside a frame, on the engine thread.");
            Require(scheduler.Scheduled.Count == 1, "Inline frames schedule no closure; only the generation's own closure reached the engine.");
            Require(core!.PendingCount == 0 && core.DroppedClosures == 1, $"pending={core.PendingCount} dropped={core.DroppedClosures}");

            Thread.Sleep(40);
            var probe = host.ProbeRetiredContextsAsync().AsTask().GetAwaiter().GetResult();
            RequireNoRetainedContexts(probe, "A generation retired under inline frames");
            Require(probe.ProbedContexts == 1 && probe.CollectedContexts == 1, $"probed={probe.ProbedContexts} collected={probe.CollectedContexts}");
        }
        finally
        {
            RunFramesUntil(async () =>
            {
                await host.DisposeAsync();
                return true;
            });
        }
    }

    // The frames' contents, without their begin and end markers; fails unless every frame number arrived once, in order,
    // with nothing between one frame's end and the next frame's begin.
    private List<List<string>> SplitIntoFrames(string[] events)
    {
        var frames = new List<List<string>>();
        List<string>? current = null;
        foreach (var entry in events)
        {
            if (entry.StartsWith($"{FrameBegin}:", StringComparison.Ordinal))
            {
                Require(current is null && entry == $"{FrameBegin}:{frames.Count}", $"Frame {frames.Count} did not begin in order: {entry}.");
                current = [];
            }
            else if (entry.StartsWith($"{FrameEnd}:", StringComparison.Ordinal))
            {
                Require(current is not null && entry == $"{FrameEnd}:{frames.Count}", $"Frame {frames.Count} did not end in order: {entry}.");
                frames.Add(current!);
                current = null;
            }
            else
            {
                current?.Add(entry);
            }
        }
        Require(current is null && (ulong)frames.Count == _nextFrame, $"Delivered {_nextFrame} frames but the mode ran {frames.Count}.");
        return frames;
    }

    private void RunFrames(int count)
    {
        for (var index = 0; index < count; index++)
        {
            NativeHost.HandleMainLoop(_nextFrame++);
        }
    }

    private T RunFramesUntil<T>(Func<Task<T>> work)
    {
        var running = Task.Run(work);
        var deadline = Stopwatch.StartNew();
        while (!running.IsCompleted)
        {
            Require(deadline.Elapsed < Deadline, "The work did not finish while frames were delivered.");
            NativeHost.HandleMainLoop(_nextFrame++);
            Thread.Sleep(1);
        }
        return running.GetAwaiter().GetResult();
    }

    private static string ScriptGameplayAssemblyPath() =>
        Path.Combine(AppContext.BaseDirectory, "Nwn.Hosting.Reload.ScriptGameplay.dll");

    /// <summary>The engine-thread queue of <see cref="NativeBindings"/>, recording every report in event order.</summary>
    private sealed class RecordingBindings(NativeBindings inner, ConcurrentQueue<string> events) : IPermanentNativeBindings
    {
        public ValueTask InvokeOnEngineThreadAsync(Func<ValueTask> action, CancellationToken cancellationToken) =>
            inner.InvokeOnEngineThreadAsync(action, cancellationToken);

        public void Report(string category, string message) => events.Enqueue($"{category}:{message}");
    }

    /// <summary>A mode whose frame pumps the engine-thread queue, then ticks the active generation.</summary>
    private sealed class PumpingHostMode(NativeBindings bindings, ConcurrentQueue<string> events) : IHostMode
    {
        public ReloadHost? Host { get; set; }

        public void Start()
        {
        }

        public bool BeforeFrame() => true;

        public void RunFrame(ulong frame)
        {
            events.Enqueue($"{FrameBegin}:{frame}");
            bindings.Pump(8);
            Host?.PumpMainLoop();
            events.Enqueue($"{FrameEnd}:{frame}");
        }

        public bool OnRunScript(string script, uint objectSelf) => false;

        public void OnSignal(string signal)
        {
        }

        public void OnDestroyServer()
        {
        }

        public void OnCallbackFailure(string phase, Exception exception) => events.Enqueue($"failure:{phase}:{exception}");

        public void OnFrameScheduleFailure(Exception exception) => events.Enqueue($"failure:schedule:{exception}");
    }
}
