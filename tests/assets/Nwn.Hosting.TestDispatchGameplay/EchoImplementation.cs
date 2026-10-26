namespace Nwn.Hosting.Reload.DispatchGameplay;

/// <summary>The one generic virtual override; a call through <see cref="EchoBase"/> dispatches here.</summary>
public sealed class EchoImplementation : EchoBase
{
    public override T Echo<T>(T value) => value;
}
