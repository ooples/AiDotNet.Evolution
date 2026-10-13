namespace AiDotNet.Evolution.Programs;

/// <summary>How a meta-prompt run picks which evolved instruction writes the next program proposal.</summary>
public enum ProgramInstructionSelection
{
    /// <summary>
    /// Pick the instruction with the best mean downstream quality, exploring with
    /// <see cref="ProgramMetaPromptOptions.ExplorationProbability"/>.
    /// </summary>
    /// <remarks>
    /// An unscored instruction counts as unexplored and is tried before any scored one, so a freshly
    /// proposed instruction cannot be starved by an incumbent that happens to lead early.
    /// </remarks>
    EpsilonGreedy,

    /// <summary>Always pick the instruction with the best mean downstream quality.</summary>
    /// <remarks>
    /// Useful as a control arm: with no exploration the archive stops learning once an instruction
    /// leads, which is what an ablation of exploration should measure.
    /// </remarks>
    Greedy,

    /// <summary>Pick uniformly at random from the archive.</summary>
    /// <remarks>The other control arm: proposals still evolve, but selection carries no credit.</remarks>
    Uniform
}
