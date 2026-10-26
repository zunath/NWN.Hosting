using Nwn.Hosting.Contracts.Generations;

namespace Nwn.Hosting.Reload.ScriptGameplay;

/// <summary>
/// Recognizes exactly one script name, <c>"nh_gen_&lt;generation&gt;"</c>, tying it to whichever
/// generation number this activation actually received - so two activations of this SAME fixture
/// (an ordinary reload cycle, exactly like a many-generation proof of real gameplay) give the
/// test two distinct, unambiguous script names to assert against without needing two separate
/// fixture assemblies. <c>"nh_throws"</c> and the main-loop tick each have their own injectable
/// failure, so a test can prove the host survives a broken generation without crashing everyone else.
/// </summary>
public sealed class ScriptGameplayGeneration : IGameplayGeneration
{
    public const string ThrowingScriptName = "nh_throws";
    public const string TickFailureVariable = "NWN_HOSTING_SCRIPT_FIXTURE_TICK_FAIL";

    private GenerationActivation? _activation;

    public string BuildId => "script-gameplay-v1";

    public ValueTask ValidateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ActivateAsync(GenerationActivation activation, CancellationToken cancellationToken)
    {
        _activation = activation;
        Scripts = new Dispatcher(activation);
        MainLoop = new Sink(activation);
        return ValueTask.CompletedTask;
    }

    public ValueTask BeginQuiescenceAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DrainAsync(TimeSpan timeout, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Scripts = null;
        MainLoop = null;
        _activation = null;
        return ValueTask.CompletedTask;
    }

    public IScriptDispatcher? Scripts { get; private set; }

    public IMainLoopSink? MainLoop { get; private set; }

    public static string ScriptNameFor(long generation) => $"nh_gen_{generation}";

    private sealed class Dispatcher(GenerationActivation activation) : IScriptDispatcher
    {
        public bool TryHandle(string scriptName, uint objectSelf, out bool conditionalResult)
        {
            conditionalResult = false;
            if (scriptName == ThrowingScriptName)
            {
                throw new InvalidOperationException("Injected script dispatch failure.");
            }

            if (scriptName != ScriptNameFor(activation.Generation)) return false;

            activation.Native.Report("script-handled", $"{scriptName}:{objectSelf}");
            return true;
        }
    }

    private sealed class Sink(GenerationActivation activation) : IMainLoopSink
    {
        public void Tick()
        {
            if (Environment.GetEnvironmentVariable(TickFailureVariable) == "1")
            {
                throw new InvalidOperationException("Injected main-loop tick failure.");
            }

            activation.Native.Report("tick", activation.Generation.ToString());
        }
    }
}
