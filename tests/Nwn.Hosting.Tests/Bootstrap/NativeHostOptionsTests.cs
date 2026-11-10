using Nwn.Hosting.Bootstrap;
using Nwn.Hosting.Tests.Support;

namespace Nwn.Hosting.Tests.Bootstrap;

[TestClass]
[TestCategory("Unit")]
public sealed class NativeHostOptionsTests
{
    [TestMethod]
    public void Extensions_DefaultToEmpty()
    {
        var options = new NativeHostOptions("PREFIX", "mode", _ => new RecordingHostMode([]));

        Assert.AreEqual(0, options.Extensions.Count);
    }

    [TestMethod]
    public void TheValuesPassedInAreExposedUnchanged()
    {
        var extension = new RecordingNativeHostExtension("extension", []);
        Func<CoreFunctionHandler, IHostMode> createMode = _ => new RecordingHostMode([]);

        var options = new NativeHostOptions("PREFIX", "mode", createMode, [extension]);

        Assert.AreEqual("PREFIX", options.LogPrefix);
        Assert.AreEqual("mode", options.ModeName);
        Assert.AreSame(createMode, options.CreateMode);
        Assert.AreSame(extension, options.Extensions.Single());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void ABlankLogPrefixIsRefused(string? prefix)
    {
        Assert.Throws<ArgumentException>(() => new NativeHostOptions(prefix!, "mode", _ => new RecordingHostMode([])));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void ABlankModeNameIsRefused(string? modeName)
    {
        Assert.Throws<ArgumentException>(() => new NativeHostOptions("PREFIX", modeName!, _ => new RecordingHostMode([])));
    }

    [TestMethod]
    public void AMissingModeFactoryIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new NativeHostOptions("PREFIX", "mode", null!));
    }

    [TestMethod]
    public void MaximumPendingClosures_DefaultsTo64_AndTakesALimitOrNone()
    {
        var byDefault = new NativeHostOptions("PREFIX", "mode", _ => new RecordingHostMode([]));
        var limited = byDefault with { MaximumPendingClosures = 4096 };
        var unlimited = byDefault with { MaximumPendingClosures = null };

        Assert.AreEqual(64, byDefault.MaximumPendingClosures);
        Assert.AreEqual(4096, limited.MaximumPendingClosures);
        Assert.IsNull(unlimited.MaximumPendingClosures);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-5)]
    public void AZeroOrNegativeClosureLimitIsRefused(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NativeHostOptions("PREFIX", "mode", _ => new RecordingHostMode([])) { MaximumPendingClosures = limit });
    }
}
