using Nwn.Hosting.Reload;
using Nwn.Hosting.Testing.Fixtures;
using static Nwn.Hosting.Testing.Support.TestAssertions;
using static Nwn.Hosting.Tests.Support.FixtureAssemblies;

namespace Nwn.Hosting.Tests.Reload;

/// <summary><see cref="ReloadHost"/> hands its <see cref="GenerationLoadOptions"/> to every generation it loads.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ReloadHostLoadOptionsTests
{
    [TestMethod]
    public async Task WithoutLoadOptions_EachGenerationLoadsIntoAContextWithTheDefaultPrefix()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native);

        var report = await host.ReloadAsync(DispatchAssembly, DrainTimeout);

        Require(report.Outcome == ReloadOutcome.Applied, report.Detail);
        var name = native.Messages("context").Single();
        Require(name.StartsWith("nwn-hosting-gameplay-", StringComparison.Ordinal), $"Unexpected context name '{name}'.");
    }

    [TestMethod]
    public async Task WithLoadOptions_EveryGenerationLoadsIntoAContextWithTheConsumersPrefix()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native, loadOptions: new GenerationLoadOptions([], "consumer-gameplay"));

        Require((await host.ReloadAsync(DispatchAssembly, DrainTimeout)).Outcome == ReloadOutcome.Applied, "First load failed.");
        Require((await host.ReloadAsync(DispatchAssembly, DrainTimeout)).Outcome == ReloadOutcome.Applied, "Second load failed.");

        var names = native.Messages("context");
        Require(names.Count == 2 && names.All(name => name.StartsWith("consumer-gameplay-", StringComparison.Ordinal)),
            $"Unexpected context names: {string.Join(", ", names)}.");
        Require(names.Distinct().Count() == 2, "Each generation must load into its own context.");
    }

    [TestMethod]
    public async Task FixtureGenerationsRegisterCallbacksThroughTheSharedTestingAssembly()
    {
        await using var native = new FixtureNativeBindings();
        await using var host = new ReloadHost(native, loadOptions: LoadOptions);

        var report = await host.ReloadAsync(AssemblyFor("v1"), DrainTimeout);

        Require(report.Outcome == ReloadOutcome.Applied, report.Detail);
        Require(native.ActiveRegistrationCount == 3,
            $"The fixture must see the test's IFixtureCallbackBindings type and register three callbacks (it registered {native.ActiveRegistrationCount}).");
    }
}
