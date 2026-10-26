using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Nwn.Hosting.Contracts.Generations;

namespace Nwn.Hosting.Reload.DispatchGameplay;

/// <summary>
/// A generation that makes generic virtual calls while it activates. The instance is created reflectively so
/// the JIT cannot devirtualize the calls, and each call uses a different instantiation, so every activation
/// resolves several generic virtual methods through the runtime's dispatch cache. LINQ's <c>Select</c> and
/// <c>Where</c> over this assembly's own types are generic virtual calls too.
/// </summary>
public sealed class DispatchGeneration : IGameplayGeneration
{
    public const string ReportCategory = "dispatch";
    public const string ContextReportCategory = "context";

    public string BuildId => "dispatch-fixture";

    public ValueTask ValidateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ActivateAsync(GenerationActivation activation, CancellationToken cancellationToken)
    {
        activation.Native.Report(ContextReportCategory, AssemblyLoadContext.GetLoadContext(typeof(DispatchGeneration).Assembly)!.Name!);
        activation.Native.Report(ReportCategory, $"{activation.Generation}:{ExerciseGenericVirtualDispatch()}");
        return ValueTask.CompletedTask;
    }

    public ValueTask BeginQuiescenceAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DrainAsync(TimeSpan timeout, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExerciseGenericVirtualDispatch()
    {
        var echo = (EchoBase)Activator.CreateInstance(typeof(EchoImplementation))!;
        var marker = echo.Echo(new Marker(3));
        var markers = echo.Echo(new[] { marker, new Marker(4) });
        var text = echo.Echo("value");
        var number = echo.Echo(7);
        var evenSum = Enumerable.Range(0, 8)
            .Select(index => new Marker(index))
            .Where(candidate => candidate.Value % 2 == 0)
            .Select(candidate => candidate.Value)
            .Sum();
        return markers.Length + text.Length + number + evenSum;
    }
}
