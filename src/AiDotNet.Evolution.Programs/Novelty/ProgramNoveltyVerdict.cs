// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Enums/ProgramNoveltyVerdict.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>A novelty judge's answer.</summary>
public enum ProgramNoveltyVerdict
{
    /// <summary>No usable answer: the request failed or the reply could not be read.</summary>
    Unavailable = 0,

    /// <summary>The candidate differs meaningfully.</summary>
    Novel = 1,

    /// <summary>The candidate is a trivial variation.</summary>
    NotNovel = 2
}
