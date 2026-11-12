using NWN.Core;
using Nwn.Hosting.Bootstrap;
using Nwn.Hosting.Reload;
using Nwn.Hosting.Reload.ScriptGameplay;
using Nwn.Hosting.Testing.Fixtures;
using Nwn.Hosting.Tests.Support;
using static Nwn.Hosting.Testing.Support.TestAssertions;

namespace Nwn.Hosting.Tests.Reload;

/// <summary>
/// A <see cref="ReloadHost"/> given the host's <see cref="CoreFunctionHandler"/> drops a retired generation's pending
/// closures. <see cref="ScriptGameplayGeneration"/> schedules a <c>DelayCommand</c> closure whenever its
/// <c>nh_schedule_closure</c> script runs; the test plays the engine, delivering every callback on the fixture's engine
/// thread through a <see cref="RecordingClosureScheduler"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ReloadHostClosureTests
{
    private const uint ObjectSelf = 0x02000000;

    private TextWriter? _originalOut;
    private StringWriter _console = new();

    [TestInitialize]
    public void RedirectConsole()
    {
        _originalOut = Console.Out;
        _console = new StringWriter();
        Console.SetOut(_console);
    }

    [TestCleanup]
    public void RestoreConsole() => Console.SetOut(_originalOut!);

    [TestMethod]
    public async Task ARetiredGenerationsClosuresNeverRun_TheirCallbacksAreIgnored_AndItsContextUnloads()
    {
        await using var native = new FixtureNativeBindings();
        var scheduler = new RecordingClosureScheduler();
        var closures = new CoreFunctionHandler(native.EngineThreadId, null, scheduler);
        await using var host = new ReloadHost(native, HostServicesFor(closures), null, closures);
        var first = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(first.Outcome == ReloadOutcome.Applied, first.Detail);
        await ScheduleClosuresAsync(native, host, 3);
        var retiredEvents = scheduler.Scheduled.Select(closure => closure.EventId).ToArray();

        var second = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(second.Outcome == ReloadOutcome.Applied, second.Detail);
        await ScheduleClosuresAsync(native, host, 3);
        var activeEvents = scheduler.Scheduled.Skip(3).Select(closure => closure.EventId).ToArray();
        await OnEngineAsync(native, () =>
        {
            // The engine still calls back for every closure it was given, the dropped ones first.
            foreach (var eventId in retiredEvents.Concat(activeEvents))
            {
                closures.Dispatch(eventId, ObjectSelf);
            }
        });

        var activeGeneration = second.ActiveGeneration!.Value;
        CollectionAssert.AreEqual(
            new[] { $"{activeGeneration}:1", $"{activeGeneration}:2", $"{activeGeneration}:3" },
            native.Messages(ScriptGameplayGeneration.ClosureRanCategory).ToArray(),
            "Only the active generation's closures may run, in the order the engine calls them back.");
        CollectionAssert.AreEqual(new[] { $"generation={first.ActiveGeneration} dropped=3" }, native.Messages("closures-dropped").ToArray());
        Require(closures.PendingCount == 0 && closures.DroppedClosures == 3, $"pending={closures.PendingCount} dropped={closures.DroppedClosures}");
        Require(!_console.ToString().Contains("ignored stale event", StringComparison.Ordinal),
            "The callbacks of dropped closures must be ignored quietly.");

        await Task.Delay(40);
        var probe = await host.ProbeRetiredContextsAsync();
        RequireNoRetainedContexts(probe, "A retired generation with pending closures");
        Require(probe.ProbedContexts == 1 && probe.CollectedContexts == 1, $"probed={probe.ProbedContexts} collected={probe.CollectedContexts}");
    }

    [TestMethod]
    public async Task WithoutTheClosureHandler_APendingClosureKeepsItsRetiredGenerationLoaded()
    {
        // The control for the test above: the same closures, but the host is not told about the handler.
        await using var native = new FixtureNativeBindings();
        var closures = new CoreFunctionHandler(native.EngineThreadId, null, new RecordingClosureScheduler());
        await using var host = new ReloadHost(native, HostServicesFor(closures));
        Require((await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout)).Outcome == ReloadOutcome.Applied, "The first generation did not start.");
        await ScheduleClosuresAsync(native, host, 1);
        Require((await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout)).Outcome == ReloadOutcome.Applied, "The second generation did not start.");
        await OnEngineAsync(native, () => { });

        await Task.Delay(40);
        var retained = await host.ProbeRetiredContextsAsync();
        await OnEngineAsync(native, () => closures.CloseAndCancel());
        await Task.Delay(40);
        var released = await host.ProbeRetiredContextsAsync();

        Require(retained.RetainedContexts == 1, "A pending closure of the retired generation must root its context.");
        Require(released.RetainedContexts == 0, "Dropping the closure must release the retired context.");
    }

    [TestMethod]
    public async Task ClosuresScheduledBeforeAnyGeneration_BelongToTheHostAndSurviveReloads()
    {
        await using var native = new FixtureNativeBindings();
        var scheduler = new RecordingClosureScheduler();
        var closures = new CoreFunctionHandler(native.EngineThreadId, null, scheduler);
        await using var host = new ReloadHost(native, HostServicesFor(closures), null, closures);
        var ran = 0;
        await OnEngineAsync(native, () => closures.ClosureDelayCommand(ObjectSelf, 6.0f, () => ran++));

        Require((await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout)).Outcome == ReloadOutcome.Applied, "The first generation did not start.");
        Require((await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout)).Outcome == ReloadOutcome.Applied, "The second generation did not start.");
        await OnEngineAsync(native, () => closures.Dispatch(scheduler.Scheduled.Single().EventId, ObjectSelf));

        Require(ran == 1, "A closure the host scheduled before any generation must run.");
        Require(closures.DroppedClosures == 0, "Nothing belonged to a retired generation.");
    }

    [TestMethod]
    public async Task DisposingTheHost_DropsTheActiveGenerationsClosures()
    {
        await using var native = new FixtureNativeBindings();
        var closures = new CoreFunctionHandler(native.EngineThreadId, null, new RecordingClosureScheduler());
        var host = new ReloadHost(native, HostServicesFor(closures), null, closures);
        var report = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(report.Outcome == ReloadOutcome.Applied, report.Detail);
        await ScheduleClosuresAsync(native, host, 2);

        await host.DisposeAsync();

        Require(closures.PendingCount == 0 && closures.DroppedClosures == 2, $"pending={closures.PendingCount} dropped={closures.DroppedClosures}");
        CollectionAssert.AreEqual(new[] { $"generation={report.ActiveGeneration} dropped=2" }, native.Messages("closures-dropped").ToArray());
    }

    [TestMethod]
    public async Task UnderDrop_ClosuresTheEngineRefuses_AreNotKeptRunOrThrown_AndTheirGenerationUnloads()
    {
        await using var native = new FixtureNativeBindings();
        var scheduler = new RecordingClosureScheduler { Refuse = true };
        var closures = new CoreFunctionHandler(native.EngineThreadId, null, scheduler, RejectedClosureHandling.Drop);
        await using var host = new ReloadHost(native, HostServicesFor(closures), null, closures);
        var first = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(first.Outcome == ReloadOutcome.Applied, first.Detail);

        // Each scheduling script must still count as handled: under Drop the refusal does not throw into gameplay code.
        await ScheduleClosuresAsync(native, host, 3);
        var second = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(second.Outcome == ReloadOutcome.Applied, second.Detail);

        Require(scheduler.Scheduled.Count == 0, "No refused closure may reach the engine's queue.");
        Require(closures.PendingCount == 0 && closures.RejectedClosureCount == 3 && closures.DroppedClosures == 0,
            $"pending={closures.PendingCount} rejected={closures.RejectedClosureCount} dropped={closures.DroppedClosures}");
        Require(native.Count(ScriptGameplayGeneration.ClosureRanCategory) == 0, "A refused closure must never run.");
        Require(native.Count("script-dispatch-failure") == 0, "A refused closure must not fail the script that scheduled it.");
        Require(native.Count("closures-dropped") == 0, "Nothing of the retired generation was still pending.");

        await Task.Delay(40);
        var probe = await host.ProbeRetiredContextsAsync();
        RequireNoRetainedContexts(probe, "A retired generation whose closures the engine refused");
        Require(probe.ProbedContexts == 1 && probe.CollectedContexts == 1, $"probed={probe.ProbedContexts} collected={probe.CollectedContexts}");
    }

    [TestMethod]
    public async Task UnderThrow_AClosureTheEngineRefuses_StillFailsTheScriptThatScheduledIt()
    {
        await using var native = new FixtureNativeBindings();
        var scheduler = new RecordingClosureScheduler { Refuse = true };
        var closures = new CoreFunctionHandler(native.EngineThreadId, null, scheduler);
        await using var host = new ReloadHost(native, HostServicesFor(closures), null, closures);
        var report = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(report.Outcome == ReloadOutcome.Applied, report.Detail);

        var handled = true;
        await OnEngineAsync(native, () => handled = host.TryDispatchScript(ScriptGameplayGeneration.ScheduleClosureScriptName, ObjectSelf));

        Require(!handled, "Under Throw the refusal throws into the scheduling script, which the host reports as not handled.");
        Require(native.Count("script-dispatch-failure") == 1, "The refusal must be reported as a dispatch failure.");
        Require(closures.PendingCount == 0 && closures.RejectedClosureCount == 1, $"pending={closures.PendingCount} rejected={closures.RejectedClosureCount}");
    }

    private static TestHostServices HostServicesFor(CoreFunctionHandler closures) =>
        new TestHostServices().Add(typeof(ICoreFunctionHandler), closures);

    private static ValueTask ScheduleClosuresAsync(FixtureNativeBindings native, ReloadHost host, int count) =>
        OnEngineAsync(native, () =>
        {
            for (var index = 0; index < count; index++)
            {
                Require(host.TryDispatchScript(ScriptGameplayGeneration.ScheduleClosureScriptName, ObjectSelf),
                    "The active generation did not handle the scheduling script.");
            }
        });

    private static ValueTask OnEngineAsync(FixtureNativeBindings native, Action action) =>
        native.InvokeOnEngineThreadAsync(() =>
        {
            action();
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

    private static string ScriptGameplayAssemblyPath() =>
        Path.Combine(AppContext.BaseDirectory, "Nwn.Hosting.Reload.ScriptGameplay.dll");
}
