using Nwn.Hosting.Reload;
using Nwn.Hosting.Testing.Fixtures;
using static Nwn.Hosting.Tests.Support.FixtureAssemblies;
using static Nwn.Hosting.Testing.Support.TestAssertions;

namespace Nwn.Hosting.Tests.Reload;

[TestClass]
[TestCategory("Unit")]
public sealed class ReloadHostLifecycleTests
{
    [TestMethod]
    public async Task AlternatesFiftyCollectibleGenerations()
    {
        await using var native = new FixtureNativeBindings();
        await using var host = new ReloadHost(native, loadOptions: LoadOptions);
        var reports = new List<ReloadReport>();
        for (var cycle = 0; cycle < 50; cycle++)
        {
            var report = await host.ReloadAsync(AssemblyFor(cycle % 2 == 0 ? "v1" : "v2"), DrainTimeout);
            reports.Add(report);
            Require(report.Outcome == ReloadOutcome.Applied, report.Detail);
            native.QueueEveryKind();
            await native.ExecutePendingAsync();
        }

        await Task.Delay(40);
        // Probe after the cycle runner has left the reload call sites. A probe performed from the
        // replacement call can retain that call's state-machine locals and is not valid evidence.
        var probe = await host.ProbeRetiredContextsAsync();
        Require(probe.RetainedContexts == 0, $"{probe.RetainedContexts} retired contexts remained rooted after 50 cycles.");
        Require(native.Count("event") == 50 && native.Count("timer") == 50 && native.Count("ui") == 50,
            "Every generation must receive exactly one event, timer, and UI callback.");
        Require(native.Count("async") > 0, "The fixture did not exercise asynchronous completion during the cycle battery.");
        var maxApplyPause = reports.Max(x => x.ApplyPause.TotalMilliseconds);
        Require(maxApplyPause <= 100, $"Managed apply pause exceeded the provisional 100 ms fixture budget: {maxApplyPause:F2} ms.");
        Console.WriteLine(
            $"METRIC cycles=50 applied=50 maxApplyPauseMs={maxApplyPause:F2} " +
            $"unloadProbeMs={probe.Duration.TotalMilliseconds:F2} events={native.Count("event")} timers={native.Count("timer")} " +
            $"ui={native.Count("ui")} async={native.Count("async")} cancelled={native.Count("cancelled")} pendingPlusExecuting={native.CallbackCount}");
    }

    [TestMethod]
    public async Task RejectsInvalidCandidateBeforeTeardown()
    {
        await using var native = new FixtureNativeBindings();
        await using var host = new ReloadHost(native, loadOptions: LoadOptions);
        var first = await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout);
        var rejected = await host.ReloadAsync(Path.Combine(AppContext.BaseDirectory, "games", "missing.dll"), DrainTimeout);
        Require(first.Outcome == ReloadOutcome.Applied, first.Detail);
        Require(rejected.Outcome == ReloadOutcome.RejectedBeforeTeardown, rejected.Detail);
        Require(rejected.ActiveGeneration == first.ActiveGeneration, "Invalid build must leave the old generation active.");
    }

    [TestMethod]
    public async Task RejectsValidationFailureBeforeTeardown()
    {
        await using var native = new FixtureNativeBindings();
        await using var host = new ReloadHost(native, loadOptions: LoadOptions);
        var first = await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout);
        Environment.SetEnvironmentVariable("NWN_HOSTING_RELOAD_VALIDATE_FAIL", "fixture-v2");
        try
        {
            var rejected = await host.ReloadAsync(AssemblyFor("v2"), DrainTimeout);
            Require(rejected.Outcome == ReloadOutcome.RejectedBeforeTeardown, rejected.Detail);
            Require(rejected.ActiveGeneration == first.ActiveGeneration, "Failed initialization must leave the old generation active.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NWN_HOSTING_RELOAD_VALIDATE_FAIL", null);
        }
    }

    [TestMethod]
    public async Task CancelsStaleWorkAndDrainsQueuedExecutingCallbacks()
    {
        await using var native = new FixtureNativeBindings();
        await using var host = new ReloadHost(native, loadOptions: LoadOptions);
        Require((await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout)).Outcome == ReloadOutcome.Applied, "V1 did not start.");
        native.QueueEveryKind(); // Pending callbacks own old-generation delegates.
        var replaced = await host.ReloadAsync(AssemblyFor("v2"), DrainTimeout);
        Require(replaced.Outcome == ReloadOutcome.Applied, replaced.Detail);
        await native.ExecutePendingAsync(); // Disposed handles make queued V1 callbacks no-ops.
        Require(native.Count("event") == 0 && native.Count("timer") == 0 && native.Count("ui") == 0,
            "Pending callbacks from the removed generation were executed.");

        Environment.SetEnvironmentVariable("NWN_HOSTING_RELOAD_HOLD_WORK_UNTIL_CANCEL", "1");
        try
        {
            native.QueueEveryKind();
            await native.ExecutePendingAsync(); // V2 callbacks enter the cancellation-aware fixture gate.
            var back = await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout);
            Require(back.Outcome == ReloadOutcome.Applied, back.Detail);
            await Task.Delay(40);
            Require(native.Messages("async").All(message => !message.StartsWith("fixture-v2", StringComparison.Ordinal)),
                "A stale V2 asynchronous completion mutated gameplay after replacement.");
            Require(native.Messages("cancelled").Any(message => message.StartsWith("fixture-v2", StringComparison.Ordinal)),
                "Replacement did not observe cancellation of V2 owned work.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NWN_HOSTING_RELOAD_HOLD_WORK_UNTIL_CANCEL", null);
        }
    }

    [TestMethod]
    public async Task DrainsBlockedCallback()
    {
        await using var native = new FixtureNativeBindings();
        await using var host = new ReloadHost(native, loadOptions: LoadOptions);
        Require((await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout)).Outcome == ReloadOutcome.Applied, "V1 did not start.");
        native.BlockNextCallbackBody();
        native.QueueEveryKind();
        var callback = native.ExecuteOnePendingAsync().AsTask();
        await native.WaitForBlockedCallbackAsync();
        var reload = host.ReloadAsync(AssemblyFor("v2"), DrainTimeout).AsTask();
        await Task.Delay(30);
        Require(!reload.IsCompleted, "Reload completed while a synchronous callback body was still blocked.");
        native.ReleaseBlockedCallback();
        await callback;
        var report = await reload;
        Require(report.Outcome == ReloadOutcome.Applied, report.Detail);
    }

    [TestMethod]
    public async Task RequiresRestartAfterPostTeardownActivationFailure()
    {
        await using var native = new FixtureNativeBindings();
        await using var host = new ReloadHost(native, loadOptions: LoadOptions);
        Require((await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout)).Outcome == ReloadOutcome.Applied, "V1 did not start.");
        Environment.SetEnvironmentVariable("NWN_HOSTING_RELOAD_ACTIVATE_FAIL", "fixture-v2");
        try
        {
            var failure = await host.ReloadAsync(AssemblyFor("v2"), DrainTimeout);
            Require(failure.Outcome == ReloadOutcome.RestartRequired, failure.Detail);
            var reported = native.Messages("activation-failure");
            Require(reported.Count == 1 && reported.Single().Contains("   at ", StringComparison.Ordinal),
                "A post-teardown activation failure must be reported with its stack trace.");
            var retry = await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout);
            Require(retry.Outcome == ReloadOutcome.RejectedBeforeTeardown && retry.Detail.Contains("supervisor restart", StringComparison.Ordinal),
                "A post-teardown failure must not silently revive or replace the torn-down generation.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NWN_HOSTING_RELOAD_ACTIVATE_FAIL", null);
        }
    }

    [TestMethod]
    public async Task RequiresRestartAfterQuiescedDrainFailure()
    {
        await using var native = new FixtureNativeBindings();
        await using var host = new ReloadHost(native, loadOptions: LoadOptions);
        Require((await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout)).Outcome == ReloadOutcome.Applied, "V1 did not start.");
        Environment.SetEnvironmentVariable("NWN_HOSTING_RELOAD_DRAIN_FAIL", "fixture-v1");
        try
        {
            var failure = await host.ReloadAsync(AssemblyFor("v2"), DrainTimeout);
            Require(failure.Outcome == ReloadOutcome.RestartRequired, failure.Detail);
            var retry = await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout);
            Require(retry.Outcome == ReloadOutcome.RejectedBeforeTeardown && retry.Detail.Contains("supervisor restart", StringComparison.Ordinal),
                "A quiesced generation must not be advertised as still active after its drain fails.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NWN_HOSTING_RELOAD_DRAIN_FAIL", null);
        }
    }
}
