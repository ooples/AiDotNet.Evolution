namespace AiDotNet.Evolution;

/// <summary>One candidate handed to the caller by <see cref="EvolutionSession{TGenome}.AskAsync"/>.</summary>
/// <typeparam name="TGenome">The task-specific genome type.</typeparam>
/// <remarks>
/// <para>
/// Carries exactly what an evaluator needs and nothing it does not: the candidate to score and the deterministic
/// context it must draw randomness from. The <see cref="EvolutionCandidate{TGenome}.EvaluationId"/> on the
/// candidate is the handle the caller passes back to <see cref="EvolutionSession{TGenome}.Tell"/>.
/// </para>
/// <para><b>For Beginners:</b> This is one entry to be judged. Score it however you like, then report the score
/// using the evaluation id so the organizer knows which entry it belongs to.</para>
/// </remarks>
public sealed class EvolutionAskItem<TGenome>
{
    /// <summary>Initializes an ask item.</summary>
    /// <param name="candidate">The candidate awaiting evaluation.</param>
    /// <param name="context">The deterministic per-evaluation context.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public EvolutionAskItem(EvolutionCandidate<TGenome> candidate, EvolutionEvaluationContext context)
    {
        Guard.NotNull(candidate);
        Guard.NotNull(context);
        Candidate = candidate;
        Context = context;
    }

    /// <summary>Gets the candidate awaiting evaluation.</summary>
    public EvolutionCandidate<TGenome> Candidate { get; }

    /// <summary>Gets the deterministic per-evaluation context.</summary>
    /// <remarks>
    /// Draw any randomness from <see cref="EvolutionEvaluationContext.CreateRandom"/> rather than ambient state,
    /// for the same reason a direct <see cref="IEvolutionTask{TGenome}"/> implementation must: a run is only
    /// reproducible if every evaluation's randomness comes from the seed stream the engine assigned it.
    /// </remarks>
    public EvolutionEvaluationContext Context { get; }

    /// <summary>Gets the identifier to pass back to <see cref="EvolutionSession{TGenome}.Tell"/>.</summary>
    public long EvaluationId => Candidate.EvaluationId;
}
