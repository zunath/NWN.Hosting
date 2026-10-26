namespace Nwn.Hosting.Reload.DispatchGameplay;

/// <summary>Declares the generic virtual method whose resolved calls the runtime caches by raw type and method addresses.</summary>
public abstract class EchoBase
{
    public abstract T Echo<T>(T value);
}
