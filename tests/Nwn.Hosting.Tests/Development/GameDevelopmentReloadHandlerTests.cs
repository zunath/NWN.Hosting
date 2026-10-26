using System.Text.Json;
using Nwn.Hosting.Contracts.Generations;
using Nwn.Hosting.Development;
using Nwn.Hosting.Reload;

namespace Nwn.Hosting.Tests.Development;

[TestClass]
[TestCategory("Unit")]
public sealed class GameDevelopmentReloadHandlerTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void CreateWorkspace()
    {
        _root = Path.Combine(Path.GetTempPath(), "nwn-hosting-development-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void RemoveWorkspace() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    [DataRow(ReloadOutcome.Applied, DevelopmentReloadStatus.Applied)]
    [DataRow(ReloadOutcome.RejectedBeforeTeardown, DevelopmentReloadStatus.Rejected)]
    [DataRow(ReloadOutcome.RestartRequired, DevelopmentReloadStatus.RestartRequired)]
    public async Task TheMatchingCandidatePathAndActualOutcomeAreAcknowledgedAsync(ReloadOutcome outcome, DevelopmentReloadStatus status)
    {
        var signal = Path.Combine(_root, "reload.signal");
        var receipt = Path.Combine(_root, "reload.result");
        var id = Guid.NewGuid();
        var request = new DevelopmentReloadRequest(1, id);
        await DevelopmentReloadFiles.WriteRequestAsync(signal, request, CancellationToken.None);
        string? observedPath = null;
        var handler = new GameDevelopmentReloadHandler(signal, Path.Combine(_root, "legacy.dll"), _root, receipt,
            (path, token) => { observedPath = path; return Task.FromResult(Report(outcome)); }, (kind, detail) => { });

        await handler.HandleAsync(CancellationToken.None);

        Assert.AreEqual(Path.Combine(_root, request.GetAssemblyRelativePath()), observedPath);
        var result = await DevelopmentReloadFiles.ReadResultAsync(receipt, CancellationToken.None);
        Assert.AreEqual(id, result.GenerationId);
        Assert.AreEqual(status, result.Status);
        Assert.AreEqual("actual outcome", result.Detail);
    }

    [TestMethod]
    public async Task AFailedReceiptWriteIsRetriedWithoutRepeatingTheNativeReloadAsync()
    {
        var signal = Path.Combine(_root, "reload.signal");
        var receipt = Path.Combine(_root, "missing", "reload.result");
        var id = Guid.NewGuid();
        await DevelopmentReloadFiles.WriteRequestAsync(signal, new(1, id), CancellationToken.None);
        var reloads = 0;
        var handler = new GameDevelopmentReloadHandler(signal, "legacy.dll", _root, receipt,
            (path, token) => { reloads++; return Task.FromResult(Report(ReloadOutcome.Applied)); }, (kind, detail) => { });

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => handler.HandleAsync(CancellationToken.None));
        Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
        await handler.HandleAsync(CancellationToken.None);

        Assert.AreEqual(1, reloads, "Retrying a failed receipt must not reapply the candidate.");
        Assert.AreEqual(DevelopmentReloadStatus.Applied, (await DevelopmentReloadFiles.ReadResultAsync(receipt, CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task AHostFailureProducesAFailedReceiptWithoutBeingReportedAsAppliedAsync()
    {
        var signal = Path.Combine(_root, "reload.signal");
        var receipt = Path.Combine(_root, "reload.result");
        await DevelopmentReloadFiles.WriteRequestAsync(signal, new(1, Guid.NewGuid()), CancellationToken.None);
        var handler = new GameDevelopmentReloadHandler(signal, "legacy.dll", _root, receipt,
            (path, token) => throw new InvalidOperationException("lease invalid"), (kind, detail) => { });

        await handler.HandleAsync(CancellationToken.None);

        var result = await DevelopmentReloadFiles.ReadResultAsync(receipt, CancellationToken.None);
        Assert.AreEqual(DevelopmentReloadStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "lease invalid");
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"FormatVersion\":2,\"GenerationId\":\"11111111-1111-1111-1111-111111111111\"}")]
    [DataRow("{\"FormatVersion\":1,\"GenerationId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [DataRow("{\"FormatVersion\":1,\"GenerationId\":\"11111111-1111-1111-1111-111111111111\",\"Path\":\"../outside.dll\"}")]
    [DataRow("{")]
    [DataRow("")]
    public async Task InvalidOrPartialRequestsCannotSelectAnAssemblyOrProduceAnAcknowledgmentAsync(string payload)
    {
        var signal = Path.Combine(_root, "reload.signal");
        var receipt = Path.Combine(_root, "reload.result");
        await File.WriteAllTextAsync(signal, payload);
        var reloads = 0;
        var handler = new GameDevelopmentReloadHandler(signal, "legacy.dll", _root, receipt,
            (path, token) => { reloads++; return Task.FromResult(Report(ReloadOutcome.Applied)); }, (kind, detail) => { });

        Exception? failure = null;
        try { await handler.HandleAsync(CancellationToken.None); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException) { failure = exception; }

        Assert.IsNotNull(failure);
        Assert.AreEqual(0, reloads);
        Assert.IsFalse(File.Exists(receipt));
    }

    [TestMethod]
    public async Task OversizedRequestsAreRefusedBeforeActivationAsync()
    {
        var signal = Path.Combine(_root, "reload.signal");
        await File.WriteAllTextAsync(signal, new string(' ', 8193));
        var handler = new GameDevelopmentReloadHandler(signal, "legacy.dll", _root, Path.Combine(_root, "reload.result"),
            (path, token) => throw new AssertFailedException("An oversized request cannot reach activation."), (kind, detail) => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task LegacyTimestampSignalsKeepTheirExistingConfiguredAssemblyPathAsync()
    {
        var signal = Path.Combine(_root, "reload.signal");
        await File.WriteAllTextAsync(signal, DateTimeOffset.UtcNow.ToString("O"));
        var legacy = Path.Combine(_root, "legacy.dll");
        string? observed = null;
        var handler = new GameDevelopmentReloadHandler(signal, legacy, null, null,
            (path, token) => { observed = path; return Task.FromResult(Report(ReloadOutcome.Applied)); }, (kind, detail) => { });
        await handler.HandleAsync(CancellationToken.None);
        Assert.AreEqual(legacy, observed);
    }

    private static ReloadReport Report(ReloadOutcome outcome) =>
        new(outcome, 1, outcome == ReloadOutcome.Applied ? 2 : 1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, true, "actual outcome");
}
