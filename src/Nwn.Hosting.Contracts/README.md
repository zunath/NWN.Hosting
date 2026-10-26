# Nwn.Hosting.Contracts

The boundary between a permanent NWNX DotNET host and the gameplay generations it hot reloads. Targets `net10.0`. No package dependencies.

A *generation* is one gameplay assembly that the host loads into its own collectible `AssemblyLoadContext`. The host stays loaded for the life of the server process; a generation is validated, activated, quiesced, drained and unloaded as a unit. This package holds the types both sides of that boundary must agree on, so a generation compiles against it and the host (`Nwn.Hosting`) loads it by name into the shared context.

| Type | Role |
|---|---|
| `IGameplayGeneration` | The one concrete entry point in a reloadable assembly: validate, activate, quiesce, drain, dispose. |
| `GenerationActivation` | What the host hands a generation on activation: its generation number, the engine-thread seam, liveness, execution tracking and host services. |
| `IPermanentNativeBindings` | The engine-thread seam: run work on the NWN engine thread, report a log line. |
| `IGenerationLiveness` / `IGenerationExecution` | Is this generation still the active one, and how many callback bodies are still running. |
| `IScriptDispatcher` / `IMainLoopSink` | Optional per-generation RunScript and main-loop hooks the host forwards to the active generation. |
| `IHostServices` | Type-keyed lookup for process-lifetime singletons the host shares with every generation. |
| `ServerInstanceId` | A typed identity for the server process, resolved through `IHostServices`. |
| `DevelopmentReloadFiles`, `DevelopmentReloadRequest`, `DevelopmentReloadResult`, `DevelopmentReloadStatus` | The bounded file protocol between a local publisher and the host's development reload watcher. |

Values crossing the boundary are primitives, strings or host-owned services, never gameplay object graphs. That keeps a retired generation unrooted so its context can be collected; see the leak rules in `docs/generation-leaks.md` of the repository.

Part of the Nwn.Hosting packages (Nwn.Hosting.Contracts, Nwn.Hosting, Nwn.Hosting.Testing). Released as previews; APIs may change before 1.0.

Licensed under MIT.
