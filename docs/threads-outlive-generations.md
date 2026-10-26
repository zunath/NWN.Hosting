# Threads outlive generations

No thread that runs generation code may exit while the process runs. A thread that exits after a
reload makes the .NET 10 runtime free objects the live generation still uses, and the process later
dies reading freed memory: "Fatal error. Internal CLR error. (0x80131506)", a bare `0xC0000005`, an
`AccessViolationException`, or a managed `NullReferenceException`/`EntryPointNotFoundException` during
a generation's activation. In a reload test suite that unloaded dozens of generations the crash aborted
about one run in three, always in the test that loaded the most gameplay code, and once an in-server
database test run on Linux.

## The runtime defect

Found in `src/coreclr/vm/threadstatics.cpp`, unchanged on dotnet/runtime `main` as of 2026-09-25:

1. A thread reads a thread static of a collectible type. The runtime gives the type a TLS index and
   records, in the thread's `pLoaderHandles[index]`, a handle into that type's loader allocator which
   keeps the thread's storage alive.
2. The generation unloads. `FreeTLSIndicesForLoaderAllocator` marks its indices free in the global
   index map but leaves every thread's `pLoaderHandles` entry in place.
3. A later generation's collectible type reuses the free index (`FindClearedIndex`).
4. The thread exits. `FreeLoaderAllocatorHandlesForTLSData` frees each of the thread's handles in the
   loader allocator of the type that holds the index *now*, the live generation. The stale handle
   names a slot of the old allocator's table, so an unrelated slot of the live generation is released:
   a cached `RuntimeType`, a statics array, whatever it held.

Generation code has collectible thread statics without declaring any: `ArrayPool<T>.Shared` over a
generation type carries one, and LINQ's `ToArray` reaches it. The thread that exits is usually an idle
thread-pool worker retiring after 20 seconds, so the crash depends on timing, grows with the amount of
generation code, and never appears in a loop that keeps the pool busy.

## Evidence

A crash dump of an aborted reload test run (`DOTNET_DbgEnableMiniDump`, full dump) showed the fault in
`RuntimeTypeHandle::GetAttributes` while a generation's composition root filtered a plugin assembly's
types. Of the 8,833 types with a cached `RuntimeType` across every module in the process, exactly one,
``<>z__ReadOnlyList`1``, pointed at freed memory; no live `RuntimeType` for it existed, its loader
allocator slot had been reused for an unrelated statics array, and the process held a dead thread-pool
worker.

A short program with no hosting code reproduces it every time: a dedicated thread touches a
`[ThreadStatic]` of a type in collectible context A; A is unloaded and collected; a type in context B
touches its own `[ThreadStatic]` (reusing the index); the thread exits; one of B's `RuntimeType`s is
then collected while B is alive, and reading it ends in `Fatal error. 0xC0000005`. With sixteen
thread-pool workers touching A's thread static instead, every worker that retires releases one of B's
objects (16 per round); with `ThreadsToKeepAlive` at -1 none is released.

Unloading while a retired generation's frames are still on the stack was measured and is not the cause.
dotnet/runtime#132562 is not the cause of this crash either, but it is a second, independent defect of
the same runtime (see `generic-virtual-dispatch.md`).

## What the packages do

- A hosting application sets `System.Threading.ThreadPool.ThreadsToKeepAlive` to -1 in its runtime
  configuration so thread-pool workers never retire:

  ```xml
  <RuntimeHostConfigurationOption Include="System.Threading.ThreadPool.ThreadsToKeepAlive" Value="-1" />
  ```

  NWNX starts the server's runtime from the host application's `*.runtimeconfig.json`, so the option
  belongs in the project that NWNX loads. A test project that reloads generations sets it too.
- `ReloadHost` refuses to start in a process that lets workers retire (`ThreadPoolKeepAlive.Require`).
  The environment variable `DOTNET_ThreadPool_ThreadsToKeepAlive` is read before the configuration
  property, as the runtime does.
- Generation code runs only on the engine thread, which never exits, and the thread pool. A managed
  stand-in for the engine thread (`SingleThreadDispatcher` in `Nwn.Hosting.Testing`) parks instead of
  exiting once it is disposed, and a test runner's own thread lives for the whole test session.
- Nothing may start a dedicated thread or `LongRunning` task that runs generation code.

The keep-alive stays until the runtime frees a thread's stale thread-static handles correctly; check
`FreeLoaderAllocatorHandlesForTLSData` when the pinned runtime moves.
