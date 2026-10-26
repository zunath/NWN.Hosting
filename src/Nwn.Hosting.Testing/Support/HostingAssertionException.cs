namespace Nwn.Hosting.Testing.Support;

/// <summary>
/// Thrown by <see cref="TestAssertions"/> when a reload proof's condition does not hold. A test framework
/// reports any exception from a test method as a failure, so the package needs no framework reference.
/// </summary>
public sealed class HostingAssertionException(string message) : Exception(message);
