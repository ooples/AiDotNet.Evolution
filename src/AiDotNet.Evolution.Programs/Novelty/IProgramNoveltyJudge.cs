// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Interfaces/IProgramNoveltyJudge.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Programs.Novelty;

public interface IProgramNoveltyJudge
{
    string Id { get; }
    string VersionHash { get; }

    ValueTask<ProgramNoveltyVerdict> JudgeAsync(
        ProgramGenome candidate,
        ProgramGenome incumbent,
        CancellationToken cancellationToken = default);
}
