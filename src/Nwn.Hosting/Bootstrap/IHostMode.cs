namespace Nwn.Hosting.Bootstrap;

/// <summary>
/// One host mode's handling of the native callbacks. <see cref="NativeHost"/> owns the callbacks
/// themselves, closure dispatch and frame delivery (<see cref="NativeHostOptions.FrameDelivery"/>); a mode owns
/// everything else. Every member runs on the engine thread.
/// </summary>
public interface IHostMode
{
    /// <summary>Runs once, at the end of <see cref="NativeHost.Initialize(NativeHostOptions)"/>.</summary>
    void Start();

    /// <summary>
    /// Runs on every main-loop callback after the module has loaded, or under <see cref="FrameDelivery.Inline"/> on every
    /// callback from the first one; false skips this frame.
    /// </summary>
    bool BeforeFrame();

    /// <summary>
    /// Runs one main-loop frame: inside the module closure (<c>OBJECT_SELF</c> is the module) by default, or under
    /// <see cref="FrameDelivery.Inline"/> right after <see cref="BeforeFrame"/> inside the main-loop callback
    /// (<c>OBJECT_SELF</c> is <c>OBJECT_INVALID</c>). Handles its own failures; one that escapes reaches
    /// <see cref="OnCallbackFailure"/> (phase <c>closure</c>, or <c>main-loop</c> for an inline frame).
    /// </summary>
    void RunFrame(ulong frame);

    /// <summary>Returns true when the mode handled <paramref name="script"/>; false runs the module's compiled script.</summary>
    bool OnRunScript(string script, uint objectSelf);

    /// <summary>
    /// The value <see cref="NativeHost"/> returns to the engine for <paramref name="script"/>:
    /// <see cref="NativeHost.ScriptNotHandled"/> runs the module's compiled script; any other value means the mode
    /// handled the script and becomes its return value, which a conditional ("starting conditional") script such as a
    /// conversation's "appears when" check reads as true when it is not 0. By default this calls
    /// <see cref="OnRunScript"/> and returns <see cref="NativeHost.ScriptHandled"/> or
    /// <see cref="NativeHost.ScriptNotHandled"/>, so a conditional script is always false; a mode that answers
    /// conditional scripts implements this member instead, and <see cref="NativeHost"/> then never calls
    /// <see cref="OnRunScript"/>. <see cref="Reload.ReloadHost.DispatchScriptResult"/> gives the active generation's value.
    /// </summary>
    int OnRunScriptResult(string script, uint objectSelf) =>
        OnRunScript(script, objectSelf) ? NativeHost.ScriptHandled : NativeHost.ScriptNotHandled;

    /// <summary>Receives every NWNX signal except <c>ON_DESTROY_SERVER</c>.</summary>
    void OnSignal(string signal);

    /// <summary>The server is being destroyed: stop, release the closure handler, and do not wait for later frames.</summary>
    void OnDestroyServer();

    /// <summary>
    /// A bootstrap, run-script, closure or signal callback threw, or an inline frame's <see cref="RunFrame"/> did
    /// (phase <c>main-loop</c>). The host has contained the failure and keeps delivering callbacks.
    /// </summary>
    void OnCallbackFailure(string phase, Exception exception);

    /// <summary>
    /// The main-loop callback failed before the frame ran: an extension's <see cref="INativeHostExtension.BeginFrame"/>,
    /// <see cref="BeforeFrame"/>, or scheduling the frame's module closure threw. The next callback tries again.
    /// </summary>
    void OnFrameScheduleFailure(Exception exception);
}
