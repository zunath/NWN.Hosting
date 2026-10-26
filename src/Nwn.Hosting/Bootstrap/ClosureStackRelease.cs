using System.Runtime.InteropServices;
using NWN.Native.API;
using NWNX.NET;
using NWNX.NET.Native;

namespace Nwn.Hosting.Bootstrap;

/// <summary>
/// Frees the VM stack NWNX DotNET allocates for every managed closure (<c>AssignCommand</c>,
/// <c>DelayCommand</c>, <c>ActionDoCommand</c>). The plugin's <c>CreateScriptForClosure</c> gives each
/// closure script a new <c>CVirtualMachineStack</c>, and its own <c>RunScriptSituation</c> hook deletes
/// only the script once the closure has run, so every closure would otherwise lose 48 bytes of native
/// heap. The host runs every main-loop frame through a module closure and the loop spins flat out while
/// a player is connected, which made that loss hundreds of thousands of stacks per second.
///
/// <para>
/// This hook sits outside the plugin's on the same function. For a closure script it detaches the stack
/// before the plugin runs the closure and deletes it afterwards; the plugin never reads a closure
/// script's stack. A plugin build that frees the stack itself then finds a null pointer, so a stack is
/// never deleted twice. Like any raw hook it touches only native structures, never NWScript.
/// </para>
/// </summary>
public static unsafe class ClosureStackRelease
{
    private const string RunScriptSituationSymbol = "_ZN15CVirtualMachine18RunScriptSituationEPvji";

    // The name NWNX DotNET's CreateScriptForClosure gives every closure script ("<prefix><event id>"),
    // the same text its own hook matches with sscanf.
    private static ReadOnlySpan<byte> ClosureScriptPrefix => "NWNX_DOTNET_INTERNAL "u8;

    private static FunctionHook* _hook;
    private static long _released;
    private static long _failures;

    /// <summary>The number of closure stacks freed since the hook was installed.</summary>
    public static long Released => Interlocked.Read(ref _released);

    /// <summary>The number of closure scripts whose stack could not be detached or freed.</summary>
    public static long Failures => Interlocked.Read(ref _failures);

    /// <summary>Installs the hook once per process. Throws when the engine symbol or the hook is unavailable.</summary>
    public static void Install()
    {
        if (_hook is not null)
        {
            return;
        }
        var address = NativeLibrary.GetExport(NativeLibrary.GetMainProgramHandle(), RunScriptSituationSymbol);
        delegate* unmanaged<void*, void*, uint, int, int> replacement = &OnRunScriptSituation;
        _hook = NWNXAPI.RequestFunctionHook(address, (IntPtr)replacement, HookOrder.Early);
        if (_hook is null)
        {
            throw new InvalidOperationException("NWNX rejected the closure stack release hook on CVirtualMachine::RunScriptSituation.");
        }
    }

    /// <summary>Whether a script situation's name is one NWNX DotNET gave a managed closure.</summary>
    internal static bool IsClosureScriptName(ReadOnlySpan<byte> name) => name.StartsWith(ClosureScriptPrefix);

    [UnmanagedCallersOnly]
    private static int OnRunScriptSituation(void* virtualMachine, void* scriptSituation, uint objectId, int objectValid)
    {
        var original = (delegate* unmanaged<void*, void*, uint, int, int>)_hook->m_trampoline;
        var stack = IntPtr.Zero;
        try
        {
            stack = DetachClosureStack(scriptSituation);
        }
        catch
        {
            Interlocked.Increment(ref _failures);
        }

        var result = original(virtualMachine, scriptSituation, objectId, objectValid);

        if (stack != IntPtr.Zero)
        {
            try
            {
                CVirtualMachineStack.FromPointer(stack, true).Dispose();
                Interlocked.Increment(ref _released);
            }
            catch
            {
                Interlocked.Increment(ref _failures);
            }
        }
        return result;
    }

    private static IntPtr DetachClosureStack(void* scriptSituation)
    {
        if (scriptSituation is null)
        {
            return IntPtr.Zero;
        }
        var script = CVirtualMachineScript.FromPointer(scriptSituation, false);
        var name = script.m_sScriptName;
        if (name.m_sString is null || !IsClosureScriptName(new ReadOnlySpan<byte>(name.m_sString, (int)name.m_nStringLength)))
        {
            return IntPtr.Zero;
        }
        var stack = script.m_pStack;
        if (stack is null)
        {
            return IntPtr.Zero;
        }
        script.m_pStack = null;
        return stack.Pointer;
    }
}
