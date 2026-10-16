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
/// The guard is deliberately narrow. A sweep of `src/` finds the operator at roughly 86 further sites in 36
/// other files, which is separate work on separate stories; pinning those two files is what keeps *this*
/// story's fix from being undone by the next edit to it.
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
