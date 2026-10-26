# Nwn.Hosting.Testing

Test doubles and retired-generation assertions for code built on `Nwn.Hosting`. Targets `net10.0`. Depends on `Nwn.Hosting`; no test-framework dependency, so any framework can use it (assertion failures throw `HostingAssertionException`).

| Type | Role |
|---|---|
| `SingleThreadDispatcher` | A dedicated thread that stands in for the NWN engine thread. Like the engine thread it never exits, because a thread that ran generation code and exits makes the runtime free live objects. |
| `FixtureNativeBindings` | An `IPermanentNativeBindings` on that thread with callback registrations a test can queue and execute, so it can show a retired generation's callbacks never run. A fixture generation reaches the registrations through `IFixtureCallbackBindings`. |
| `RecordingNativeBindings` | Runs engine-thread work inline and records every `Report` call by category. |
| `TestObjectSelfScope` | Restores a stand-in `OBJECT_SELF` on dispose. |
| `VirtualDispatchCacheInspector` | Reads the runtime's generic-virtual-dispatch cache so a test can see whether its flush sentinel holds an entry. |
| `TestAssertions` | `Require`, `RequireThrows`, `RequireThrowsAsync` and `RequireNoRetainedContexts`, throwing `HostingAssertionException`. |

A fixture generation that implements `IFixtureCallbackBindings`-aware code must see the same type as the test, so the test host lists this assembly as shared when it creates the `ReloadHost`:

```csharp
var options = new GenerationLoadOptions(["Nwn.Hosting.Testing"]);
await using var host = new ReloadHost(native, loadOptions: options);
```

Part of the Nwn.Hosting packages (Nwn.Hosting.Contracts, Nwn.Hosting, Nwn.Hosting.Testing). Released as previews; APIs may change before 1.0.

Licensed under MIT.
