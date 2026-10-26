namespace Nwn.Hosting.Bootstrap;

/// <summary>What a consumer decides when it starts <see cref="NativeHost"/>.</summary>
public sealed record NativeHostOptions
{
    /// <param name="logPrefix">The first word of every console line <see cref="NativeHost.Log"/> writes; supervisors match on it.</param>
    /// <param name="modeName">Names the mode in the bootstrap log line.</param>
    /// <param name="createMode">Creates the mode that handles the native callbacks, given the closure handler it must dispose on server destruction.</param>
    /// <param name="extensions">Native hooks installed in list order after the host's own; empty when omitted.</param>
    public NativeHostOptions(
        string logPrefix,
        string modeName,
        Func<CoreFunctionHandler, IHostMode> createMode,
        IReadOnlyList<INativeHostExtension>? extensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(modeName);
        ArgumentNullException.ThrowIfNull(createMode);
        LogPrefix = logPrefix;
        ModeName = modeName;
        CreateMode = createMode;
        Extensions = extensions ?? [];
    }

    /// <summary>The first word of every console line <see cref="NativeHost.Log"/> writes.</summary>
    public string LogPrefix { get; }

    /// <summary>Names the mode in the bootstrap log line.</summary>
    public string ModeName { get; }

    /// <summary>Creates the mode that handles the native callbacks.</summary>
    public Func<CoreFunctionHandler, IHostMode> CreateMode { get; }

    /// <summary>Native hooks installed in list order after the host's own.</summary>
    public IReadOnlyList<INativeHostExtension> Extensions { get; }
}
