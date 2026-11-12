namespace Nwn.Hosting.Bootstrap;

/// <summary>How <see cref="NativeHost"/> hands each NWNX main-loop callback to the mode (<see cref="NativeHostOptions.FrameDelivery"/>).</summary>
public enum FrameDelivery
{
    /// <summary>
    /// The default. Once the module has finished loading (<c>ON_MODULE_LOAD_FINISH</c>), each callback schedules a closure
    /// assigned to the module that calls <see cref="IHostMode.RunFrame"/>; the engine runs it later in the same loop
    /// iteration, during the AI master's event pass, with the module as <c>OBJECT_SELF</c>. At most one frame closure is
    /// pending at a time, and it belongs to the host, so retiring a generation never stops the frames.
    /// </summary>
    ModuleClosure = 0,

    /// <summary>
    /// Every callback, from the first one and before the module has loaded, calls <see cref="IHostMode.BeforeFrame"/> and
    /// <see cref="IHostMode.RunFrame"/> directly inside the NWNX main-loop callback, at the start of the engine's main loop,
    /// in the script context NWNX pushes for it (<c>OBJECT_SELF</c> is <c>OBJECT_INVALID</c>). No closure is scheduled.
    /// This is where a server that registered its own main-loop handler with NWNX DotNET ran its frame work.
    /// </summary>
    Inline = 1,
}
