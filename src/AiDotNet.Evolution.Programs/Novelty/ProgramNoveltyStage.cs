// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Enums/ProgramNoveltyStage.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>The stage of <see cref="ProgramNoveltyPolicy"/> that made a decision.</summary>
public enum ProgramNoveltyStage
{
    /// <summary>No comparison was needed, because nothing was known.</summary>
    None = 0,

    /// <summary>Structural distance decided.</summary>
    Structural = 1,

    /// <summary>Embedding similarity decided, or the embedding request failed.</summary>
    Embedding = 2,

    /// <summary>A model judge decided.</summary>
    LanguageModel = 3
}
