namespace Nwn.Hosting.Bootstrap;

/// <summary>
/// What <see cref="CoreFunctionHandler"/> does when the engine refuses an <c>AssignCommand</c>, <c>DelayCommand</c> or
/// <c>ActionDoCommand</c> closure (NWNX DotNET returns 0, for example because the target object is not valid); set
/// through <see cref="NativeHostOptions.RejectedClosures"/>.
/// </summary>
public enum RejectedClosureHandling
{
    /// <summary>The default: the closure is forgotten and the scheduling call throws <see cref="InvalidOperationException"/>.</summary>
    Throw = 0,

    /// <summary>
    /// The closure is forgotten without running and the scheduling call returns normally, as NWN.Core's own closure
    /// handling does: it holds no capacity, keeps no generation loaded, and only
    /// <see cref="CoreFunctionHandler.RejectedClosureCount"/> records it. The host's own frame closure still throws.
    /// </summary>
    Drop = 1,
}
