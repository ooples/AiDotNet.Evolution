// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Interfaces/IProgramNoveltyJudge.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>Decides whether a candidate differs meaningfully from an existing program.</summary>
public interface IProgramNoveltyJudge
{
    /// <summary>Gets the judge identity.</summary>
    string Id { get; }

    /// <summary>Gets a revision that changes whenever the judge's answers could.</summary>
    string VersionHash { get; }

    /// <summary>Compares a candidate with an existing program.</summary>
    /// <param name="candidate">The proposed program.</param>
    /// <param name="incumbent">The existing program it most resembles.</param>
    /// <param name="cancellationToken">Cancels the judgement.</param>
    /// <returns>The verdict; <see cref="ProgramNoveltyVerdict.Unavailable"/> when no usable answer was obtained.</returns>
    ValueTask<ProgramNoveltyVerdict> JudgeAsync(
        ProgramGenome candidate,
        ProgramGenome incumbent,
        CancellationToken cancellationToken = default);
}
