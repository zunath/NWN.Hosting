# Generation leaks

A generation unloads only when nothing outside its collectible context still references anything inside it.
The host and the process-lifetime services it shares (logger, serializer defaults, connection pools) live
outside the context, so any path from one of them into generation code pins the whole generation and
defeats unload. Two such paths have been found in real gameplay code; both are easy to introduce and
neither shows up until a reload proves the retired context is still alive.

The proof that catches them is a many-generation reload test that probes every retired context after the
reload call has returned: `ReloadHost.ProbeRetiredContextsAsync` reports how many contexts it collected and
how many stayed rooted, and `TestAssertions.RequireNoRetainedContexts` in `Nwn.Hosting.Testing` fails the
test when any did. Run it against the real gameplay assembly with every plugin loaded, not only against a
small fixture.

## A logged exception object

**The mechanism.** Logging a raw `Exception` OBJECT (not its message) through a logger the host owns hands
that root-owned logger a reference whose stack trace touches types from the collectible assembly. ANY sink
attached to that logger that retains the log event (an in-memory sink kept for test assertions, a
buffering or batching sink, anything that does not immediately discard the event) transitively roots the
assembly's `LoaderAllocator` for as long as it keeps that one event, which pins the WHOLE generation.

**The rule for every generation.** Never pass an exception object to a logger the host keeps around across
generations, which is every sink, since sinks are wired once, at host startup, outside any generation's own
lifetime. Log the exception's type name and message as plain structured arguments instead; a stack trace is
not needed to diagnose a plugin-level failure the way it might be for a genuine host crash. A service that
cannot do its work because an optional dependency is absent should check for that dependency before the
call and log a plain warning once per generation, instead of letting the failure surface as an exception
object. A recording sink that asserts every captured event's exception is null guards the rule.

(`ReloadHost` itself reports a failed script dispatch, main-loop tick or activation to the native
bindings as text through `IPermanentNativeBindings.Report`, never as an exception object. A binding that
stores those strings does not root a generation.)

## The default JSON options

**The mechanism.** `JsonSerializerOptions.Default` belongs to the non-collectible System.Text.Json assembly
and caches the metadata (`JsonTypeInfo`, converters, member accessors) of every type it serializes for the
life of the process. A call on the default options with a type from the collectible generation context (a
gameplay type, a plugin payload, an anonymous type in plugin code) therefore roots that context. An options
instance whose settings equal the default shares the same cache, so `new JsonSerializerOptions()` is no
escape either.

**The rule for every generation.** Give the generation its own options that are unequal to the default, for
example by setting a `TypeInfoResolver` of its own (`new DefaultJsonTypeInfoResolver()`), held in a static
of the generation's own assembly so its cache is collected with the generation, and pass them to every
`JsonSerializer.Serialize` and `Deserialize` call. Never call `JsonSerializer` on the default options from
generation code. A guard test that refuses any call naming no options keeps the rule from eroding.

## Related

- `threads-outlive-generations.md`: thread statics of collectible types and the keep-alive rule.
- `generic-virtual-dispatch.md`: the runtime cache that can hand a later generation an unloaded one's code.
