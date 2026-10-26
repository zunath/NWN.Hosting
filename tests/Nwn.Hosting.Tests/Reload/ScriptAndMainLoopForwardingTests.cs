using Nwn.Hosting.Reload;
using Nwn.Hosting.Reload.ScriptGameplay;
using Nwn.Hosting.Testing.Fixtures;
using static Nwn.Hosting.Testing.Support.TestAssertions;

namespace Nwn.Hosting.Tests.Reload;

/// <summary>
/// <see cref="ReloadHost.TryDispatchScript(string, uint)"/>/<see cref="ReloadHost.PumpMainLoop"/>
/// reach only the currently active generation - proven across a real reload swap using
/// <see cref="ScriptGameplayGeneration"/>, a fixture whose recognized script name is tied to its
/// own generation number (see that type's doc comment for why one fixture assembly is enough to
/// prove both directions of the swap).
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ScriptAndMainLoopForwardingTests
{
    private const uint ObjectSelf = 0x02000000;

    [TestMethod]
    public async Task TryDispatchScript_ReturnsFalse_WhenNoGenerationIsActive()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native);

        Assert.IsFalse(host.TryDispatchScript("nh_gen_0", ObjectSelf));
    }

    [TestMethod]
    public async Task TryDispatchScript_ReachesTheActiveGenerationsOwnScript()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native);
        var report = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(report.Outcome == ReloadOutcome.Applied, report.Detail);
        var scriptName = ScriptGameplayGeneration.ScriptNameFor(report.ActiveGeneration!.Value);

        var handled = host.TryDispatchScript(scriptName, ObjectSelf);

        Assert.IsTrue(handled);
        Assert.AreEqual(1, native.Count("script-handled"));
    }

    [TestMethod]
    public async Task TryDispatchScript_ReturnsFalse_ForAnUnrecognizedScript()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native);
        var report = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(report.Outcome == ReloadOutcome.Applied, report.Detail);

        Assert.IsFalse(host.TryDispatchScript("nh_totally_unknown", ObjectSelf));
    }

    [TestMethod]
    public async Task TryDispatchScript_NeverThrowsIntoTheCaller_WhenTheGenerationsDispatcherThrows()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native);
        var report = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(report.Outcome == ReloadOutcome.Applied, report.Detail);

        var handled = host.TryDispatchScript(ScriptGameplayGeneration.ThrowingScriptName, ObjectSelf);

        Assert.IsFalse(handled, "A dispatch failure must be treated as 'not handled', not surfaced as a result.");
        Assert.AreEqual(1, native.Count("script-dispatch-failure"));
    }

    [TestMethod]
    public async Task TryDispatchScript_AfterAReloadSwap_NeverReachesTheRetiredGeneration()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native);
        var first = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(first.Outcome == ReloadOutcome.Applied, first.Detail);
        var firstScript = ScriptGameplayGeneration.ScriptNameFor(first.ActiveGeneration!.Value);

        var second = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(second.Outcome == ReloadOutcome.Applied, second.Detail);
        var secondScript = ScriptGameplayGeneration.ScriptNameFor(second.ActiveGeneration!.Value);

        Assert.AreNotEqual(firstScript, secondScript);
        Assert.IsFalse(host.TryDispatchScript(firstScript, ObjectSelf), "The retired generation's own script must no longer be handled.");
        Assert.IsTrue(host.TryDispatchScript(secondScript, ObjectSelf), "The newly active generation's script must be handled.");
        Assert.AreEqual(1, native.Count("script-handled"), "Only the live generation's handler may have run.");
    }

    [TestMethod]
    public async Task PumpMainLoop_DoesNothing_WhenNoGenerationIsActive()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native);

        host.PumpMainLoop();

        Assert.AreEqual(0, native.Count("tick"));
    }

    [TestMethod]
    public async Task PumpMainLoop_TicksTheActiveGenerationsScheduler()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native);
        var report = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(report.Outcome == ReloadOutcome.Applied, report.Detail);

        host.PumpMainLoop();
        host.PumpMainLoop();

        Assert.AreEqual(2, native.Count("tick"));
    }

    [TestMethod]
    public async Task PumpMainLoop_NeverThrowsIntoTheCaller_WhenTheGenerationsSinkThrows()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native);
        var report = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(report.Outcome == ReloadOutcome.Applied, report.Detail);

        Environment.SetEnvironmentVariable(ScriptGameplayGeneration.TickFailureVariable, "1");
        try
        {
            host.PumpMainLoop();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScriptGameplayGeneration.TickFailureVariable, null);
        }

        Assert.AreEqual(1, native.Count("main-loop-dispatch-failure"));
    }

    [TestMethod]
    public async Task PumpMainLoop_AfterAReloadSwap_NeverReachesTheRetiredGeneration()
    {
        var native = new RecordingNativeBindings();
        await using var host = new ReloadHost(native);
        var first = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(first.Outcome == ReloadOutcome.Applied, first.Detail);
        host.PumpMainLoop();
        Assert.AreEqual(1, native.Count("tick"));

        var second = await host.ReloadAsync(ScriptGameplayAssemblyPath(), DrainTimeout);
        Require(second.Outcome == ReloadOutcome.Applied, second.Detail);
        host.PumpMainLoop();

        Assert.AreEqual(2, native.Count("tick"), "Only the live generation ticks; the retired one must never run again.");
    }

    private static string ScriptGameplayAssemblyPath() =>
        Path.Combine(AppContext.BaseDirectory, "Nwn.Hosting.Reload.ScriptGameplay.dll");
}
