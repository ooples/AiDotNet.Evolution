using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Programs.Metrics;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ProgramTypes;

public sealed class EvolveBlockExtractionTests
{
    private const string Marked = "import math\n# EVOLVE-BLOCK-START\nx = 1\ny = 2\n# EVOLVE-BLOCK-END\nprint(x)\n";

    [Fact]
    public void A_complete_block_splits_the_source_around_its_body()
    {
        EvolveBlockExtractionResult result = EvolveBlock.Extract(Marked);

        Assert.Equal(EvolveBlockStatus.Complete, result.Status);
        Assert.True(result.IsWellFormed);
        Assert.True(result.TryGetPrimaryRegion(out EvolveBlockRegion region));
        Assert.Equal("x = 1\ny = 2\n", region.Body);
        Assert.StartsWith("import math\n", region.Prefix, StringComparison.Ordinal);
        Assert.EndsWith("print(x)\n", region.Suffix, StringComparison.Ordinal);
        Assert.Equal(2, region.BodyLineCount);
        Assert.Equal(Marked, region.ToSource());
        Assert.Equal(Marked.Replace("x = 1\ny = 2\n", "x = 3\n"), region.Rewrite("x = 3\n"));
    }

    [Fact]
    public void Missing_and_malformed_markers_are_reported_not_guessed()
    {
        EvolveBlockExtractionResult absent = EvolveBlock.Extract("x = 1\n");
        Assert.Equal(EvolveBlockStatus.NotPresent, absent.Status);
        Assert.False(absent.HasRegions);
        Assert.True(absent.IsWellFormed);

        EvolveBlockExtractionResult unterminated = EvolveBlock.Extract("# EVOLVE-BLOCK-START\nx = 1\n");
        Assert.Equal(EvolveBlockStatus.Unterminated, unterminated.Status);
        Assert.False(unterminated.IsWellFormed);

        EvolveBlockExtractionResult stray = EvolveBlock.Extract("x = 1\n# EVOLVE-BLOCK-END\n");
        Assert.Equal(EvolveBlockStatus.UnmatchedEnd, stray.Status);
        Assert.NotEmpty(stray.Diagnostics);
    }

    [Fact]
    public void A_language_selects_its_comment_markers()
    {
        string csharp = "class C {\n// EVOLVE-BLOCK-START\nint x = 1;\n// EVOLVE-BLOCK-END\n}\n";
        Assert.Equal(EvolveBlockStatus.Complete, EvolveBlock.Extract(csharp, ProgramLanguage.CSharp).Status);
        // Hash markers do not delimit C# blocks.
        Assert.Equal(EvolveBlockStatus.NotPresent, EvolveBlock.Extract(csharp).Status);
    }

    [Fact]
    public void Every_metric_value_reports_its_kind()
    {
        Assert.Equal(ProgramMetricValueKind.Number, ProgramMetricValue.Number(0.5).Kind);
        Assert.Equal(ProgramMetricValueKind.Flag, ProgramMetricValue.Flag(true).Kind);
        Assert.Equal(ProgramMetricValueKind.Text, ProgramMetricValue.Text("0.5").Kind);
    }

    [Fact]
    public void A_compilation_diagnostic_is_an_error_unless_it_says_otherwise()
    {
        var reported = new CompilationDiagnostic { Message = "';' expected", Code = "CS1002", Line = 3, Column = 7, Tool = "csc" };
        Assert.Equal(CompilationDiagnosticSeverity.Error, reported.Severity);

        var response = new ProgramExecuteResponse
        {
            Success = false,
            Language = ProgramLanguage.CSharp,
            ExitCode = 1,
            CompilationAttempted = true,
            CompilationSucceeded = false,
            CompilationDiagnostics = { reported, new CompilationDiagnostic { Message = "unused", Severity = CompilationDiagnosticSeverity.Warning } }
        };
        Assert.Equal(new[] { CompilationDiagnosticSeverity.Error, CompilationDiagnosticSeverity.Warning },
            response.CompilationDiagnostics.Select(diagnostic => diagnostic.Severity));
    }
}
