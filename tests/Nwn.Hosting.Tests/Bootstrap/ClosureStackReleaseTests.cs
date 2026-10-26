using System.Text;
using Nwn.Hosting.Bootstrap;

namespace Nwn.Hosting.Tests.Bootstrap;

[TestClass]
[TestCategory("Unit")]
public sealed class ClosureStackReleaseTests
{
    [TestMethod]
    [DataRow("NWNX_DOTNET_INTERNAL 1")]
    [DataRow("NWNX_DOTNET_INTERNAL 18446744073709551615")]
    public void IsClosureScriptName_MatchesTheNamesNwnxGivesClosures(string name)
    {
        Assert.IsTrue(ClosureStackRelease.IsClosureScriptName(Encoding.ASCII.GetBytes(name)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("NWNX_DOTNET_INTERNAL")]
    [DataRow("nwnx_dotnet_internal 1")]
    [DataRow("module_script_cb")]
    [DataRow("module_heartbeat")]
    public void IsClosureScriptName_LeavesEngineScriptSituationsAlone(string name)
    {
        Assert.IsFalse(ClosureStackRelease.IsClosureScriptName(Encoding.ASCII.GetBytes(name)));
    }
}
