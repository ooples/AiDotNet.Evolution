// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Evolution/Programs/Novelty/ProgramLineEditDistance.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.

namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>Line-level Levenshtein distance between two normalised programs, divided by the longer line count.</summary>
/// <remarks>Programs in different languages are at distance 1. Lines past the limit are folded into one hashed line,
/// so very long programs cost bounded time.</remarks>
public sealed class ProgramLineEditDistance : IGenomeDistance<ProgramGenome>
{
    /// <summary>The metric identity reported as <see cref="Id"/>.</summary>
    public const string MetricId = "program-line-edit";

    /// <summary>The default number of lines compared individually.</summary>
    public const int DefaultMaxComparedLines = 2_000;

    /// <summary>Creates the distance.</summary>
    /// <param name="maxComparedLines">Lines compared individually, 1 to 4096.</param>
    /// <exception cref="ArgumentOutOfRangeException">The limit is out of range.</exception>
    public ProgramLineEditDistance(int maxComparedLines = DefaultMaxComparedLines)
    {
        if (maxComparedLines < 1 || maxComparedLines > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxComparedLines), maxComparedLines,
                "Value must be between 1 and 4096.");
        }

        MaxComparedLines = maxComparedLines;
    }

    /// <summary>Gets how many lines are compared individually.</summary>
    public int MaxComparedLines { get; }

    /// <inheritdoc />
    public string Id => MetricId;

    /// <inheritdoc />
    public string VersionHash =>
        MetricId + "-v2-" + MaxComparedLines.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public double Distance(ProgramGenome first, ProgramGenome second)
    {
        ProgramGuard.NotNull(first);
        ProgramGuard.NotNull(second);
        if (first.Language != second.Language) return 1;
        return Compute(first.NormalizedSource, second.NormalizedSource, MaxComparedLines);
    }

    /// <summary>Computes the distance between two source texts.</summary>
    /// <param name="first">One source.</param>
    /// <param name="second">The other.</param>
    /// <param name="maxComparedLines">Lines compared individually, 1 to 4096.</param>
    /// <returns>A distance in [0, 1].</returns>
    /// <exception cref="ArgumentOutOfRangeException">The limit is out of range.</exception>
    public static double ComputeDistance(string first, string second, int maxComparedLines = DefaultMaxComparedLines)
    {
        ProgramGuard.NotNull(first);
        ProgramGuard.NotNull(second);
        if (maxComparedLines < 1 || maxComparedLines > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxComparedLines), maxComparedLines,
                "Value must be between 1 and 4096.");
        }

        return Compute(ProgramText.Normalize(first), ProgramText.Normalize(second), maxComparedLines);
    }

    private static double Compute(string first, string second, int maxComparedLines)
    {
        if (first.Length > ProgramGenome.MaxSourceLength || second.Length > ProgramGenome.MaxSourceLength)
            throw new ArgumentException("Structural comparison source exceeds the program bound.");
        if (string.Equals(first, second, StringComparison.Ordinal)) return 0.0;

        IReadOnlyList<string> firstLines = Bound(ProgramText.SplitLines(first), maxComparedLines);
        IReadOnlyList<string> secondLines = Bound(ProgramText.SplitLines(second), maxComparedLines);

        int longer = Math.Max(firstLines.Count, secondLines.Count);
        if (longer == 0) return 0.0;

        int edits = Levenshtein(firstLines, secondLines);
        double distance = (double)edits / longer;
        return distance < 0.0 ? 0.0 : distance > 1.0 ? 1.0 : distance;
    }

    private static IReadOnlyList<string> Bound(List<string> lines, int maxComparedLines)
    {
        if (lines.Count <= maxComparedLines) return lines.Select(line => "line:" + line).ToArray();
        var prefix = lines.Take(maxComparedLines - 1).Select(line => "line:" + line).ToList();
        prefix.Add("tail:" + EvolutionHash.Compute(string.Join("\n", lines.Skip(maxComparedLines - 1))));
        return prefix;
    }

    private static int Levenshtein(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        // Keep the shorter sequence on the row axis so the two working rows stay as small as possible.
        IReadOnlyList<string> rows = first.Count <= second.Count ? first : second;
        IReadOnlyList<string> columns = ReferenceEquals(rows, first) ? second : first;

        var previous = new int[rows.Count + 1];
        var current = new int[rows.Count + 1];
        for (int index = 0; index <= rows.Count; index++) previous[index] = index;

        for (int column = 1; column <= columns.Count; column++)
        {
            current[0] = column;
            string columnLine = columns[column - 1];
            for (int row = 1; row <= rows.Count; row++)
            {
                int substitution = previous[row - 1] +
                    (string.Equals(rows[row - 1], columnLine, StringComparison.Ordinal) ? 0 : 1);
                int deletion = previous[row] + 1;
                int insertion = current[row - 1] + 1;
                int best = substitution < deletion ? substitution : deletion;
                current[row] = best < insertion ? best : insertion;
            }

            int[] swap = previous;
            previous = current;
            current = swap;
        }

        return previous[rows.Count];
    }
}
