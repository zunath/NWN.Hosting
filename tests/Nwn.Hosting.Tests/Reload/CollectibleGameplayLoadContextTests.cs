using System.Reflection;
using System.Runtime.Loader;
using Nwn.Hosting.Contracts.Generations;
using Nwn.Hosting.Reload;
using Nwn.Hosting.Testing.Support;

namespace Nwn.Hosting.Tests.Reload;

/// <summary>
/// Which assemblies a generation's collectible context resolves to the host's copy and which it loads for
/// itself. The test assembly stands in for the gameplay assembly: its dependency manifest lists every
/// assembly the context could load on its own.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CollectibleGameplayLoadContextTests
{
    private static readonly string GameplayAssemblyPath = typeof(CollectibleGameplayLoadContextTests).Assembly.Location;
    private static readonly AssemblyName ConsumerAssembly = typeof(TestAssertions).Assembly.GetName();

    [TestMethod]
    public void ConsumerListedNames_ResolveToTheHostsCopy()
    {
        var context = Create(new GenerationLoadOptions([ConsumerAssembly.Name!]));
        try
        {
            var resolved = context.ResolveForTest(ConsumerAssembly);

            Assert.AreSame(typeof(TestAssertions).Assembly, resolved);
            Assert.AreNotSame(context, AssemblyLoadContext.GetLoadContext(resolved!));
        }
        finally
        {
            context.Unload();
        }
    }

    [TestMethod]
    public void UnlistedNames_LoadIntoTheCollectibleContext()
    {
        var context = Create(GenerationLoadOptions.Default);
        try
        {
            var resolved = context.ResolveForTest(ConsumerAssembly);

            Assert.IsNotNull(resolved, "The dependency manifest lists this assembly, so the context loads its own copy.");
            Assert.AreNotSame(typeof(TestAssertions).Assembly, resolved);
            Assert.AreSame(context, AssemblyLoadContext.GetLoadContext(resolved));
            Assert.IsTrue(context.IsCollectible);
        }
        finally
        {
            context.Unload();
        }
    }

    [TestMethod]
    public void NamesNothingListsAndNothingProvides_ResolveToNothing()
    {
        var context = Create(GenerationLoadOptions.Default);
        try
        {
            Assert.IsNull(context.ResolveForTest(new AssemblyName("Nwn.Hosting.Tests.NotAnAssembly")));
        }
        finally
        {
            context.Unload();
        }
    }

    [TestMethod]
    [DataRow("NWN.Core")]
    [DataRow("NWN.Native")]
    [DataRow("NWNX.NET")]
    public void TheEngineAssemblies_AreAlwaysSharedEvenWhenNothingIsListed(string name)
    {
        var context = Create(GenerationLoadOptions.Default);
        try
        {
            var resolved = context.ResolveForTest(new AssemblyName(name));

            Assert.IsNotNull(resolved);
            Assert.AreNotSame(context, AssemblyLoadContext.GetLoadContext(resolved));
            Assert.AreEqual(name, resolved.GetName().Name);
        }
        finally
        {
            context.Unload();
        }
    }

    [TestMethod]
    public void TheContractsAssembly_IsAlwaysTheHostsCopy()
    {
        var context = Create(GenerationLoadOptions.Default);
        try
        {
            var resolved = context.ResolveForTest(typeof(IGameplayGeneration).Assembly.GetName());

            Assert.AreSame(typeof(IGameplayGeneration).Assembly, resolved);
        }
        finally
        {
            context.Unload();
        }
    }

    [TestMethod]
    public void TheContextName_StartsWithTheConfiguredPrefix()
    {
        var context = Create(new GenerationLoadOptions([], "consumer-gameplay"));
        try
        {
            StringAssert.StartsWith(context.Name, "consumer-gameplay-");
            Assert.AreEqual("consumer-gameplay-".Length + 32, context.Name!.Length, "The prefix is followed by a GUID without separators.");
        }
        finally
        {
            context.Unload();
        }
    }

    [TestMethod]
    public void TheContextName_DefaultsToTheHostingPrefix()
    {
        var context = Create(GenerationLoadOptions.Default);
        try
        {
            StringAssert.StartsWith(context.Name, "nwn-hosting-gameplay-");
        }
        finally
        {
            context.Unload();
        }
    }

    [TestMethod]
    public void MissingOptionsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new CollectibleGameplayLoadContext(GameplayAssemblyPath, null!));
    }

    private static CollectibleGameplayLoadContext Create(GenerationLoadOptions options) => new(GameplayAssemblyPath, options);
}
