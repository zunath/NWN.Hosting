namespace Nwn.Hosting.Testing.Fixtures;

/// <summary>
/// Native callback registrations a managed reload proof hands its fixture generations by implementing
/// this beside <see cref="Nwn.Hosting.Contracts.Generations.IPermanentNativeBindings"/>, so a test can
/// show that a retired generation's callbacks never run. The permanent host does not implement it. A
/// fixture generation loads in a collectible context and must see the same type as the test that
/// implements it, so the test shares this assembly with the host through
/// <see cref="Nwn.Hosting.Reload.GenerationLoadOptions.SharedAssemblyNames"/>.
/// </summary>
public interface IFixtureCallbackBindings
{
    IDisposable SubscribeEvent(string eventName, Action callback);

    IDisposable SubscribeTimer(string timerName, Action callback);

    IDisposable SubscribeUi(string windowId, Action callback);
}
