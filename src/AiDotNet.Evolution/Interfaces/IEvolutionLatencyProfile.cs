namespace AiDotNet.Evolution;

/// <summary>Declares whether a variation operator's proposals or a task's evaluations wait on external latency.</summary>
/// <remarks>
/// <see cref="EvolutionDispatchMode.Auto"/> reads this to choose between pipeline and batch dispatch. Declare
/// <see cref="IsLatencyBound"/> as <c>true</c> for work that mostly waits (a model API call, a subprocess, a remote
/// evaluator), and <c>false</c> or leave the interface unimplemented for in-process computation. Decorators should
/// forward their inner component's value. The value must be constant for the component's lifetime.
/// </remarks>
public interface IEvolutionLatencyProfile
{
    /// <summary>Gets whether this component's calls mostly wait on external latency rather than compute.</summary>
    bool IsLatencyBound { get; }
}