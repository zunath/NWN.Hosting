# Adopting the packages in a server

How a Neverwinter Nights server built on NWNX DotNET moves onto `Nwn.Hosting`. The package READMEs
describe each package; this guide covers the decisions a consumer makes.

## The host application

NWNX DotNET loads one assembly and calls one parameterless static method. That assembly stays the
consumer's own small host project; `NativeHost.Initialize` takes options and cannot be the entry point
itself.

- The entry method reads whatever setting selects the consumer's mode and calls
  `NativeHost.Initialize(new NativeHostOptions(logPrefix, modeName, createMode, extensions))`. A failure
  before that call (for example an invalid mode setting) is the consumer's to log; `NativeHost.Log` uses
  the prefix `NWN_HOSTING_NATIVE` until `Initialize` has run.
- `logPrefix` is the first word of every console line the host writes. Keep the prefix any existing
  health check matches.
- The host project keeps the application settings a library cannot set: `EnableDynamicLoading`,
  `GenerateRuntimeConfigurationFiles`, the runtime identifier, and
  `<RuntimeHostConfigurationOption Include="System.Threading.ThreadPool.ThreadsToKeepAlive" Value="-1" />`
  (see [threads-outlive-generations.md](threads-outlive-generations.md)).

## Native hooks

A consumer's own NWNX function hooks stay in its host project and plug in as `INativeHostExtension`s.
`NativeHost` calls them in list order:

- `Install` once during `Initialize`, after the NWNX callbacks and closure-stack release are registered
  and before the mode starts.
- `BeginFrame` on every main-loop callback, before the host schedules the frame.
- `OnModuleLoaded` on `ON_MODULE_LOAD_FINISH`, before the module counts as loaded.

A hook installed this way lives for the whole process. Native code holds its function pointer, so its
code cannot be reloaded; a hook whose behaviour must change with gameplay code forwards to the active
generation through a contract and keeps the engine's own behaviour when no generation answers.

## Loading generations

`ReloadHost` loads each generation into a collectible context. `NWN.Core`, `NWN.Native`, `NWNX.NET`
and `Nwn.Hosting.Contracts` are always shared with the host. Pass every other assembly whose state or
types must be the host's in `GenerationLoadOptions.SharedAssemblyNames`:

- the consumer's own contracts assembly (types handed across the boundary through `IHostServices`);
- a logging library whose root logger the host hands to generations;
- any engine wrapper that keeps static state bound to the initialized engine;
- a persistence layer whose connections live for the process.

A shared assembly must never reference a generation's types; anything that does keeps the retired
generation alive ([generation-leaks.md](generation-leaks.md)). A dependency-injection container is the
opposite case: load it inside each generation so its caches are collected with it.

Every `ReloadHost` and `LoadedGeneration.Load` that loads the real gameplay assembly, in tests as well,
passes the same options. A missing name does not fail loudly; the generation silently gets its own copy.

## Development reload

`GameReloadSignalWatcher` polls a signal file; `GameDevelopmentReloadHandler` turns a request in it into a
reload and writes a receipt. A typed request selects the candidate
`<generation root>/generations/<generation id, 32 hex digits>/<game assembly file name>`. The consumer
names its gameplay assembly file (`gameAssemblyFileName`); the request file carries only the format
version and the generation id. A signal file without a typed request reloads the configured legacy path.

## Tests

`Nwn.Hosting.Testing` holds the doubles the host's own tests use: a parking single-thread dispatcher,
recording native bindings, `FixtureNativeBindings` with `IFixtureCallbackBindings` for fixture
generations, an object-self scope, the virtual-dispatch-cache inspector and retired-context assertions.
A fixture generation that uses `IFixtureCallbackBindings` must see the test's copy of that type, so a test
that loads one lists `Nwn.Hosting.Testing` in its `SharedAssemblyNames`.

Keep the tests that need the consumer's real gameplay assembly in the consumer: collectibility of every
real generation, plugin discovery, symbol loading and the real-assembly dispatch-cache check. They are
the acceptance gate for the consumer; this repository's tests cover the host with small fixtures.

On a runtime that already carries the dispatch-cache fix (.NET 10.0.13 and later) the dispatch-cache test
cannot show the seal working; it still proves the fixture exercises the cache and every context is
collected. Run it on an affected runtime (10.0.0 to 10.0.12) for the deterministic check
([generic-virtual-dispatch.md](generic-virtual-dispatch.md)).
