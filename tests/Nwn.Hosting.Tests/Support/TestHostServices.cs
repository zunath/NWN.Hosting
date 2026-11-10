using Nwn.Hosting.Contracts.Generations;

namespace Nwn.Hosting.Tests.Support;

/// <summary>Host services that resolve exactly the services a test registers by type.</summary>
internal sealed class TestHostServices : IHostServices
{
    private readonly Dictionary<Type, object> _services = [];

    public TestHostServices Add(Type serviceType, object service)
    {
        _services.Add(serviceType, service);
        return this;
    }

    public object? Resolve(Type serviceType) => _services.GetValueOrDefault(serviceType);
}
