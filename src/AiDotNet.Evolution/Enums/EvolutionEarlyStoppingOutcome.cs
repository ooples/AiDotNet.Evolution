namespace AiDotNet.Evolution;

/// <summary>What one reading of the configured early-stopping criterion concluded.</summary>
/// <remarks>
/// <para>
/// A criterion reading has three outcomes, not two. <see cref="Improved"/> and <see cref="NotImproved"/> both mean
/// the criterion was measured; only <see cref="NotImproved"/> charges the batch's evaluations to patience.
/// <see cref="Unmeasurable"/> means there was nothing to measure yet - a metric no evaluation has reported, an empty
/// feasible front, an empty archive, or archives with no cells - and it never charges patience, because "not
/// measured" is not evidence of a plateau. Every unmeasurable reading carries an
/// <see cref="EvolutionEarlyStoppingUnmeasurableReason"/> and is counted in
/// <see cref="EvolutionEarlyStoppingReport"/>, and a run whose criterion was never measurable once fails rather than
/// reporting as though it had a stopping criterion.
/// </para>
/// <para><b>For Beginners:</b> "The score got worse" and "there is no score yet" are different situations. Treating
/// the second as the first is what makes a search stop before it has produced anything to judge.</para>
/// </remarks>
public enum EvolutionEarlyStoppingOutcome
{
    /// <summary>The criterion was measured and gained at least the configured minimum improvement.</summary>
    Improved = 0,

    /// <summary>The criterion was measured and did not improve; the batch's evaluations are charged to patience.</summary>
    NotImproved = 1,

    /// <summary>The criterion could not be measured; patience is not charged and the reason is recorded.</summary>
    Unmeasurable = 2
}
