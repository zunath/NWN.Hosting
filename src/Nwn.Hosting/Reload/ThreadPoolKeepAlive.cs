using System.Globalization;

namespace Nwn.Hosting.Reload;

/// <summary>
/// Whether this process keeps every thread-pool worker alive for its whole lifetime, which hot reload
/// requires. The .NET 10 runtime gives each collectible type that has thread statics a TLS index, hands a
/// retired generation's indices to later generations, and, when a thread exits, frees that thread's
/// thread-static handles in the loader allocator of whichever type holds each index at that moment. A
/// thread that ran a retired generation's code and exits after a later generation took over its index
/// therefore releases an unrelated object of the live generation (a <see cref="Type"/>, a statics array),
/// and the next use of it reads freed memory. Framework generics give generation code thread statics
/// without declaring any (<c>ArrayPool&lt;T&gt;.Shared</c> over a generation type, reached from LINQ), so
/// the only safe rule is that no thread which runs generation code exits while the process runs: the
/// engine thread never does, and thread-pool workers do not when ThreadsToKeepAlive is -1
/// (docs/threads-outlive-generations.md in the Nwn.Hosting repository).
/// </summary>
public static class ThreadPoolKeepAlive
{
    /// <summary>The runtime configuration property; the host application's project file sets it for the server.</summary>
    public const string ConfigurationProperty = "System.Threading.ThreadPool.ThreadsToKeepAlive";

    /// <summary>The environment variable the runtime reads before the configuration property.</summary>
    public const string EnvironmentVariable = "DOTNET_ThreadPool_ThreadsToKeepAlive";

    private const int KeepEveryWorker = -1;

    public static bool IsEnabled =>
        KeepsEveryWorker(Environment.GetEnvironmentVariable(EnvironmentVariable), AppContext.GetData(ConfigurationProperty));

    /// <summary>Resolves the setting the way the runtime does: a readable environment variable wins.</summary>
    public static bool KeepsEveryWorker(string? environmentValue, object? configurationValue)
    {
        if (TryRead(environmentValue, out var fromEnvironment)) return fromEnvironment == KeepEveryWorker;
        return TryRead(Convert.ToString(configurationValue, CultureInfo.InvariantCulture), out var fromConfiguration)
            && fromConfiguration == KeepEveryWorker;
    }

    public static void Require()
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException(
                $"Hot reload needs every thread-pool worker kept alive ({ConfigurationProperty} = {KeepEveryWorker} in the " +
                "process's runtime configuration): a worker that retires after a reload frees objects of the live " +
                "generation (docs/threads-outlive-generations.md in the Nwn.Hosting repository).");
        }
    }

    private static bool TryRead(string? text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
