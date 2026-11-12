using NWN.Core;
using Nwn.Hosting.Contracts.Generations;

namespace Nwn.Hosting.Reload.ScriptGameplay;

/// <summary>
/// Recognizes exactly one action script name, <c>"nh_gen_&lt;generation&gt;"</c>, tying it to whichever
/// generation number this activation actually received - so two activations of this SAME fixture
/// (an ordinary reload cycle, exactly like a many-generation proof of real gameplay) give the
/// test two distinct, unambiguous script names to assert against without needing two separate
/// fixture assemblies. <c>"nh_throws"</c> and the main-loop tick each have their own injectable
/// failure, so a test can prove the host survives a broken generation without crashing everyone else.
/// <c>"nh_cond_true"</c> and <c>"nh_cond_false"</c> are conditional scripts with a fixed result, and
/// <c>"nh_schedule_closure"</c> schedules a <c>DelayCommand</c> closure through the host's
/// <see cref="ICoreFunctionHandler"/> (resolved from the host services) that reports
/// <c>"&lt;generation&gt;:&lt;sequence&gt;"</c> under <c>"closure-ran"</c> when it runs. Activation and disposal
/// report the generation under <c>"generation-activated"</c> and <c>"generation-disposed"</c>.
/// </summary>
public sealed class ScriptGameplayGeneration : IGameplayGeneration
{
    public const string ThrowingScriptName = "nh_throws";
    public const string TickFailureVariable = "NWN_HOSTING_SCRIPT_FIXTURE_TICK_FAIL";
    public const string ConditionalTrueScriptName = "nh_cond_true";
    public const string ConditionalFalseScriptName = "nh_cond_false";
    public const string ScheduleClosureScriptName = "nh_schedule_closure";
    public const string ClosureRanCategory = "closure-ran";
    public const string ActivatedCategory = "generation-activated";
    public const string DisposedCategory = "generation-disposed";

    private GenerationActivation? _activation;

    public string BuildId => "script-gameplay-v1";

    public ValueTask ValidateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ActivateAsync(GenerationActivation activation, CancellationToken cancellationToken)
    {
        _activation = activation;
        Scripts = new Dispatcher(activation);
        MainLoop = new Sink(activation);
        activation.Native.Report(ActivatedCategory, activation.Generation.ToString());
        return ValueTask.CompletedTask;
    }

    public ValueTask BeginQuiescenceAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DrainAsync(TimeSpan timeout, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _activation?.Native.Report(DisposedCategory, _activation.Generation.ToString());
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
        private int _scheduled;

        public bool TryHandle(string scriptName, uint objectSelf, out bool conditionalResult)
        {
            conditionalResult = false;
            switch (scriptName)
            {
                case ThrowingScriptName:
                    throw new InvalidOperationException("Injected script dispatch failure.");
                case ConditionalTrueScriptName:
                    conditionalResult = true;
                    return true;
                case ConditionalFalseScriptName:
                    return true;
                case ScheduleClosureScriptName:
                    ScheduleClosure(objectSelf);
                    return true;
            }

            if (scriptName != ScriptNameFor(activation.Generation)) return false;

            activation.Native.Report("script-handled", $"{scriptName}:{objectSelf}");
            return true;
        }

        private void ScheduleClosure(uint objectSelf)
        {
            var closures = (ICoreFunctionHandler?)activation.HostServices?.Resolve(typeof(ICoreFunctionHandler))
                ?? throw new InvalidOperationException("The host shares no ICoreFunctionHandler.");
            var sequence = ++_scheduled;
            closures.ClosureDelayCommand(objectSelf, 1.0f,
                () => activation.Native.Report(ClosureRanCategory, $"{activation.Generation}:{sequence}"));
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
