namespace AiDotNet.Evolution;

/// <summary>Marks a selection policy that can draw a parent from a Pareto archive's infeasible exploration pool.</summary>
/// <typeparam name="TGenome">The task-specific genome type.</typeparam>
/// <remarks>
/// <para>
/// A <see cref="ParetoArchive{TGenome}"/> with an enabled exploration pool can hold candidates while its deployable
/// front is still empty. Those candidates are selectable only by a policy that knows the pool exists:
/// <see cref="ParetoEvolutionSelectionPolicy{TGenome}"/> implements this interface, while a scalar policy such as
/// <see cref="UniformEvolutionSelectionPolicy{TGenome}"/> samples the feasible archive alone and would return
/// <c>null</c> for a pool-only island. The engine therefore counts the pool as selectable material only for policies
/// that declare this capability, instead of treating a pool-only island as occupied and then ending the batch with
/// <c>NoCandidates</c> when the policy declines it.
/// </para>
/// <para><b>For Beginners:</b> Implement this on a custom selection policy only if it actually looks at
/// <c>IEvolutionParetoArchiveView&lt;TGenome&gt;.InfeasibleEntries</c>. Declaring it without sampling the pool tells
/// the engine a run can continue from candidates the policy will never pick.</para>
/// </remarks>
public interface IInfeasibleExplorationSelectionPolicy<TGenome> : ISelectionPolicy<TGenome>
{
}
