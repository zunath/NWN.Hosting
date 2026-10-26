using Nwn.Hosting.Reload;
using Nwn.Hosting.Testing.Fixtures;
using Nwn.Hosting.Testing.Support;
using static Nwn.Hosting.Testing.Support.TestAssertions;
using static Nwn.Hosting.Tests.Support.FixtureAssemblies;

namespace Nwn.Hosting.Tests.Reload;

/// <summary>
/// .NET 10 before 10.0.13 stores generic-virtual-dispatch entries in the cache's flush sentinel, keeps them
/// through every later flush, and hands an unloaded generation's code address to a later generation whose
/// types reuse its addresses: a hang, an access violation or an <see cref="ExecutionEngineException"/> in
/// generation code (dotnet/runtime#132562; docs/generic-virtual-dispatch.md). <see cref="ReloadHost"/> seals
/// the sentinel; these tests check that generations making generic virtual calls, each collected before the
/// next one runs, leave nothing in it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class VirtualDispatchCacheSentinelTests
{
    private const int Cycles = 30;

    [TestMethod]
    public async Task CollectedGenerationsMakingGenericVirtualCalls_LeaveNoEntryInTheDispatchCacheSentinel()
    {
        var readable = VirtualDispatchCacheInspector.IsReadable;
        Require(readable || !VirtualDispatchCacheSentinel.IsAffected(Environment.Version),
            $"Runtime {Environment.Version} has the sentinel defect, but its dispatch cache cannot be read.");

        var native = new RecordingNativeBindings();
        var collected = 0;
        await using var host = new ReloadHost(native);
        for (var cycle = 1; cycle <= Cycles; cycle++)
        {
            var report = await host.ReloadAsync(DispatchAssembly, DrainTimeout);
            Require(report.Outcome == ReloadOutcome.Applied, $"Cycle {cycle}: {report.Detail}");

            // Collecting the retired generation flushes the dispatch cache, so the next generation's first
            // generic virtual calls are the ones a defective runtime stores in the sentinel.
            await Task.Delay(40);
            var probe = await host.ProbeRetiredContextsAsync();
            RequireNoRetainedContexts(probe, $"Cycle {cycle}");
            Require(probe.CollectedContexts == probe.ProbedContexts, $"Cycle {cycle}: a retired context was probed but not collected.");
            collected += probe.CollectedContexts;

            // A runtime that is not affected may keep its cache elsewhere; collection is still proven above.
            if (readable)
            {
                var stored = VirtualDispatchCacheInspector.StoredSentinelEntries();
                Require(stored == 0,
                    $"Cycle {cycle}: the dispatch cache's flush sentinel holds {stored} entries on runtime {Environment.Version}; " +
                    "a later generation can be dispatched into this one's unloaded code.");
            }
        }

        Require(native.Count("dispatch") == Cycles, $"Every cycle's activation must make its generic virtual calls ({native.Count("dispatch")} of {Cycles} ran).");
        Require(collected == Cycles - 1, $"Every retired context must be collected ({collected} of {Cycles - 1}).");
    }

    [TestMethod]
    public void OnlyDotNet10BeforeTheFixedPatch_IsAffected()
    {
        Require(VirtualDispatchCacheSentinel.IsAffected(new Version(10, 0, 12)), "10.0.12 has the defect.");
        Require(VirtualDispatchCacheSentinel.IsAffected(new Version(10, 0, 0)), "10.0.0 has the defect.");
        Require(!VirtualDispatchCacheSentinel.IsAffected(new Version(10, 0, 13)), "10.0.13 carries the fix.");
        Require(!VirtualDispatchCacheSentinel.IsAffected(new Version(10, 1, 0)), "A later minor version carries the fix.");
        Require(!VirtualDispatchCacheSentinel.IsAffected(new Version(11, 0, 0)), "Later major versions carry the fix.");
        Require(!VirtualDispatchCacheSentinel.IsAffected(new Version(8, 0, 21)), ".NET 8 has no such cache.");
    }
}
