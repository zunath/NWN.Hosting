using Nwn.Hosting.Bootstrap;
using Nwn.Hosting.Testing.Fixtures;

namespace Nwn.Hosting.Tests.Bootstrap;

/// <summary>
/// <see cref="CoreFunctionHandler"/> driven through a <see cref="RecordingClosureScheduler"/>: the test thread is the
/// engine thread, and the test delivers each callback the engine would make.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CoreFunctionHandlerTests
{
    private const uint Creature = 0x02000001;
    private const uint OtherCreature = 0x02000002;

    private readonly RecordingClosureScheduler _scheduler = new();
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
    public void WithoutALimit_TheDefaultOf64Applies()
    {
        Assert.AreEqual(64, new CoreFunctionHandler(Environment.CurrentManagedThreadId).MaximumPendingClosures);
        var handler = Create(CoreFunctionHandler.DefaultMaximumPendingClosures);
        for (var index = 0; index < 64; index++)
        {
            handler.ClosureDelayCommand(Creature, 1, () => { });
        }

        var exhausted = Assert.Throws<InvalidOperationException>(() => handler.ClosureDelayCommand(Creature, 1, () => { }));

        StringAssert.Contains(exhausted.Message, "(64)");
        Assert.AreEqual(64, handler.PendingCount);
        Assert.AreEqual(64, _scheduler.Scheduled.Count, "The refused closure must not reach the engine.");
    }

    [TestMethod]
    public void AConfiguredLimit_IsEnforcedAndADispatchedClosureFreesItsSlot()
    {
        var handler = Create(2);
        handler.ClosureAssignCommand(Creature, () => { });
        handler.ClosureAssignCommand(Creature, () => { });
        Assert.Throws<InvalidOperationException>(() => handler.ClosureAssignCommand(Creature, () => { }));

        handler.Dispatch(_scheduler.Scheduled[0].EventId, Creature);
        handler.ClosureAssignCommand(Creature, () => { });

        Assert.AreEqual(2, handler.PendingCount);
        Assert.AreEqual(2, handler.MaximumPendingClosures);
    }

    [TestMethod]
    public void NoLimit_AcceptsFarMoreThanTheDefault()
    {
        var handler = Create(null);
        for (var index = 0; index < 10_000; index++)
        {
            handler.ClosureActionDoCommand(Creature, () => { });
        }

        Assert.IsNull(handler.MaximumPendingClosures);
        Assert.AreEqual(10_000, handler.PendingCount);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void AZeroOrNegativeLimit_IsRefused(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFunctionHandler(Environment.CurrentManagedThreadId, limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFunctionHandler(Environment.CurrentManagedThreadId, limit, _scheduler));
    }

    [TestMethod]
    public void AMissingScheduler_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new CoreFunctionHandler(Environment.CurrentManagedThreadId, null, null!));
    }

    [TestMethod]
    public void EachKindReachesTheEngineAndItsCallbackRunsAsTheCallbacksObject()
    {
        var handler = Create(null);
        var ranAs = new List<uint>();
        handler.ClosureActionDoCommand(Creature, () => ranAs.Add(handler.ObjectSelf));
        handler.ClosureAssignCommand(OtherCreature, () => ranAs.Add(handler.ObjectSelf));
        handler.ClosureDelayCommand(Creature, 2.5f, () => ranAs.Add(handler.ObjectSelf));

        var scheduled = _scheduler.Scheduled;
        CollectionAssert.AreEqual(
            new[] { "ActionDoCommand", "AssignCommand", "DelayCommand" },
            scheduled.Select(closure => closure.Kind).ToArray());
        Assert.AreEqual(2.5f, scheduled[2].Duration);
        Assert.AreEqual(3, scheduled.Select(closure => closure.EventId).Distinct().Count(), "Every closure needs its own event id.");

        foreach (var closure in scheduled)
        {
            handler.Dispatch(closure.EventId, closure.Object);
        }

        CollectionAssert.AreEqual(new[] { Creature, OtherCreature, Creature }, ranAs);
        Assert.AreEqual(0, handler.PendingCount);
    }

    [TestMethod]
    public void AClosureTheEngineRefuses_IsForgottenAndThrows()
    {
        var handler = Create(null);
        _scheduler.Refuse = true;

        Assert.Throws<InvalidOperationException>(() => handler.ClosureDelayCommand(Creature, 1, () => { }));

        Assert.AreEqual(0, handler.PendingCount);
    }

    [TestMethod]
    public void BeforeAnyGenerationBegins_ClosuresBelongToTheHostAndSurviveARetirement()
    {
        var handler = Create(null);
        var ran = 0;
        handler.ClosureDelayCommand(Creature, 1, () => ran++);

        Assert.AreEqual(CoreFunctionHandler.HostGeneration, handler.SchedulingGeneration);
        handler.BeginGeneration(1);
        Assert.AreEqual(0, handler.RetireGeneration(1), "A host closure must not be dropped with a generation.");
        handler.Dispatch(_scheduler.Scheduled.Single().EventId, Creature);

        Assert.AreEqual(1, ran);
        Assert.AreEqual(0, handler.DroppedClosures);
    }

    [TestMethod]
    public void RetiringAGeneration_DropsOnlyItsClosures_IgnoresTheirCallbacks_AndKeepsTheNextGenerationsInOrder()
    {
        var handler = Create(null);
        var ran = new List<string>();
        handler.BeginGeneration(1);
        handler.ClosureDelayCommand(Creature, 1, () => ran.Add("1:a"));
        handler.ClosureActionDoCommand(Creature, () => ran.Add("1:b"));
        handler.HostAssignCommand(NativeHost.ModuleObject, () => ran.Add("host"));
        var retiredEvents = _scheduler.Scheduled.Take(2).Select(closure => closure.EventId).ToArray();
        var hostEvent = _scheduler.Scheduled[2].EventId;

        var dropped = handler.RetireGeneration(1);
        handler.BeginGeneration(2);
        handler.ClosureDelayCommand(Creature, 1, () => ran.Add("2:a"));
        handler.ClosureDelayCommand(Creature, 1, () => ran.Add("2:b"));
        handler.ClosureDelayCommand(Creature, 1, () => ran.Add("2:c"));
        var activeEvents = _scheduler.Scheduled.Skip(3).Select(closure => closure.EventId).ToArray();

        foreach (var eventId in retiredEvents.Append(hostEvent).Concat(activeEvents))
        {
            handler.Dispatch(eventId, Creature);
        }

        Assert.AreEqual(2, dropped);
        Assert.AreEqual(2, handler.DroppedClosures);
        CollectionAssert.AreEqual(new[] { "host", "2:a", "2:b", "2:c" }, ran, "The retired generation's closures must never run.");
        Assert.AreEqual(0, handler.PendingCount);
        Assert.IsFalse(_console.ToString().Contains("ignored stale event", StringComparison.Ordinal),
            "The callback of a dropped closure is expected and must be ignored quietly.");
    }

    [TestMethod]
    public void AfterItsRetirement_ARetiredGenerationsClosuresAreNeverScheduled_UntilTheNextGenerationBegins()
    {
        var handler = Create(null);
        var ran = new List<string>();
        handler.BeginGeneration(1);
        handler.RetireGeneration(1);

        handler.ClosureDelayCommand(Creature, 1, () => ran.Add("leftover"));
        handler.HostAssignCommand(NativeHost.ModuleObject, () => ran.Add("host"));
        handler.BeginGeneration(2);
        handler.ClosureAssignCommand(Creature, () => ran.Add("2"));
        foreach (var closure in _scheduler.Scheduled)
        {
            handler.Dispatch(closure.EventId, closure.Object);
        }

        Assert.AreEqual(2, _scheduler.Scheduled.Count, "The retired generation's leftover closure must not reach the engine.");
        Assert.AreEqual(1, handler.DroppedClosures);
        Assert.AreEqual(2, handler.SchedulingGeneration);
        CollectionAssert.AreEqual(new[] { "host", "2" }, ran);
    }

    [TestMethod]
    public void RetiringAGenerationThatIsNotScheduling_LeavesTheSchedulingGenerationAlone()
    {
        var handler = Create(null);
        var ran = 0;
        handler.BeginGeneration(2);

        Assert.AreEqual(0, handler.RetireGeneration(1));
        handler.ClosureAssignCommand(Creature, () => ran++);
        handler.Dispatch(_scheduler.Scheduled.Single().EventId, Creature);

        Assert.AreEqual(1, ran);
    }

    [TestMethod]
    public void TheCallbackOfAnUnknownEvent_IsStillLoggedAsStale()
    {
        var handler = Create(null);

        handler.Dispatch(999, Creature);

        StringAssert.Contains(_console.ToString(), "ignored stale event=999 object=0x02000001");
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    public void TheHostIsNeverAGenerationToBeginOrRetire(long generation)
    {
        var handler = Create(null);

        Assert.Throws<ArgumentOutOfRangeException>(() => handler.BeginGeneration(generation));
        Assert.Throws<ArgumentOutOfRangeException>(() => handler.RetireGeneration(generation));
    }

    [TestMethod]
    public void GenerationChangesOffTheEngineThread_AreRefused()
    {
        var handler = Create(null);
        Exception? begin = null;
        Exception? retire = null;

        // A dedicated thread, never the test's own; it runs no generation code, so it may exit.
        var other = new Thread(() =>
        {
            begin = Record(() => handler.BeginGeneration(1));
            retire = Record(() => handler.RetireGeneration(1));
        });
        other.Start();
        other.Join();

        Assert.IsInstanceOfType<InvalidOperationException>(begin);
        Assert.IsInstanceOfType<InvalidOperationException>(retire);
        Assert.AreEqual(CoreFunctionHandler.HostGeneration, handler.SchedulingGeneration);
    }

    private static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private CoreFunctionHandler Create(int? maximumPendingClosures) =>
        new(Environment.CurrentManagedThreadId, maximumPendingClosures, _scheduler);
}
