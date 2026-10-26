using System.Reflection;

namespace Nwn.Hosting.Testing.Support;

/// <summary>
/// Reads the runtime's generic-virtual-dispatch cache (<c>VirtualDispatchHelpers.s_virtualFunctionPointerCache</c>
/// in System.Private.CoreLib) independently of the host's own code, so a test can see whether its flush
/// sentinel holds a dispatch entry. A correct runtime never stores one there; .NET 10 before 10.0.13 does,
/// keeps it through every later flush, and returns its code address for a later generation whose types
/// reuse the unloaded generation's addresses (docs/generic-virtual-dispatch.md in the Nwn.Hosting
/// repository).
/// </summary>
public static class VirtualDispatchCacheInspector
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    /// <summary>Whether this runtime's cache has the shape the inspector reads.</summary>
    public static bool IsReadable => TryReadSentinel(out _, out _);

    /// <summary>The sentinel entries that hold a dispatch key: zero on a runtime, or in a process, that is safe.</summary>
    public static int StoredSentinelEntries()
    {
        if (!TryReadSentinel(out var sentinel, out var key))
        {
            throw new InvalidOperationException("This runtime's generic-virtual-dispatch cache does not have the shape the inspector reads.");
        }

        var empty = Activator.CreateInstance(key.FieldType);
        var stored = 0;
        // Element 0 holds the table's own metadata, not an entry.
        for (var index = 1; index < sentinel.Length; index++)
        {
            if (!Equals(key.GetValue(sentinel.GetValue(index)), empty)) stored++;
        }

        return stored;
    }

    private static bool TryReadSentinel(out Array sentinel, out FieldInfo key)
    {
        sentinel = Array.Empty<object>();
        key = null!;
        var helpers = typeof(object).Assembly.GetType("System.Runtime.CompilerServices.VirtualDispatchHelpers");
        var cacheField = helpers?.GetField("s_virtualFunctionPointerCache", Members);
        var sentinelField = cacheField?.FieldType.GetField("_sentinelTable", Members);
        if (cacheField is null || sentinelField?.GetValue(cacheField.GetValue(null)) is not Array table) return false;
        var keyField = table.GetType().GetElementType()?.GetField("_key", Members);
        if (keyField is null) return false;
        sentinel = table;
        key = keyField;
        return true;
    }
}
