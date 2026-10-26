using System.Reflection;

namespace Nwn.Hosting.Reload;

/// <summary>
/// Makes the runtime's generic-virtual-dispatch cache safe across generations on .NET 10 before 10.0.13
/// (dotnet/runtime#132562; docs/generic-virtual-dispatch.md in the Nwn.Hosting repository).
/// <para>
/// CoreLib caches each resolved generic virtual call (<c>VirtualDispatchHelpers.s_virtualFunctionPointerCache</c>,
/// keyed by the raw addresses of the object's type, the declaring type and the instantiated method), and
/// unloading any collectible context flushes it by swapping in a shared two-entry sentinel table. The
/// affected runtimes test for the sentinel with the wrong length, so the first resolutions after a flush are
/// stored in the sentinel itself and survive every later flush. Once that generation unloads, a later one
/// whose types and methods land at the same addresses is handed the unloaded code: the call hangs, faults or
/// throws <see cref="ExecutionEngineException"/>. Generation code makes such calls constantly (LINQ's
/// <c>Select</c> and <c>Where</c> are generic virtual methods).
/// </para>
/// <para>
/// <see cref="Seal"/> fills both sentinel entries with a key no lookup can match, marked as complete entries
/// at their home slot, which the cache's insertion never evicts. The sentinel then behaves as on a fixed
/// runtime: a lookup misses and an insertion grows a real table. It runs once per process, when the first
/// <see cref="ReloadHost"/> is created, before any generation is loaded; on any other runtime it does nothing.
/// </para>
/// </summary>
public static class VirtualDispatchCacheSentinel
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    /// <summary>A completed entry (even version) at distance 0 from its home slot, which insertion never replaces.</summary>
    private const uint SealedVersion = 2;

    private static readonly Lock Gate = new();
    private static bool _sealed;

    /// <summary>Whether <paramref name="runtime"/> stores dispatch entries in the cache's flush sentinel.</summary>
    public static bool IsAffected(Version runtime) =>
        runtime.Major == 10 && runtime.Minor == 0 && runtime.Build is >= 0 and < 13;

    public static void Seal()
    {
        if (!IsAffected(Environment.Version)) return;
        lock (Gate)
        {
            if (_sealed) return;
            var sentinel = FindSentinel(out var info, out var version, out var key) ?? throw new InvalidOperationException(
                $"Runtime {Environment.Version} stores generic virtual dispatch entries in its cache's flush sentinel, and its " +
                "cache does not have the shape the host seals; hot reload would dispatch into unloaded generations " +
                "(docs/generic-virtual-dispatch.md in the Nwn.Hosting repository).");

            var unmatchableKey = Activator.CreateInstance(key.FieldType);
            // Element 0 holds the table's own metadata, not an entry.
            for (var index = 1; index < sentinel.Length; index++)
            {
                var entry = sentinel.GetValue(index)!;
                var entryInfo = info.GetValue(entry)!;
                version.SetValue(entryInfo, SealedVersion);
                info.SetValue(entry, entryInfo);
                key.SetValue(entry, unmatchableKey);
                sentinel.SetValue(entry, index);
            }

            _sealed = true;
        }
    }

    /// <summary>
    /// The sentinel table and the entry members <see cref="Seal"/> writes: the version, and the key whose
    /// default (a null object type) no lookup can match, since every lookup is keyed by a live object's type.
    /// </summary>
    private static Array? FindSentinel(out FieldInfo info, out FieldInfo version, out FieldInfo key)
    {
        info = version = key = null!;
        var helpers = typeof(object).Assembly.GetType("System.Runtime.CompilerServices.VirtualDispatchHelpers");
        var cacheField = helpers?.GetField("s_virtualFunctionPointerCache", Members);
        var sentinelField = cacheField?.FieldType.GetField("_sentinelTable", Members);
        if (cacheField is null || sentinelField?.GetValue(cacheField.GetValue(null)) is not Array sentinel) return null;
        var entryType = sentinel.GetType().GetElementType();
        var infoField = entryType?.GetField("_info", Members);
        var versionField = infoField?.FieldType.GetField("_version", Members);
        var keyField = entryType?.GetField("_key", Members);
        if (infoField is null || versionField?.FieldType != typeof(uint) || keyField is null || !keyField.FieldType.IsValueType) return null;
        info = infoField;
        version = versionField;
        key = keyField;
        return sentinel;
    }
}
