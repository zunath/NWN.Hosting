using Nwn.Hosting.Reload;

namespace Nwn.Hosting.Testing.Support;

/// <summary>
/// Condition-and-message assertions for the reload proofs. They throw <see cref="HostingAssertionException"/>
/// on failure, which a test framework reports as a failing test, and keep each proof's condition next to the
/// reason it matters.
/// </summary>
public static class TestAssertions
{
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new HostingAssertionException(message);
    }

    public static void RequireThrows<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new HostingAssertionException(message);
    }

    public static async Task RequireThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new HostingAssertionException(message);
    }

    /// <summary>
    /// Requires that every retired generation's collectible context was collected. Probe after the cycle
    /// runner has left the reload call sites: a probe taken from inside the replacement call can retain that
    /// call's state-machine locals and is not valid evidence.
    /// </summary>
    public static void RequireNoRetainedContexts(RetiredContextProbe probe, string scenario)
    {
        ArgumentNullException.ThrowIfNull(probe);
        Require(probe.RetainedContexts == 0, $"{scenario}: {probe.RetainedContexts} retired contexts remained rooted.");
    }
}
