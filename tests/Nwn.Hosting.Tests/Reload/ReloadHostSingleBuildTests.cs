using System.Runtime.Loader;
using Nwn.Hosting.Reload;
using Nwn.Hosting.Testing.Fixtures;
using static Nwn.Hosting.Testing.Support.TestAssertions;
using static Nwn.Hosting.Tests.Support.FixtureAssemblies;

namespace Nwn.Hosting.Tests.Reload;

/// <summary>
/// <see cref="GenerationLoadOptions.Collectible"/> false loads one build into a non-collectible context for a server
/// that runs with reload off. Each test leaves its build loaded for the rest of the run, as a server would.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ReloadHostSingleBuildTests
{
    [TestMethod]
    public async Task LoadedGeneration_WithCollectibleFalse_LoadsIntoANonCollectibleContextThatIsNeverUnloaded()
    {
        var native = new RecordingNativeBindings();
        var collectible = LoadedGeneration.Load(DispatchAssembly, 1);
        var single = LoadedGeneration.Load(DispatchAssembly, 2, GenerationLoadOptions.Default with { Collectible = false });

        Require(collectible.IsCollectible, "Generations must stay collectible by default.");
        Require(!single.IsCollectible, "The option must load a non-collectible generation.");
        Require(AssemblyLoadContext.GetLoadContext(single.Gameplay.GetType().Assembly) is { IsCollectible: false },
            "The generation's own assembly must live in a non-collectible context.");
        var collectibleContext = await collectible.DisposeAndUnloadAsync(native);
        var singleContext = await single.DisposeAndUnloadAsync(native);
        Require(await LoadedGeneration.ProbeCollectedAsync(collectibleContext), "The collectible generation must still unload.");
        Require(singleContext.IsAlive, "A non-collectible context is never unloaded.");
    }

    [TestMethod]
    public async Task ReloadHost_WithCollectibleFalse_ActivatesOneNonCollectibleBuildAndRefusesEveryLaterReload()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native, loadOptions: new GenerationLoadOptions([], "single-build") { Collectible = false });

        var first = await host.ReloadAsync(DispatchAssembly, DrainTimeout);
        var second = await host.ReloadAsync(DispatchAssembly, DrainTimeout);

        Require(first.Outcome == ReloadOutcome.Applied, first.Detail);
        var contextName = native.Messages("context").Single();
        Require(AssemblyLoadContext.All.Single(context => context.Name == contextName) is { IsCollectible: false },
            $"The build must load into a non-collectible context ({contextName}).");
        Require(second.Outcome == ReloadOutcome.RejectedBeforeTeardown && second.Detail.Contains("non-collectible", StringComparison.Ordinal),
            $"A reload must be refused: {second.Outcome} {second.Detail}");
        Require(second.ActiveGeneration == first.ActiveGeneration && host.IsCurrent(first.ActiveGeneration!.Value),
            "The refused reload must leave the build active.");
        Require(native.Messages("context").Count == 1, "The refused reload must not load a second build.");
        Require((await host.ProbeRetiredContextsAsync()).ProbedContexts == 0, "Nothing was retired.");
    }

    [TestMethod]
    public async Task ReloadHost_WithCollectibleFalse_RefusesAnotherBuildEvenAfterTheFirstOneFailed()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native, loadOptions: LoadOptions with { Collectible = false });
        Environment.SetEnvironmentVariable("NWN_HOSTING_RELOAD_VALIDATE_FAIL", "fixture-v1");
        ReloadReport failed;
        try
        {
            failed = await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NWN_HOSTING_RELOAD_VALIDATE_FAIL", null);
        }

        var retry = await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout);

        Require(failed.Outcome == ReloadOutcome.RejectedBeforeTeardown && !failed.PreviousContextCollected,
            "The failed build stays loaded: a non-collectible context cannot be collected.");
        Require(retry.Outcome == ReloadOutcome.RejectedBeforeTeardown && retry.Detail.Contains("non-collectible", StringComparison.Ordinal),
            $"A second non-collectible build must never load: {retry.Detail}");
        Require(retry.ActiveGeneration is null, "Nothing is active after the failed first build.");
    }
}
