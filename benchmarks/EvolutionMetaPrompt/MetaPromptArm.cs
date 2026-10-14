using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.MetaPromptAblation;

/// <summary>Which arm of the V1-53 (#166) ablation a run belongs to.</summary>
/// <remarks>
/// An enum rather than a string because the arm decides what is constructed, and a typo in a string would
/// silently produce a control run labelled as a treatment one - the shape of error this ablation exists to
/// avoid.
/// </remarks>
internal enum MetaPromptArm
{
    /// <summary>The seed instruction is fixed for the whole run; nothing proposes a new one.</summary>
    Fixed = 0,

    /// <summary>Instructions evolve through <see cref="ProgramMetaPromptEvolver"/>, credited by outcome.</summary>
    Evolving = 1
}
