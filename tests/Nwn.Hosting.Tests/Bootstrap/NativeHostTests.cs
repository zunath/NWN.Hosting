using System.Runtime.InteropServices;
using Nwn.Hosting.Bootstrap;
using Nwn.Hosting.Testing.Fixtures;
using Nwn.Hosting.Tests.Support;

namespace Nwn.Hosting.Tests.Bootstrap;

/// <summary>
/// The native registration cannot run outside a server, so these tests drive the parts of
/// <see cref="NativeHost"/> around it: the initialization sequence with the registration injected, and the
/// signal, run-script and main-loop handlers the unmanaged callbacks delegate to.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class NativeHostTests
{
    private const string LogPrefix = "TESTHOST_NATIVE";
    private const string ModuleLoadFinish = "ON_MODULE_LOAD_FINISH";
    private const string DestroyServer = "ON_DESTROY_SERVER";
    private const uint Creature = 0x02000001;

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

    [TestMethod]
    public void HandleRunScript_ReturnsTheResultOfAModeThatAnswersResults_WithObjectSelfSetDuringTheCall()
    {
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", core => new ResultHostMode(core, _events)
        {
            Results = { ["cond_true"] = NativeHost.ScriptConditionTrue, ["cond_false"] = NativeHost.ScriptHandled, ["action"] = NativeHost.ScriptHandled },
        }), _ => { });
        _events.Clear();

        Assert.AreEqual(1, RunScript("cond_true"), "A true conditional script must reach the engine as 1.");
        Assert.AreEqual(0, RunScript("cond_false"));
        Assert.AreEqual(0, RunScript("action"));
        Assert.AreEqual(-1, RunScript("not_mine"), "An unhandled script must reach the engine as NWNX's ~0.");

        CollectionAssert.AreEqual(
            new[]
            {
                "mode.OnRunScriptResult:cond_true@0x02000001", "mode.OnRunScriptResult:cond_false@0x02000001",
                "mode.OnRunScriptResult:action@0x02000001", "mode.OnRunScriptResult:not_mine@0x02000001",
            },
            _events);
    }

    [TestMethod]
    [DataRow(true, 0)]
    [DataRow(false, -1)]
    public void HandleRunScript_AModeThatAnswersOnlyWhetherItHandledTheScript_KeepsTheEarlierResults(bool handled, int expected)
    {
        Initialize(new RecordingHostMode(_events) { HandlesScripts = handled });
        _events.Clear();

        Assert.AreEqual(expected, RunScript("any_script"));
        CollectionAssert.AreEqual(new[] { "mode.OnRunScript:any_script" }, _events);
    }

    [TestMethod]
    public void HandleRunScript_WhenTheModeThrows_ReportsTheFailureAndLeavesTheScriptUnhandled()
    {
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", core => new ResultHostMode(core, _events) { ThrowsFor = "broken" }), _ => { });
        _events.Clear();

        Assert.AreEqual(NativeHost.ScriptNotHandled, RunScript("broken"));
        CollectionAssert.AreEqual(new[] { "mode.OnRunScriptResult:broken@0x02000001", "mode.OnCallbackFailure:run-script" }, _events);
    }

    [TestMethod]
    public void HandleRunScript_WithoutAScriptNameOrAMode_IsNotHandled()
    {
        Assert.AreEqual(NativeHost.ScriptNotHandled, RunScript("before_initialize"));

        Initialize(new RecordingHostMode(_events) { HandlesScripts = true });
        _events.Clear();

        Assert.AreEqual(NativeHost.ScriptNotHandled, NativeHost.HandleRunScript(IntPtr.Zero, Creature));
        Assert.AreEqual(0, _events.Count, "The mode must not be asked about a missing script name.");
    }

    [TestMethod]
    public void Initialize_GivesTheModeAClosureHandlerWithTheConfiguredLimit()
    {
        CoreFunctionHandler? unlimited = null;
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", core =>
        {
            unlimited = core;
            return new RecordingHostMode(_events);
        }) { MaximumPendingClosures = null }, _ => { });
        NativeHost.ResetForTest();
        CoreFunctionHandler? byDefault = null;
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", core =>
        {
            byDefault = core;
            return new RecordingHostMode(_events);
        }), _ => { });

        Assert.IsNull(unlimited!.MaximumPendingClosures);
        Assert.AreEqual(CoreFunctionHandler.DefaultMaximumPendingClosures, byDefault!.MaximumPendingClosures);
    }

    [TestMethod]
    public void HandleMainLoop_TheFrameClosureBelongsToTheHost_SoRetiringAGenerationNeverStopsTheFrames()
    {
        var scheduler = new RecordingClosureScheduler();
        CoreFunctionHandler? core = null;
        var mode = new RecordingHostMode(_events) { RunsFrames = true };
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", created =>
        {
            core = created;
            return mode;
        }), _ => { }, scheduler);
        NativeHost.HandleSignal(ModuleLoadFinish);
        core!.BeginGeneration(1);
        _events.Clear();

        NativeHost.HandleMainLoop(5);
        var dropped = core.RetireGeneration(1);
        var frame = scheduler.Scheduled.Single();
        core.Dispatch(frame.EventId, frame.Object);
        NativeHost.HandleMainLoop(6);

        Assert.AreEqual(0, dropped, "The frame closure must not be dropped with a generation.");
        Assert.AreEqual(nameof(INativeClosureScheduler.AssignCommand), frame.Kind);
        Assert.AreEqual(NativeHost.ModuleObject, frame.Object);
        CollectionAssert.AreEqual(new[] { "mode.BeforeFrame", "mode.RunFrame:5", "mode.BeforeFrame" }, _events);
        Assert.AreEqual(2, scheduler.Scheduled.Count, "The next frame must still be scheduled after a retirement.");
    }

    [TestMethod]
    public void Initialize_GivesTheModeAClosureHandlerWithTheConfiguredRejectedClosureHandling()
    {
        CoreFunctionHandler? byDefault = null;
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", core =>
        {
            byDefault = core;
            return new RecordingHostMode(_events);
        }), _ => { });
        NativeHost.ResetForTest();
        CoreFunctionHandler? dropping = null;
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", core =>
        {
            dropping = core;
            return new RecordingHostMode(_events);
        }) { RejectedClosures = RejectedClosureHandling.Drop }, _ => { });

        Assert.AreEqual(RejectedClosureHandling.Throw, byDefault!.RejectedClosureHandling);
        Assert.AreEqual(RejectedClosureHandling.Drop, dropping!.RejectedClosureHandling);
    }

    [TestMethod]
    public void Initialize_NamesTheFrameDeliveryAndRejectedClosureHandlingInTheBootstrapLineWhenTheyAreNotTheDefaults()
    {
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "test-mode", _ => new RecordingHostMode(_events))
        {
            FrameDelivery = FrameDelivery.Inline,
            RejectedClosures = RejectedClosureHandling.Drop,
        }, _ => { });

        Assert.AreEqual(
            $"{LogPrefix} bootstrap registered signal, run-script, and main-loop handlers; closure handler registered mode=test-mode " +
            $"frames=Inline rejected-closures=Drop{Environment.NewLine}",
            _console.ToString());
    }

    [TestMethod]
    public void InlineFrames_RunOnEveryCallbackFromTheFirst_WithoutTheModuleLoadGateOrAClosure()
    {
        var scheduler = new RecordingClosureScheduler();
        CoreFunctionHandler? core = null;
        var mode = new RecordingHostMode(_events) { RunsFrames = true };
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", created =>
        {
            core = created;
            return mode;
        }, [new RecordingNativeHostExtension("first", _events)]) { FrameDelivery = FrameDelivery.Inline }, _ => { }, scheduler);
        _events.Clear();

        NativeHost.HandleMainLoop(0);
        NativeHost.HandleMainLoop(1);
        NativeHost.HandleSignal(ModuleLoadFinish);
        NativeHost.HandleMainLoop(2);

        CollectionAssert.AreEqual(
            new[]
            {
                "first.BeginFrame", "mode.BeforeFrame", "mode.RunFrame:0",
                "first.BeginFrame", "mode.BeforeFrame", "mode.RunFrame:1",
                "first.OnModuleLoaded", $"mode.OnSignal:{ModuleLoadFinish}",
                "first.BeginFrame", "mode.BeforeFrame", "mode.RunFrame:2",
            },
            _events,
            "An inline frame runs inside the callback that delivers it, before the module has loaded as well.");
        Assert.AreEqual(0, scheduler.Scheduled.Count, "Inline frames schedule no closure.");
        Assert.AreEqual(0, core!.PendingCount);
    }

    [TestMethod]
    public void InlineFrames_WhenBeforeFrameAnswersFalse_SkipThatFrameOnly()
    {
        var mode = new RecordingHostMode(_events) { RunsFrames = false };
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", _ => mode) { FrameDelivery = FrameDelivery.Inline }, _ => { });
        _events.Clear();

        NativeHost.HandleMainLoop(0);
        mode.RunsFrames = true;
        NativeHost.HandleMainLoop(1);

        CollectionAssert.AreEqual(new[] { "mode.BeforeFrame", "mode.BeforeFrame", "mode.RunFrame:1" }, _events);
    }

    [TestMethod]
    public void InlineFrames_RunWithObjectSelfInvalid_AndRestoreTheObjectSelfAroundThem()
    {
        CoreFunctionHandler? core = null;
        var mode = new RecordingHostMode(_events) { RunsFrames = true };
        mode.ObserveOnRunFrame = () => $"0x{core!.ObjectSelf:X8}";
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", created =>
        {
            core = created;
            return mode;
        }) { FrameDelivery = FrameDelivery.Inline }, _ => { });
        _events.Clear();

        NativeHost.HandleMainLoop(0);
        uint afterFrame;
        using (core!.EnterObjectSelf(Creature))
        {
            NativeHost.HandleMainLoop(1);
            afterFrame = core.ObjectSelf;
        }

        var invalid = $"0x{NWN.Core.NWScript.OBJECT_INVALID:X8}";
        CollectionAssert.AreEqual(
            new[] { "mode.BeforeFrame", $"mode.RunFrame:0[{invalid}]", "mode.BeforeFrame", $"mode.RunFrame:1[{invalid}]" },
            _events,
            "An inline frame runs in NWNX's main-loop script context, whose OBJECT_SELF is OBJECT_INVALID.");
        Assert.AreEqual(Creature, afterFrame, "The frame must hand back the OBJECT_SELF it found.");
    }

    [TestMethod]
    public void ModuleClosureFrames_StillRunAsTheModule()
    {
        var scheduler = new RecordingClosureScheduler();
        CoreFunctionHandler? core = null;
        var mode = new RecordingHostMode(_events) { RunsFrames = true };
        mode.ObserveOnRunFrame = () => $"0x{core!.ObjectSelf:X8}";
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", created =>
        {
            core = created;
            return mode;
        }), _ => { }, scheduler);
        NativeHost.HandleSignal(ModuleLoadFinish);
        _events.Clear();

        NativeHost.HandleMainLoop(4);
        var frame = scheduler.Scheduled.Single();
        core!.Dispatch(frame.EventId, frame.Object);

        CollectionAssert.AreEqual(new[] { "mode.BeforeFrame", $"mode.RunFrame:4[0x{NativeHost.ModuleObject:X8}]" }, _events);
    }

    [TestMethod]
    public void InlineFrames_AFailedFrameIsReportedAsAMainLoopCallbackFailure_AndTheNextFrameStillRuns()
    {
        var failure = new InvalidOperationException("frame failed");
        var mode = new RecordingHostMode(_events) { RunsFrames = true, RunFrameFailure = failure };
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", _ => mode) { FrameDelivery = FrameDelivery.Inline }, _ => { });
        _events.Clear();

        NativeHost.HandleMainLoop(3);
        mode.RunFrameFailure = null;
        NativeHost.HandleMainLoop(4);

        CollectionAssert.AreEqual(
            new[] { "mode.BeforeFrame", "mode.RunFrame:3", "mode.OnCallbackFailure:main-loop", "mode.BeforeFrame", "mode.RunFrame:4" },
            _events);
        Assert.AreSame(failure, mode.LastCallbackFailure);
        Assert.AreEqual("main-loop", mode.CallbackFailurePhase);
        Assert.IsNull(mode.LastFrameScheduleFailure);
    }

    [TestMethod]
    public void InlineFrames_AFailedExtensionFrameStartIsStillAScheduleFailure_AndSkipsTheFrame()
    {
        var failure = new InvalidOperationException("frame hook failed");
        var mode = new RecordingHostMode(_events) { RunsFrames = true };
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", _ => mode,
            [new RecordingNativeHostExtension("broken", _events) { BeginFrameFailure = failure }]) { FrameDelivery = FrameDelivery.Inline }, _ => { });
        _events.Clear();

        NativeHost.HandleMainLoop(0);

        CollectionAssert.AreEqual(new[] { "broken.BeginFrame", "mode.OnFrameScheduleFailure" }, _events);
        Assert.AreSame(failure, mode.LastFrameScheduleFailure);
    }

    [TestMethod]
    public void InlineFrames_StopOnceTheServerIsDestroyed()
    {
        var mode = new RecordingHostMode(_events) { RunsFrames = true };
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", _ => mode) { FrameDelivery = FrameDelivery.Inline }, _ => { });
        _events.Clear();

        NativeHost.HandleSignal(DestroyServer);
        NativeHost.HandleMainLoop(8);

        CollectionAssert.AreEqual(new[] { "mode.OnDestroyServer" }, _events);
    }

    [TestMethod]
    public void UnderDrop_ARefusedFrameClosureIsStillAScheduleFailure_AndTheNextFrameIsScheduled()
    {
        var scheduler = new RecordingClosureScheduler { Refuse = true };
        CoreFunctionHandler? core = null;
        var mode = new RecordingHostMode(_events) { RunsFrames = true };
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", created =>
        {
            core = created;
            return mode;
        }) { RejectedClosures = RejectedClosureHandling.Drop }, _ => { }, scheduler);
        NativeHost.HandleSignal(ModuleLoadFinish);
        _events.Clear();

        NativeHost.HandleMainLoop(1);
        scheduler.Refuse = false;
        NativeHost.HandleMainLoop(2);
        var frame = scheduler.Scheduled.Single();
        core!.Dispatch(frame.EventId, frame.Object);

        CollectionAssert.AreEqual(
            new[] { "mode.BeforeFrame", "mode.OnFrameScheduleFailure", "mode.BeforeFrame", "mode.RunFrame:2" },
            _events,
            "Dropping the host's frame closure silently would stop every later frame.");
        Assert.AreEqual(1, core.RejectedClosureCount);
    }

    private static int RunScript(string script)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(script);
        try
        {
            return NativeHost.HandleRunScript(pointer, Creature);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private void Initialize(RecordingHostMode mode, params string[] extensionNames)
    {
        var extensions = extensionNames.Select(name => (INativeHostExtension)new RecordingNativeHostExtension(name, _events)).ToArray();
        NativeHost.Initialize(new NativeHostOptions(LogPrefix, "mode", _ => mode, extensions), _ => { });
    }
}
