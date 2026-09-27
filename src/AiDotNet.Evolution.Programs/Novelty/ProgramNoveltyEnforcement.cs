namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>What a novelty gate does with a candidate its policy judges not novel.</summary>
public enum ProgramNoveltyEnforcement
{
    /// <summary>Reject it before evaluation (the original behavior).</summary>
    Reject,

    /// <summary>Evaluate it anyway and attach a <c>program_similar</c> diagnostic: similarity is only a heuristic.</summary>
    Advise
}