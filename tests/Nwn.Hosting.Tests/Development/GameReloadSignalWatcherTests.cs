using Nwn.Hosting.Development;
using Nwn.Hosting.Tests.Support;

namespace Nwn.Hosting.Tests.Development;

/// <summary>
/// Managed coverage of <see cref="GameReloadSignalWatcher"/>'s polling/change-detection:
/// a fake <see cref="IPeriodicTimer"/> drives ticks deterministically, and a real
/// temp file's last-write time is the actual signal, exactly like production reads it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class GameReloadSignalWatcherTests
{
    [TestMethod]
    public async Task ARequestPublishedBeforeTheWatcherStartsIsDeliveredWithoutAnotherWriteAsync()
    {
        var path = Path.GetTempFileName();
        try
        {
            var request = new Nwn.Hosting.Contracts.Generations.DevelopmentReloadRequest(1, Guid.NewGuid());
            await Nwn.Hosting.Contracts.Generations.DevelopmentReloadFiles.WriteRequestAsync(path, request, CancellationToken.None);
            var timer = new FakePeriodicTimer();
            var calls = 0;
            await using var watcher = new GameReloadSignalWatcher(path, async token =>
            {
                var delivered = await Nwn.Hosting.Contracts.Generations.DevelopmentReloadFiles.ReadRequestAsync(path, token);
                Assert.AreEqual(request, delivered);
                calls++;
            }, exception => Assert.Fail(exception.ToString()), timer, inspectInitialRequest: true);
            await timer.TickAndWaitAsync();
            await timer.TickAndWaitAsync();
            Assert.AreEqual(1, calls);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow("initial")]
    [DataRow("2026-10-02T12:00:00.0000000+00:00")]
    public async Task StartupInspectionIgnoresLegacyMarkersAsync(string marker)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, marker);
            var timer = new FakePeriodicTimer();
            var calls = 0;
            await using var watcher = new GameReloadSignalWatcher(path, token =>
            {
                calls++;
                return Task.CompletedTask;
            }, exception => Assert.Fail(exception.ToString()), timer, inspectInitialRequest: true);
            await timer.TickAndWaitAsync();
            await timer.TickAndWaitAsync();
            Assert.AreEqual(0, calls);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task StartupInspectionRetriesAPartialRequestUntilItsWriteCompletesAsync()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{");
            var timer = new FakePeriodicTimer();
            var calls = 0;
            var failures = 0;
            await using var watcher = new GameReloadSignalWatcher(path, token =>
            {
                calls++;
                return Task.CompletedTask;
            }, exception => failures++, timer, inspectInitialRequest: true);
            await timer.TickAndWaitAsync();
            var request = new Nwn.Hosting.Contracts.Generations.DevelopmentReloadRequest(1, Guid.NewGuid());
            await Nwn.Hosting.Contracts.Generations.DevelopmentReloadFiles.WriteRequestAsync(path, request, CancellationToken.None);
            await timer.TickAndWaitAsync();
            await timer.TickAndWaitAsync();
            Assert.AreEqual(1, failures);
            Assert.AreEqual(1, calls);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task AFailedPartialReadIsRetriedWithoutRequiringAnotherFilesystemWriteAsync()
    {
        var path = Path.GetTempFileName();
        try
        {
            var timer = new FakePeriodicTimer();
            var calls = 0;
            var failures = 0;
            await using var watcher = new GameReloadSignalWatcher(path, token =>
            {
                if (++calls == 1) throw new System.Text.Json.JsonException("Partial request write.");
                return Task.CompletedTask;
            }, exception => failures++, timer);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            await timer.TickAndWaitAsync();
            await timer.TickAndWaitAsync();
            await timer.TickAndWaitAsync();
            Assert.AreEqual(1, failures);
            Assert.AreEqual(2, calls);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task NoChangeSinceStartup_NeverCallsBack()
    {
        var path = Path.GetTempFileName();
        try
        {
            var timer = new FakePeriodicTimer();
            var calls = 0;
            await using var watcher = new GameReloadSignalWatcher(path, _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, _ => { }, timer);

            await timer.TickAndWaitAsync();
            await timer.TickAndWaitAsync();

            Assert.AreEqual(0, Volatile.Read(ref calls), "a file that already existed at startup must not itself trigger a reload");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task FileWriteTimeChanges_CallsBackExactlyOnce()
    {
        var path = Path.GetTempFileName();
        try
        {
            var timer = new FakePeriodicTimer();
            var calls = 0;
            await using var watcher = new GameReloadSignalWatcher(path, _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, _ => { }, timer);

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            await timer.TickAndWaitAsync();
            Assert.AreEqual(1, Volatile.Read(ref calls));

            // No further change: the next few ticks must not call back again.
            await timer.TickAndWaitAsync();
            await timer.TickAndWaitAsync();
            Assert.AreEqual(1, Volatile.Read(ref calls));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task RepeatedWrites_CallBackOncePerChange()
    {
        var path = Path.GetTempFileName();
        try
        {
            var timer = new FakePeriodicTimer();
            var calls = 0;
            await using var watcher = new GameReloadSignalWatcher(path, _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, _ => { }, timer);

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            await timer.TickAndWaitAsync();
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10));
            await timer.TickAndWaitAsync();

            Assert.AreEqual(2, Volatile.Read(ref calls));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task OnFailure_IsCalled_WhenTheCallbackThrows()
    {
        var path = Path.GetTempFileName();
        try
        {
            var timer = new FakePeriodicTimer();
            Exception? observed = null;
            await using var watcher = new GameReloadSignalWatcher(
                path, _ => throw new InvalidOperationException("reload blew up"), exception => observed = exception, timer);

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            await timer.TickAndWaitAsync();

            Assert.IsInstanceOfType<InvalidOperationException>(observed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task MissingFile_IsTreatedAsNoSignal_NeverThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nwn-hosting-reload-signal-{Guid.NewGuid():N}.touch");
        var timer = new FakePeriodicTimer();
        var calls = 0;
        await using var watcher = new GameReloadSignalWatcher(path, _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, _ => { }, timer);

        await timer.TickAndWaitAsync();

        Assert.AreEqual(0, Volatile.Read(ref calls));
    }
}
