namespace Nwn.Hosting.Development;

/// <summary>
/// A gameplay host's reload trigger: polls one file's last-write time and calls back once it
/// changes. Deliberately the smallest production-worthy trigger -- an operator (or a deploy script)
/// publishes a new gameplay assembly and then touches/rewrites the signal file; this class notices the
/// change and the callback asks the host to reload through the same <c>ReloadHost</c> that activated the
/// first generation -- never a second reload mechanism. A deployment that wants hot reload opts in by
/// creating a watcher for a signal file; a host that creates none never reloads from disk.
///
/// A plain background-timer shape (pure I/O, never the engine thread) with a fake-timer test seam.
/// </summary>
public sealed class GameReloadSignalWatcher : IAsyncDisposable
{
    private readonly string _signalFilePath;
    private readonly Func<CancellationToken, Task> _onSignalAsync;
    private readonly Action<Exception> _onFailure;
    private readonly IPeriodicTimer _timer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private DateTime? _lastSeenWriteTimeUtc;
    private bool _inspectInitialRequest;

    public GameReloadSignalWatcher(string signalFilePath, TimeSpan pollInterval, Func<CancellationToken, Task> onSignalAsync, Action<Exception> onFailure,
        bool inspectInitialRequest = false)
        : this(signalFilePath, onSignalAsync, onFailure, new PeriodicTimerAdapter(pollInterval), inspectInitialRequest)
    {
    }

    /// <summary>Test-only seam: a fake <see cref="IPeriodicTimer"/> drives polling cadence deterministically.</summary>
    internal GameReloadSignalWatcher(string signalFilePath, Func<CancellationToken, Task> onSignalAsync, Action<Exception> onFailure, IPeriodicTimer timer,
        bool inspectInitialRequest = false)
    {
        _signalFilePath = signalFilePath;
        _onSignalAsync = onSignalAsync ?? throw new ArgumentNullException(nameof(onSignalAsync));
        _onFailure = onFailure ?? throw new ArgumentNullException(nameof(onFailure));
        _timer = timer;
        // Legacy markers establish the baseline; a typed request can arrive before the host finishes booting.
        _lastSeenWriteTimeUtc = ReadWriteTimeUtcOrNull();
        _inspectInitialRequest = inspectInitialRequest;
        _loop = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    var current = ReadWriteTimeUtcOrNull();
                    if (_inspectInitialRequest && current is not null)
                    {
                        var request = await Nwn.Hosting.Contracts.Generations.DevelopmentReloadFiles.ReadRequestAsync(
                            _signalFilePath, _stopping.Token).ConfigureAwait(false);
                        if (request is not null)
                            await _onSignalAsync(_stopping.Token).ConfigureAwait(false);
                        _lastSeenWriteTimeUtc = current;
                        _inspectInitialRequest = false;
                        continue;
                    }
                    if (current is { } observed && observed != _lastSeenWriteTimeUtc)
                    {
                        await _onSignalAsync(_stopping.Token).ConfigureAwait(false);
                        _lastSeenWriteTimeUtc = observed;
                    }
                }
                catch (Exception exception) when (!_stopping.IsCancellationRequested)
                {
                    _onFailure(exception);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private DateTime? ReadWriteTimeUtcOrNull()
    {
        try
        {
            return File.Exists(_signalFilePath) ? File.GetLastWriteTimeUtc(_signalFilePath) : null;
        }
        catch (IOException)
        {
            // A signal file mid-write (another process still touching it) is treated as "no change
            // yet"; the next poll picks up the settled timestamp.
            return _lastSeenWriteTimeUtc;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _timer.Dispose();
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _stopping.Dispose();
    }
}
