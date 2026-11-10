using System.Reflection;
using System.Runtime.CompilerServices;
using Nwn.Hosting.Bootstrap;
using Nwn.Hosting.Contracts.Generations;

namespace Nwn.Hosting.Reload;

/// <summary>
/// One gameplay assembly loaded into its own load context, with the tracking a reload needs to retire it. The context
/// is collectible unless <see cref="GenerationLoadOptions.Collectible"/> is false.
/// </summary>
public sealed class LoadedGeneration
{
    private CollectibleGameplayLoadContext? _context;
    private IGameplayGeneration? _gameplay;

    private LoadedGeneration(CollectibleGameplayLoadContext context, IGameplayGeneration gameplay, long generation)
    {
        _context = context;
        _gameplay = gameplay;
        Generation = generation;
        IsCollectible = context.IsCollectible;
        Execution = new GenerationExecution();
    }

    public long Generation { get; }

    /// <summary>Whether the generation's context can be unloaded; false when it was loaded with <see cref="GenerationLoadOptions.Collectible"/> false.</summary>
    public bool IsCollectible { get; }
    public IGenerationExecution Execution { get; }
    public IGameplayGeneration Gameplay => _gameplay ?? throw new ObjectDisposedException(nameof(LoadedGeneration));

    public static LoadedGeneration Load(string assemblyPath, long generation, GenerationLoadOptions? loadOptions = null)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var context = new CollectibleGameplayLoadContext(fullPath, loadOptions ?? GenerationLoadOptions.Default);
        try
        {
            // NEVER context.LoadFromAssemblyPath(fullPath) here: CoreCLR's assembly binder caches a
            // loaded PE image by PATH for the lifetime of the PROCESS, independent of which
            // AssemblyLoadContext requests it and independent of the file's actual bytes/inode on
            // disk. The documented, intended reload workflow is "publish an updated gameplay
            // assembly to the SAME path, then touch the reload signal file" - reloading the SAME path
            // by-path would therefore silently keep activating the FIRST generation's bytecode forever
            // after the very first activation, no matter how many times a real new build is published,
            // defeating hot reload entirely. A real published gameplay assembly carrying a different
            // assembly version reproduces this: same-path reloads keep
            // reporting the original generation's build id; reloading from a genuinely different path
            // picks up the new one immediately. Reading the bytes ourselves and loading from a
            // stream never associates the resulting Assembly with a file path at all, so there is
            // nothing left for that cache to key on - the standard fix for this exact, known .NET
            // assembly-reload hazard.
            using var stream = new MemoryStream(File.ReadAllBytes(fullPath));
            // A PDB next to the DLL is optional (a Release publish with no debug symbols is a
            // normal, supported deployment) -- when present, load it from a stream for the exact
            // same reason the assembly itself is stream-loaded above: a stream-loaded PDB is
            // never associated with a file path either, so it cannot reintroduce the by-path
            // caching hazard this method's existing comment already documents.
            var symbolPath = Path.ChangeExtension(fullPath, ".pdb");
            using var symbolStream = File.Exists(symbolPath)
                ? new MemoryStream(File.ReadAllBytes(symbolPath))
                : null;
            var assembly = symbolStream is null
                ? context.LoadFromStream(stream)
                : context.LoadFromStream(stream, symbolStream);
            var type = FindGameplayEntryType(assembly);
            var gameplay = (IGameplayGeneration)Activator.CreateInstance(type)!;
            gameplay.ConfigureSourceDirectory(Path.GetDirectoryName(fullPath)!);
            return new LoadedGeneration(context, gameplay, generation);
        }
        catch
        {
            if (context.IsCollectible)
            {
                context.Unload();
            }
            throw;
        }
    }

    /// <summary>
    /// Every reloadable payload (test fixtures and real gameplay assemblies alike) is
    /// found by interface rather than a fixed type name, so the entry point can live in any
    /// namespace.
    /// </summary>
    private static Type FindGameplayEntryType(Assembly assembly)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types;
        }

        var candidates = types
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IGameplayGeneration).IsAssignableFrom(type))
            .Select(type => type!)
            .ToArray();

        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException(
                $"{assembly.GetName().Name} does not define a concrete {nameof(IGameplayGeneration)} implementation."),
            _ => throw new InvalidOperationException(
                $"{assembly.GetName().Name} defines {candidates.Length} concrete {nameof(IGameplayGeneration)} implementations; exactly one is required."),
        };
    }

    /// <summary>
    /// Disposes the generation on the engine thread and unloads its context; returns a weak reference that dies once
    /// the context is collected. A non-collectible context is never unloaded, so its reference stays alive.
    /// </summary>
    public ValueTask<WeakReference> DisposeAndUnloadAsync(IPermanentNativeBindings native) => DisposeAndUnloadAsync(native, closures: null);

    /// <summary>
    /// <see cref="DisposeAndUnloadAsync(IPermanentNativeBindings)"/> that also retires the generation's closures in
    /// <paramref name="closures"/> on the engine thread, in the same engine-thread call as the dispose, so none of its
    /// pending closures can run between the dispose and the drop.
    /// </summary>
    internal async ValueTask<WeakReference> DisposeAndUnloadAsync(IPermanentNativeBindings native, CoreFunctionHandler? closures)
    {
        if (_gameplay is { } gameplay)
        {
            await native.InvokeOnEngineThreadAsync(() =>
            {
                try
                {
                    return gameplay.DisposeAsync();
                }
                finally
                {
                    RetireClosures(native, closures);
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }

        _gameplay = null;
        var context = _context ?? throw new ObjectDisposedException(nameof(LoadedGeneration));
        _context = null;
        var weak = new WeakReference(context, trackResurrection: false);
        if (context.IsCollectible)
        {
            context.Unload();
        }
        return weak;
    }

    private void RetireClosures(IPermanentNativeBindings native, CoreFunctionHandler? closures)
    {
        if (closures is null)
        {
            return;
        }
        var dropped = closures.RetireGeneration(Generation);
        if (dropped > 0)
        {
            native.Report("closures-dropped", $"generation={Generation} dropped={dropped}");
        }
    }

    public static async ValueTask<bool> ProbeCollectedAsync(WeakReference context, Action<string>? diagnostic = null)
    {
        // This intentionally runs away from an engine tick. WaitForPendingFinalizers can block and
        // neither this test helper nor a deadline turns that blocking wait into a latency guarantee.
        return await Task.Run(() => ProbeCollected(context, diagnostic)).ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ProbeCollected(WeakReference context, Action<string>? diagnostic)
    {
        for (var attempt = 0; context.IsAlive && attempt < 10; attempt++)
        {
            diagnostic?.Invoke($"collect attempt={attempt + 1} before-gc");
            GC.Collect();
            diagnostic?.Invoke($"collect attempt={attempt + 1} before-finalizers");
            GC.WaitForPendingFinalizers();
            diagnostic?.Invoke($"collect attempt={attempt + 1} after-finalizers");
            GC.Collect();
        }

        return !context.IsAlive;
    }
}
