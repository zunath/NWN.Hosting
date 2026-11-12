# Nwn.Hosting

A permanent NWNX DotNET host that hot reloads gameplay assemblies without restarting the Neverwinter Nights server. Targets `net10.0`. Depends on `Nwn.Hosting.Contracts`, `NWN.Core`, `NWN.Native` and `NWNX.NET`.

The host stays loaded for the life of the process. Each *generation* of gameplay code is an assembly loaded into its own collectible `AssemblyLoadContext`; a reload validates the candidate first, quiesces and drains the running generation, unloads it, activates the candidate and probes that the old context was collected.

| Area | Types |
|---|---|
| `Nwn.Hosting.Bootstrap` | `NativeHost` registers the NWNX callbacks, returns the mode's script result to the engine (`IHostMode.OnRunScriptResult`, so conditional scripts work) and delivers every main-loop frame to the mode: inside a module closure once the module has loaded (the default), or with `NativeHostOptions.FrameDelivery = FrameDelivery.Inline` directly inside the NWNX main-loop callback from the first frame, with `OBJECT_SELF` invalid and no closure. `NativeHostOptions` and `INativeHostExtension` plug a consumer's mode, native hooks, closure limit (`MaximumPendingClosures`, 64 by default, null for none) and rejected-closure handling (`RejectedClosures`: `RejectedClosureHandling.Throw` by default, or `Drop` to forget a closure NWNX refuses without throwing, as NWN.Core does) in. `CoreFunctionHandler` routes NWN.Core closures and tags each with the generation that scheduled it, so a retired generation's pending closures can be dropped (`RetireGeneration`); it counts refused closures in `RejectedClosureCount`. `INativeClosureScheduler` lets a test replace NWNX. `NativeBindings` is the bounded engine-thread queue, `ClosureStackRelease` frees the VM stack NWNX allocates for each closure. |
| `Nwn.Hosting.Reload` | `ReloadHost` is the reload state machine; given the host's `CoreFunctionHandler` it drops each retired generation's pending closures, and `DispatchScriptResult` gives a mode the active generation's script result. `LoadedGeneration` and `GenerationLoadOptions` load a generation from bytes into a collectible context (or, with `Collectible = false`, one non-collectible build for a server that never reloads) and name the assemblies it shares with the host. `ThreadPoolKeepAlive` and `VirtualDispatchCacheSentinel` guard two .NET runtime defects that corrupt a process which unloads collectible code. |
| `Nwn.Hosting.Development` | `GameReloadSignalWatcher` polls a signal file and `GameDevelopmentReloadHandler` turns a typed request into a reload and a receipt. |

```csharp
using Nwn.Hosting.Bootstrap;

public static class Entry
{
    // The method NWNX DotNET is configured to call.
    public static void Initialize() =>
        NativeHost.Initialize(new NativeHostOptions(
            logPrefix: "MYSERVER_NATIVE",
            modeName: "game",
            createMode: coreFunctions => new MyHostMode(coreFunctions),
            extensions: [new MyNativeHook()]));
}
```

A process that hosts `ReloadHost` must keep every thread-pool worker alive: set `System.Threading.ThreadPool.ThreadsToKeepAlive` to `-1` in the host project (`<RuntimeHostConfigurationOption Include="System.Threading.ThreadPool.ThreadsToKeepAlive" Value="-1" />`). `ReloadHost` refuses to start otherwise. The reasons, and the rules for gameplay code that must stay unloadable, are in the `docs` folder of the repository: `threads-outlive-generations.md`, `generic-virtual-dispatch.md` and `generation-leaks.md`. `adopting.md` describes moving an existing server onto the packages, including frame delivery, script results, closures (and refused ones) and a single non-collectible build.

Part of the Nwn.Hosting packages (Nwn.Hosting.Contracts, Nwn.Hosting, Nwn.Hosting.Testing). Released as previews; APIs may change before 1.0.

Licensed under MIT.
