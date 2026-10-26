namespace Nwn.Hosting.Bootstrap;

/// <summary>
/// A native hook a consumer adds to <see cref="NativeHost"/>. The host calls each extension in list order:
/// <see cref="Install"/> once during initialization (after the host's own hooks, before the mode starts),
/// <see cref="OnModuleLoaded"/> when the module finishes loading, and <see cref="BeginFrame"/> at the start of
/// every main-loop callback. All members run on the engine thread; an exception from <see cref="Install"/>
/// fails initialization, and one from the others is reported through the mode.
/// </summary>
public interface INativeHostExtension
{
    /// <summary>Installs the extension's native hooks.</summary>
    void Install();

    /// <summary>Runs when the <c>ON_MODULE_LOAD_FINISH</c> signal arrives, before the host marks the module finished.</summary>
    void OnModuleLoaded() { }

    /// <summary>Runs at the start of every main-loop callback, before the host's own frame handling.</summary>
    void BeginFrame() { }
}
