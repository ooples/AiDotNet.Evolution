using System.Text.RegularExpressions;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// US-20 (#38): the pipeline's own source carries no null-forgiving operator.
/// </summary>
/// <remarks>
/// The two files below used `!` nine times between them, and one of those suppressions was load-bearing:
/// `request.Context.ProposalIdentity!` let a proposal with no stable identity be scheduled, so the story's
/// own criterion - "an immutable semantic context snapshot and stable request identity are supplied" -
/// could be violated and would only surface as a replay mismatch much later. The identity is now computed
/// in the branch that has it, and absence throws where it happens.
///
/// The guard grows with the sweep rather than all at once. `src/` held 73 real sites across 37 files when
/// this was written; the files listed below are the ones cleaned so far, and each is pinned as it lands so
/// the next edit to it cannot quietly put a suppression back.
/// </remarks>
public sealed class PipelineNullHandlingTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // A `!` directly after an identifier, a closing bracket or a `>` and not part of `!=`. That is the
    // null-forgiving position; logical negation always has an operator or an opening bracket before it.
    private static readonly Regex NullForgiving =
        new(@"[A-Za-z0-9_\]\)>]\!(?!=)", RegexOptions.None, RegexTimeout);

    [Theory]
    [InlineData("src/AiDotNet.Evolution/Core/EvolutionEngine.Pipeline.cs")]
    [InlineData("src/AiDotNet.Evolution/Core/EvolutionEngine.Evaluation.cs")]
    // Cleaned by the sweep this guard exists to make stick, one file at a time.
    [InlineData("src/AiDotNet.Evolution/Core/DurableEvolutionWorkCoordinator.cs")]
    [InlineData("src/AiDotNet.Evolution/Core/EvolutionEngine.Persistence.cs")]
    [InlineData("src/AiDotNet.Evolution/Core/EvolutionIncumbentChallenge.cs")]
    public void TheseFilesSuppressNoNullWarnings(string relativePath)
    {
        string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), path + " is missing; this guard names a file that moved");

        var offenders = new List<string>();
        int number = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            number++;
            string trimmed = line.TrimStart();
            // Comments may discuss the operator - this file's own remarks do.
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;
            if (NullForgiving.IsMatch(line)) offenders.Add(number + ": " + trimmed);
        }

        Assert.Empty(offenders);
    }

    private static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AiDotNet.Evolution.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
