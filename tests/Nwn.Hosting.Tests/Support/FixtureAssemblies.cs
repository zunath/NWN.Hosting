using Nwn.Hosting.Reload;

namespace Nwn.Hosting.Tests.Support;

/// <summary>Where the fixture generation builds land and how the test host shares assemblies with them.</summary>
internal static class FixtureAssemblies
{
    /// <summary>
    /// The fixture generations register callbacks through <c>IFixtureCallbackBindings</c>, which lives in
    /// Nwn.Hosting.Testing; sharing that assembly gives them the host's copy, so the type they test for is the
    /// type the test's bindings implement.
    /// </summary>
    public static readonly GenerationLoadOptions LoadOptions = new(["Nwn.Hosting.Testing"], "nwn-hosting-test");

    /// <summary>Path to a reload fixture build, copied by Nwn.Hosting.Tests.csproj into games/&lt;version&gt;.</summary>
    public static string AssemblyFor(string version) =>
        Path.Combine(AppContext.BaseDirectory, "games", version, $"Nwn.Hosting.Reload.Gameplay.V{version[1]}.dll");

    /// <summary>Path to the generic-virtual-dispatch fixture build, copied by Nwn.Hosting.Tests.csproj into games/dispatch.</summary>
    public static string DispatchAssembly =>
        Path.Combine(AppContext.BaseDirectory, "games", "dispatch", "Nwn.Hosting.Reload.DispatchGameplay.dll");
}
