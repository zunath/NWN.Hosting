using Nwn.Hosting.Reload;

namespace Nwn.Hosting.Tests.Reload;

[TestClass]
[TestCategory("Unit")]
public sealed class GenerationLoadOptionsTests
{
    [TestMethod]
    public void Default_SharesNothingExtraAndUsesTheHostingPrefix()
    {
        Assert.AreEqual(0, GenerationLoadOptions.Default.SharedAssemblyNames.Count);
        Assert.AreEqual("nwn-hosting-gameplay", GenerationLoadOptions.Default.ContextNamePrefix);
        Assert.AreEqual(GenerationLoadOptions.DefaultContextNamePrefix, GenerationLoadOptions.Default.ContextNamePrefix);
    }

    [TestMethod]
    public void TheValuesPassedInAreExposedUnchanged()
    {
        string[] names = ["Serilog", "Npgsql"];

        var options = new GenerationLoadOptions(names, "game");

        Assert.AreSame(names, options.SharedAssemblyNames);
        Assert.AreEqual("game", options.ContextNamePrefix);
    }

    [TestMethod]
    public void MissingNamesAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new GenerationLoadOptions(null!));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("  ")]
    public void ABlankPrefixIsRefused(string prefix)
    {
        Assert.Throws<ArgumentException>(() => new GenerationLoadOptions([], prefix));
    }
}
