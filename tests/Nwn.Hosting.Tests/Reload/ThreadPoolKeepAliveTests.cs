using Nwn.Hosting.Reload;
using Nwn.Hosting.Testing.Fixtures;
using static Nwn.Hosting.Testing.Support.TestAssertions;

namespace Nwn.Hosting.Tests.Reload;

/// <summary>
/// Hot reload needs every thread that runs generation code to outlive the process's later generations
/// (docs/threads-outlive-generations.md): the host's and this test host's runtime configuration keep
/// thread-pool workers alive, and <see cref="ReloadHost"/> refuses a process that does not.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ThreadPoolKeepAliveTests
{
    [TestMethod]
    public void ThisTestProcess_KeepsEveryThreadPoolWorkerAlive()
    {
        Require(ThreadPoolKeepAlive.IsEnabled,
            $"The test host must set {ThreadPoolKeepAlive.ConfigurationProperty} to -1, as a hosting application does.");
    }

    [TestMethod]
    public void TheEnvironmentVariable_OverridesTheConfigurationProperty_AsInTheRuntime()
    {
        Require(ThreadPoolKeepAlive.KeepsEveryWorker("-1", null), "The environment variable alone must count.");
        Require(ThreadPoolKeepAlive.KeepsEveryWorker(null, "-1"), "The configuration property alone must count.");
        Require(!ThreadPoolKeepAlive.KeepsEveryWorker("0", "-1"), "A readable environment variable must win.");
        Require(ThreadPoolKeepAlive.KeepsEveryWorker("not-a-number", "-1"), "An unreadable environment variable must be ignored.");
        Require(!ThreadPoolKeepAlive.KeepsEveryWorker(null, null), "The runtime default lets idle workers retire.");
        Require(!ThreadPoolKeepAlive.KeepsEveryWorker(null, "4"), "Keeping only some workers alive is not enough.");
    }

    [TestMethod]
    public void ReloadHost_RefusesAProcessWhoseThreadPoolRetiresWorkers()
    {
        var previous = Environment.GetEnvironmentVariable(ThreadPoolKeepAlive.EnvironmentVariable);
        Environment.SetEnvironmentVariable(ThreadPoolKeepAlive.EnvironmentVariable, "0");
        try
        {
            RequireThrows<InvalidOperationException>(
                () => _ = new ReloadHost(new RecordingNativeBindings()),
                "ReloadHost must refuse a process whose idle thread-pool workers retire.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ThreadPoolKeepAlive.EnvironmentVariable, previous);
        }
    }
}
