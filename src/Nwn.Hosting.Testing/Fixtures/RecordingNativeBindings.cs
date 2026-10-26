using System.Collections.Concurrent;
using Nwn.Hosting.Contracts.Generations;

namespace Nwn.Hosting.Testing.Fixtures;

/// <summary>
/// Engine-thread dispatch that runs inline on the calling async flow (no dedicated thread), plus
/// captured <see cref="Report"/> calls - script and main-loop forwarding tests use this to observe a
/// fixture generation's <c>IScriptDispatcher</c>/<c>IMainLoopSink</c> side effects (e.g. "did the tick
/// actually run") across the collectible-context boundary without needing a second IPC channel.
/// </summary>
public sealed class RecordingNativeBindings : IPermanentNativeBindings
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _reports = new();

    public ValueTask InvokeOnEngineThreadAsync(Func<ValueTask> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return action();
    }


    public void Report(string category, string message) => _reports.GetOrAdd(category, _ => new()).Enqueue(message);

    public int Count(string category) => _reports.TryGetValue(category, out var messages) ? messages.Count : 0;
    public IReadOnlyCollection<string> Messages(string category) => _reports.TryGetValue(category, out var messages) ? messages.ToArray() : [];
}
