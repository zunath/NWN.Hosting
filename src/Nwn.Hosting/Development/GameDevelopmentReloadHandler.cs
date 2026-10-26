using Nwn.Hosting.Contracts.Generations;
using Nwn.Hosting.Reload;

namespace Nwn.Hosting.Development;

/// <summary>Resolves isolated candidates, serializes activation and preserves terminal receipts across retries.</summary>
public sealed class GameDevelopmentReloadHandler(
    string signalPath,
    string legacyAssemblyPath,
    string? generationRoot,
    string? resultPath,
    Func<string, CancellationToken, Task<ReloadReport>> reloadAsync,
    Action<string, string> log)
{
    private readonly Dictionary<Guid, DevelopmentReloadResult> _results = [];

    public async Task HandleAsync(CancellationToken cancellationToken)
    {
        var request = await DevelopmentReloadFiles.ReadRequestAsync(signalPath, cancellationToken).ConfigureAwait(false);
        log("reload-signal-observed", request is null ? signalPath : $"generation={request.GenerationId}");
        if (request is null)
        {
            var legacy = await reloadAsync(legacyAssemblyPath, cancellationToken).ConfigureAwait(false);
            log("reload-signal-result", $"outcome={legacy.Outcome} detail={legacy.Detail}");
            return;
        }
        if (string.IsNullOrWhiteSpace(generationRoot) || string.IsNullOrWhiteSpace(resultPath))
            throw new InvalidOperationException("Immutable development reloads require a generation root and result file.");
        if (!_results.TryGetValue(request.GenerationId, out var result))
        {
            try
            {
                var report = await reloadAsync(Path.Combine(Path.GetFullPath(generationRoot), request.GetAssemblyRelativePath()), cancellationToken).ConfigureAwait(false);
                var status = report.Outcome switch
                {
                    ReloadOutcome.Applied => DevelopmentReloadStatus.Applied,
                    ReloadOutcome.RejectedBeforeTeardown => DevelopmentReloadStatus.Rejected,
                    ReloadOutcome.RestartRequired => DevelopmentReloadStatus.RestartRequired,
                    _ => throw new InvalidOperationException("The permanent host returned an unknown reload outcome."),
                };
                result = new(DevelopmentReloadFiles.CurrentFormatVersion, request.GenerationId, status, LimitDetail(report.Detail));
                log("reload-signal-result", $"outcome={report.Outcome} detail={report.Detail}");
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                result = new(DevelopmentReloadFiles.CurrentFormatVersion, request.GenerationId, DevelopmentReloadStatus.Failed,
                    LimitDetail($"{exception.GetType().Name}: {exception.Message}"));
                log("reload-signal-failure", result.Detail);
            }
            // A lost receipt write must never cause the same candidate to be activated twice.
            _results.Add(request.GenerationId, result);
        }
        await DevelopmentReloadFiles.WriteResultAsync(resultPath, result, cancellationToken).ConfigureAwait(false);
    }

    private static string LimitDetail(string detail) => detail.Length <= 1024 ? detail : detail[..1024];
}
