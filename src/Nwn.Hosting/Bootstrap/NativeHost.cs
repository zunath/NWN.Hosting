using System.Runtime.InteropServices;
using NWN.Core;
using NWNX.NET;

namespace Nwn.Hosting.Bootstrap;

/// <summary>
/// The permanent NWNX DotNET host. A consumer's own parameterless entry point (the method NWNX DotNET
/// is configured to call) passes its <see cref="NativeHostOptions"/> to
/// <see cref="Initialize(NativeHostOptions)"/>. The host registers the native callbacks, routes NWN.Core
/// closures, delivers every main-loop frame to the mode (inside a module closure by default, or inside the main-loop
/// callback itself; see <see cref="FrameDelivery"/>), and hands everything else to the <see cref="IHostMode"/> the
/// options create. Every unmanaged callback contains its own exceptions.
/// </summary>
public static class NativeHost
{
    /// <summary>
    /// The module's object id. It equals the VM's <c>OBJECT_SELF</c> constant, so under
    /// <see cref="FrameDelivery.ModuleClosure"/> a frame runs inside a closure assigned to the module to get the module's
    /// script context.
    /// </summary>
    public const uint ModuleObject = 0x00000000;

    /// <summary>
    /// The run-script result that tells NWNX DotNET the script was not handled, so the engine runs the module's
    /// compiled script (NWNX's <c>~0</c>).
    /// </summary>
    public const int ScriptNotHandled = -1;

    /// <summary>The run-script result of a handled script that is not conditional, or of a conditional one that is false.</summary>
    public const int ScriptHandled = 0;

    /// <summary>The run-script result of a conditional script that is true.</summary>
    public const int ScriptConditionTrue = 1;

    private const string DefaultLogPrefix = "NWN_HOSTING_NATIVE";
    private const string ModuleLoadFinishSignal = "ON_MODULE_LOAD_FINISH";
    private const string DestroyServerSignal = "ON_DESTROY_SERVER";
    private const string InlineFramePhase = "main-loop";

    private static CoreFunctionHandler? _coreFunctions;
    private static IHostMode? _mode;
    private static INativeHostExtension[] _extensions = [];
    private static volatile string _logPrefix = DefaultLogPrefix;
    private static FrameDelivery _frameDelivery = FrameDelivery.ModuleClosure;
    private static int _bootstrapped;
    private static int _moduleFinished;
    private static int _destroyed;
    private static int _frameClosurePending;
    private static long _nextClosureStackFailureReport = 1;

    /// <summary>
    /// Registers the native callbacks and starts the mode. Only the first call in a process does anything;
    /// a failure while starting is reported through the mode (or the console when no mode exists yet), never thrown.
    /// </summary>
    public static void Initialize(NativeHostOptions options) => Initialize(options, RegisterNativeCallbacks);

    /// <summary>
    /// The initialization sequence with the native registration (and, optionally, the closure scheduler) injected, so
    /// the order of the steps around it and the closures it schedules can be exercised without a running server.
    /// </summary>
    internal static void Initialize(
        NativeHostOptions options, Action<CoreFunctionHandler> registerNative, INativeClosureScheduler? closureScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Interlocked.Exchange(ref _bootstrapped, 1) != 0)
        {
            return;
        }

        _logPrefix = options.LogPrefix;
        _extensions = [.. options.Extensions];
        _frameDelivery = options.FrameDelivery;
        try
        {
            var coreFunctions = new CoreFunctionHandler(
                Environment.CurrentManagedThreadId,
                options.MaximumPendingClosures,
                closureScheduler ?? NwnxClosureScheduler.Instance,
                options.RejectedClosures);
            _coreFunctions = coreFunctions;
            _mode = options.CreateMode(coreFunctions);
            registerNative(coreFunctions);
            foreach (var extension in _extensions)
            {
                extension.Install();
            }
            _mode.Start();
            Log("bootstrap", $"registered signal, run-script, and main-loop handlers; closure handler registered mode={options.ModeName}" +
                (options.FrameDelivery == FrameDelivery.ModuleClosure ? string.Empty : $" frames={options.FrameDelivery}") +
                (options.RejectedClosures == RejectedClosureHandling.Throw ? string.Empty : $" rejected-closures={options.RejectedClosures}"));
        }
        catch (Exception exception)
        {
            ReportCallbackFailure("bootstrap", exception);
        }
    }

    /// <summary>
    /// The permanent host's own console log, prefixed with <see cref="NativeHostOptions.LogPrefix"/> once the host
    /// is initialized; a supervisor's health checks can read these lines.
    /// </summary>
    public static void Log(string category, string message) => Console.WriteLine($"{_logPrefix} {category} {message}");

    private static unsafe void RegisterNativeCallbacks(CoreFunctionHandler coreFunctions)
    {
        NWNCore.Init(coreFunctions);
        NWNXAPI.RegisterSignalHandler(&OnSignal);
        NWNXAPI.RegisterMainLoopHandler(&OnMainLoop);
        NWNXAPI.RegisterRunScriptHandler(&OnRunScript);
        NWNXAPI.RegisterClosureHandler(&OnClosure);
        ClosureStackRelease.Install();
    }

    [UnmanagedCallersOnly]
    private static int OnRunScript(IntPtr scriptPointer, uint objectSelf) => HandleRunScript(scriptPointer, objectSelf);

    /// <summary>
    /// The run-script callback: the mode's <see cref="IHostMode.OnRunScriptResult"/> for the script, or
    /// <see cref="ScriptNotHandled"/> when there is no script name, no mode, or the mode threw.
    /// </summary>
    internal static int HandleRunScript(IntPtr scriptPointer, uint objectSelf)
    {
        try
        {
            using var scope = _coreFunctions?.EnterObjectSelf(objectSelf);
            var script = Marshal.PtrToStringUTF8(scriptPointer);
            return script is not null && _mode is { } mode ? mode.OnRunScriptResult(script, objectSelf) : ScriptNotHandled;
        }
        catch (Exception exception)
        {
            ReportCallbackFailure("run-script", exception);
            return ScriptNotHandled;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnClosure(ulong eventId, uint objectSelf)
    {
        try
        {
            _coreFunctions?.Dispatch(eventId, objectSelf);
        }
        catch (Exception exception)
        {
            ReportCallbackFailure("closure", exception);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnSignal(IntPtr signalPointer)
    {
        try
        {
            HandleSignal(Marshal.PtrToStringAnsi(signalPointer));
        }
        catch (Exception exception)
        {
            ReportCallbackFailure("signal", exception);
        }
    }

    internal static void HandleSignal(string? signal)
    {
        Log("signal", signal ?? "<null>");
        if (signal == ModuleLoadFinishSignal)
        {
            foreach (var extension in _extensions)
            {
                extension.OnModuleLoaded();
            }
            Interlocked.Exchange(ref _moduleFinished, 1);
        }
        if (signal == DestroyServerSignal)
        {
            DestroyServer();
        }
        else if (signal is not null)
        {
            _mode?.OnSignal(signal);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnMainLoop(ulong frame) => HandleMainLoop(frame);

    /// <summary>
    /// The main-loop callback: every extension's <see cref="INativeHostExtension.BeginFrame"/>, the closure-stack release
    /// report, then the frame as <see cref="NativeHostOptions.FrameDelivery"/> says. A failure of an extension, of
    /// <see cref="IHostMode.BeforeFrame"/> or of scheduling the frame closure goes to
    /// <see cref="IHostMode.OnFrameScheduleFailure"/>; an inline frame's <see cref="IHostMode.RunFrame"/> failure goes to
    /// <see cref="IHostMode.OnCallbackFailure"/> with the phase <c>main-loop</c>, as a module-closure frame's goes there
    /// with the phase <c>closure</c>. Either way the next callback delivers the next frame.
    /// </summary>
    internal static void HandleMainLoop(ulong frame)
    {
        try
        {
            foreach (var extension in _extensions)
            {
                extension.BeginFrame();
            }
            ReportClosureStackReleaseFailures();
            var mode = _mode;
            var coreFunctions = _coreFunctions;
            if (mode is null || coreFunctions is null || Volatile.Read(ref _destroyed) != 0)
            {
                return;
            }
            if (_frameDelivery == FrameDelivery.Inline)
            {
                if (mode.BeforeFrame())
                {
                    RunInlineFrame(mode, coreFunctions, frame);
                }
                return;
            }
            if (Volatile.Read(ref _moduleFinished) == 0 || !mode.BeforeFrame())
            {
                return;
            }
            if (Interlocked.CompareExchange(ref _frameClosurePending, 1, 0) != 0)
            {
                return;
            }
            try
            {
                // The host owns the frame closure: dropping it with a retired generation would stop every later frame.
                coreFunctions.HostAssignCommand(ModuleObject, () =>
                {
                    try
                    {
                        mode.RunFrame(frame);
                    }
                    finally
                    {
                        Volatile.Write(ref _frameClosurePending, 0);
                    }
                });
            }
            catch
            {
                Volatile.Write(ref _frameClosurePending, 0);
                throw;
            }
        }
        catch (Exception exception)
        {
            if (_mode is { } mode)
            {
                mode.OnFrameScheduleFailure(exception);
            }
            else
            {
                Log("failure", $"main-loop-schedule: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    // The frame runs in the script context NWNX pushed for the main-loop callback, so OBJECT_SELF is OBJECT_INVALID
    // whatever scope the handler held, and a closure the frame schedules for OBJECT_SELF is refused as it was when the
    // server registered its own main-loop handler.
    private static void RunInlineFrame(IHostMode mode, CoreFunctionHandler coreFunctions, ulong frame)
    {
        var previous = coreFunctions.ExchangeObjectSelf(NWScript.OBJECT_INVALID);
        try
        {
            mode.RunFrame(frame);
        }
        catch (Exception exception)
        {
            ReportCallbackFailure(InlineFramePhase, exception);
        }
        finally
        {
            coreFunctions.ExchangeObjectSelf(previous);
        }
    }

    // Server destruction is terminal: the mode releases what it holds without waiting for gameplay
    // work, finalizers, or a later engine tick that may never arrive; the external supervisor owns
    // process exit.
    private static void DestroyServer()
    {
        Interlocked.Exchange(ref _destroyed, 1);
        _mode?.OnDestroyServer();
        _coreFunctions = null;
        Volatile.Write(ref _frameClosurePending, 0);
    }

    private static void ReportCallbackFailure(string phase, Exception exception)
    {
        if (_mode is { } mode)
        {
            mode.OnCallbackFailure(phase, exception);
        }
        else
        {
            Log("failure", $"{phase}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    // Logs the first closure stack the release hook could not free, then again each time the count doubles:
    // the hook runs inside native code and can only count, and a stuck failure would otherwise leak silently.
    private static void ReportClosureStackReleaseFailures()
    {
        var failures = ClosureStackRelease.Failures;
        if (failures < _nextClosureStackFailureReport)
        {
            return;
        }
        _nextClosureStackFailureReport = failures * 2;
        Log("closure-stack-release", $"failures={failures} released={ClosureStackRelease.Released}");
    }

    /// <summary>Returns every piece of process-wide state to its value before <see cref="Initialize(NativeHostOptions)"/>.</summary>
    internal static void ResetForTest()
    {
        _coreFunctions = null;
        _mode = null;
        _extensions = [];
        _logPrefix = DefaultLogPrefix;
        _frameDelivery = FrameDelivery.ModuleClosure;
        Volatile.Write(ref _bootstrapped, 0);
        Volatile.Write(ref _moduleFinished, 0);
        Volatile.Write(ref _destroyed, 0);
        Volatile.Write(ref _frameClosurePending, 0);
        _nextClosureStackFailureReport = 1;
    }
}
