using Nwn.Hosting.Bootstrap;

namespace Nwn.Hosting.Tests.Support;

/// <summary>An <see cref="INativeHostExtension"/> that appends every call it receives to a shared event list.</summary>
internal sealed class RecordingNativeHostExtension(string name, List<string> events) : INativeHostExtension
{
    public Exception? InstallFailure { get; init; }

    public Exception? ModuleLoadedFailure { get; init; }

    public Exception? BeginFrameFailure { get; init; }

    public void Install()
    {
        events.Add($"{name}.Install");
        if (InstallFailure is not null) throw InstallFailure;
    }

    public void OnModuleLoaded()
    {
        events.Add($"{name}.OnModuleLoaded");
        if (ModuleLoadedFailure is not null) throw ModuleLoadedFailure;
    }

    public void BeginFrame()
    {
        events.Add($"{name}.BeginFrame");
        if (BeginFrameFailure is not null) throw BeginFrameFailure;
    }
}
