# Generic virtual dispatch across generations

On .NET 10 before 10.0.13 a generation can be dispatched into the code of an unloaded one. The process
hangs with a thread spinning on one core, dies with `Fatal error. 0xC0000005`, or a generation's
activation or validation fails with an `ExecutionEngineException` or a `NullReferenceException`
("Candidate validation failed; the running generation was left active").

## The runtime defect

dotnet/runtime#132562, fixed by #133184 for 10.0.13. CoreLib caches every resolved generic virtual call in
`VirtualDispatchHelpers.s_virtualFunctionPointerCache`, a `GenericCache<VirtualResolutionData, IntPtr>` keyed
by the raw addresses of the object's type, the declaring type and the instantiated method. Unloading any
collectible context flushes it by swapping in a shared two-entry sentinel table. `GenericCache.TrySet`
recognises the sentinel by `table.Length == 2`, but the sentinel array has three elements (element 0 holds
metadata), so the first resolutions after a flush are stored in the sentinel itself and survive every later
flush. Once their generation unloads, a later generation whose types and method instantiations land at the
same addresses is handed the unloaded code address.

Generation code makes generic virtual calls constantly: LINQ's `Select` and `Where` are generic virtual
methods on its iterators, which is where a generation's service registration faulted in the crash dump that
found this, with `GenericCache<VirtualResolutionData, IntPtr>` on the faulting stack.

## Evidence (2026-10-05, runtime 10.0.12, Windows)

- A dependency-free program that loads one generic-virtual override into a new collectible context per
  cycle, calls it, unloads and collects the context hung at cycle 3 in 3 of 3 runs, one cycle after the
  sentinel first held an entry. With the sentinel sealed as below it completed 100 cycles in 3 of 3 runs
  with the sentinel empty throughout.
- A reload test that loads a full gameplay assembly with its plugins, collecting each retired generation
  before the next runs, found the sentinel holding 2 entries from cycle 3 on, in 3 of 3 runs, without the
  seal; with it, 0 entries in every cycle of all 47 runs since.
- A gate that merely reloads many generations failed rarely and depended on address reuse, so the
  sentinel check is the deterministic one. Earlier measurements saw the gate fail in about one run in
  ten under `DOTNET_TC_BackgroundWorkerTimeoutMs=0` (the tiered-compilation worker exits whenever idle)
  or under heavy machine load. How the worker's exits raise the rate of address reuse is not established.

## What the packages do

`ReloadHost` seals the sentinel when the first one is created in a process, before any generation is loaded
(`VirtualDispatchCacheSentinel`): both sentinel entries get a key no lookup can match (a null object type)
and a completed version at their home slot, which the cache's insertion never evicts. The sentinel then
behaves as on a fixed runtime: a lookup misses and an insertion grows a real table. It is applied only on
10.0.0 to 10.0.12; on an affected runtime whose cache has another shape `ReloadHost` refuses to start.

When the pinned runtime moves to 10.0.13 or later the seal does nothing and can be deleted with its
`IsAffected` check. `VirtualDispatchCacheSentinelTests` keeps checking the sentinel on the new runtime:
it reloads a fixture generation that makes generic virtual calls through 30 collectible contexts and reads
the sentinel with `VirtualDispatchCacheInspector` (in `Nwn.Hosting.Testing`) after each context is
collected. A consumer that reloads a larger gameplay assembly should run the same loop against it.
