using Nwn.Hosting.Bootstrap;
using Nwn.Hosting.Tests.Support;

namespace Nwn.Hosting.Tests.Bootstrap;

/// <summary>
/// The native registration cannot run outside a server, so these tests drive the parts of
/// <see cref="NativeHost"/> around it: the initialization sequence with the registration injected, and the
/// signal and main-loop handlers the unmanaged callbacks delegate to.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class NativeHostTests
{
    private const string LogPrefix = "TESTHOST_NATIVE";
    private const string ModuleLoadFinish = "ON_MODULE_LOAD_FINISH";
    private const string DestroyServer = "ON_DESTROY_SERVER";

    private readonly List<string> _events = [];
    private TextWriter? _originalOut;
    private StringWriter _console = new();

    [TestInitialize]
    public void RedirectConsole()
    {
        NativeHost.ResetForTest();
        _originalOut = Console.Out;
        _console = new StringWriter();
        Console.SetOut(_console);
    }

    [TestCleanup]
    public void RestoreConsole()
    {
        Console.SetOut(_originalOut!);
        NativeHost.ResetForTest();
    }

    [TestMethod]
    public void Initialize_RunsTheStepsInOrder_ExtensionsInListOrderBetweenTheNativeRegistrationAndTheModeStart()
    {
        var mode = new RecordingHostMode(_events) { ObserveOnStart = () => _console.ToString().Contains("closure handler registered") ? "logged" : "not-logged" };
        var options = new NativeHostOptions(LogPrefix, "test-mode", core =>
        {
            _events.Add("mode.Created");
            return mode;
        }, [new RecordingNativeHostExtension("first", _events), new RecordingNativeHostExtension("second", _events)]);

        NativeHost.Initialize(options, core =>
        {
            Assert.IsNotNull(core);
            _events.Add("native.Registered");
        });

        CollectionAssert.AreEqual(
            new[] { "mode.Created", "native.Registered", "first.Install", "second.Install", "mode.Start[not-logged]" },
            _events,
            "Initialization order changed.");
        Assert.AreEqual(
            $"{LogPrefix} bootstrap registered signal, run-script, and main-loop handlers; closure handler registered mode=test-mode{Environment.NewLine}",
            _console.ToString(),
            "The bootstrap line must follow the mode start and carry the consumer's prefix and mode name.");
    }

    [TestMethod]
    public void Initialize_WithoutExtensions_StartsTheMode()
    {
        var options = new NativeHostOptions(LogPrefix, "plain", _ => new RecordingHostMode(_events));

        NativeHost.Initialize(options, _ => _events.Add("native.Registered"));

        CollectionAssert.AreEqual(new[] { "native.Registered", "mode.Start" }, _events);
    }

    [TestMethod]
    public void Initialize_OnlyTheFirstCallDoesAnything()
    {
        var created = 0;
        var options = new NativeHostOptions(LogPrefix, "first", _ =>
        {
            created++;
            return new RecordingHostMode(_events);
        });
        var secondOptions = new NativeHostOptions("OTHER_NATIVE", "second", _ =>
        {
            created += 10;
            return new RecordingHostMode(_events);
        });

        NativeHost.Initialize(options, _ => { });
        NativeHost.Initialize(secondOptions, _ => Assert.Fail("A second initialization must not register again."));
        NativeHost.Log("check", "prefix");

        Assert.AreEqual(1, created);
        StringAssert.Contains(_console.ToString(), $"{LogPrefix} check prefix");
        Assert.IsFalse(_console.ToString().Contains("OTHER_NATIVE", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Initialize_RejectsMissingOptions()
    {
        Assert.Throws<ArgumentNullException>(() => NativeHost.Initialize(null!, _ => { }));
    }

    [TestMethod]
    public void Log_UsesTheDefaultPrefixBeforeInitializationAndTheConsumerPrefixAfter()
    {
        NativeHost.Log("before", "message");
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", _ => new RecordingHostMode(_events)), _ => { });
        NativeHost.Log("after", "message");

        var lines = _console.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual("NWN_HOSTING_NATIVE before message", lines[0]);
        Assert.AreEqual($"{LogPrefix} after message", lines[^1]);
    }

    [TestMethod]
    public void Initialize_WhenAnExtensionFailsToInstall_ReportsThroughTheModeAndNeverStartsIt()
    {
        var failure = new InvalidOperationException("hook unavailable");
        var mode = new RecordingHostMode(_events);
        var options = new NativeHostOptions(LogPrefix, "mode", _ => mode,
            [new RecordingNativeHostExtension("broken", _events) { InstallFailure = failure }, new RecordingNativeHostExtension("later", _events)]);

        NativeHost.Initialize(options, _ => { });

        CollectionAssert.AreEqual(new[] { "broken.Install", "mode.OnCallbackFailure:bootstrap" }, _events);
        Assert.AreSame(failure, mode.LastCallbackFailure);
    }

    [TestMethod]
    public void Initialize_WhenTheModeCannotBeCreated_LogsTheFailureWithThePrefix()
    {
        var options = new NativeHostOptions(LogPrefix, "mode", _ => throw new InvalidOperationException("no mode"));

        NativeHost.Initialize(options, _ => Assert.Fail("The native registration must not run without a mode."));

        Assert.AreEqual($"{LogPrefix} failure bootstrap: InvalidOperationException: no mode{Environment.NewLine}", _console.ToString());
    }

    [TestMethod]
    public void HandleSignal_OnModuleLoadFinish_NotifiesEveryExtensionInOrderThenMarksTheModuleFinishedThenForwardsTheSignal()
    {
        var mode = new RecordingHostMode(_events) { RunsFrames = false };
        Initialize(mode, "first", "second");
        _events.Clear();

        NativeHost.HandleSignal(ModuleLoadFinish);
        NativeHost.HandleMainLoop(1);

        CollectionAssert.AreEqual(
            new[]
            {
                "first.OnModuleLoaded", "second.OnModuleLoaded", $"mode.OnSignal:{ModuleLoadFinish}",
                "first.BeginFrame", "second.BeginFrame", "mode.BeforeFrame",
            },
            _events,
            "The module counts as finished once the extensions were told, so the first frame after the signal consults the mode.");
    }

    [TestMethod]
    public void HandleSignal_OtherSignals_AreForwardedToTheModeOnly()
    {
        Initialize(new RecordingHostMode(_events), "first");
        _events.Clear();

        NativeHost.HandleSignal("ON_MODULE_LOAD_START");

        CollectionAssert.AreEqual(new[] { "mode.OnSignal:ON_MODULE_LOAD_START" }, _events);
    }

    [TestMethod]
    public void HandleSignal_WhenAnExtensionFailsOnModuleLoad_PropagatesAndLeavesTheModuleUnfinished()
    {
        var failure = new InvalidOperationException("module hook failed");
        var mode = new RecordingHostMode(_events) { RunsFrames = true };
        var options = new NativeHostOptions(LogPrefix, "mode", _ => mode,
            [new RecordingNativeHostExtension("broken", _events) { ModuleLoadedFailure = failure }]);
        NativeHost.Initialize(options, _ => { });
        _events.Clear();

        var thrown = Assert.Throws<InvalidOperationException>(() => NativeHost.HandleSignal(ModuleLoadFinish));
        NativeHost.HandleMainLoop(1);

        Assert.AreSame(failure, thrown);
        CollectionAssert.AreEqual(
            new[] { "broken.OnModuleLoaded", "broken.BeginFrame" },
            _events,
            "The unmanaged callback reports the failure; the module must not count as finished and the mode must not see the signal.");
    }

    [TestMethod]
    public void HandleMainLoop_BeforeTheModuleFinishesLoading_OnlyBeginsTheExtensionFrames()
    {
        Initialize(new RecordingHostMode(_events), "first", "second");
        _events.Clear();

        NativeHost.HandleMainLoop(7);

        CollectionAssert.AreEqual(new[] { "first.BeginFrame", "second.BeginFrame" }, _events);
    }

    [TestMethod]
    public void HandleMainLoop_WhenAnExtensionFailsToBeginAFrame_ReportsTheScheduleFailureToTheMode()
    {
        var failure = new InvalidOperationException("frame hook failed");
        var mode = new RecordingHostMode(_events);
        var options = new NativeHostOptions(LogPrefix, "mode", _ => mode,
            [new RecordingNativeHostExtension("broken", _events) { BeginFrameFailure = failure }]);
        NativeHost.Initialize(options, _ => { });
        _events.Clear();

        NativeHost.HandleMainLoop(3);

        CollectionAssert.AreEqual(new[] { "broken.BeginFrame", "mode.OnFrameScheduleFailure" }, _events);
        Assert.AreSame(failure, mode.LastFrameScheduleFailure);
    }

    [TestMethod]
    public void HandleSignal_OnDestroyServer_StopsTheModeFromSeeingFramesButNotTheExtensions()
    {
        var mode = new RecordingHostMode(_events) { RunsFrames = false };
        Initialize(mode, "first");
        NativeHost.HandleSignal(ModuleLoadFinish);
        _events.Clear();

        NativeHost.HandleSignal(DestroyServer);
        NativeHost.HandleMainLoop(9);

        CollectionAssert.AreEqual(new[] { "mode.OnDestroyServer", "first.BeginFrame" }, _events);
    }

    private void Initialize(RecordingHostMode mode, params string[] extensionNames)
    {
        var extensions = extensionNames.Select(name => (INativeHostExtension)new RecordingNativeHostExtension(name, _events)).ToArray();
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", _ => mode, extensions), _ => { });
    }
}
