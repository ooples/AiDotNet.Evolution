using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests;

/// <summary>US-17 for Python: the compiler-guided loop's compiler contract, against a real interpreter.</summary>
public sealed class PythonProgramCompilerTests
{
    private static readonly string Python = Environment.GetEnvironmentVariable("EVOLUTION_PYTHON")
        ?? (OperatingSystem.IsWindows() ? "python" : "python3");

    private const string Source = "# \U0001F600 non-BMP before the code\ndef solve(x):\n    y = x + 1\n    return y * 2\n";

    private static ProgramSnapshot Parent() => new(new Dictionary<string, string> { ["solver.py"] = Source });

    [Fact]
    public void Catalog_spans_are_exact_utf16_substrings_of_function_statements_and_expressions()
    {
        var compiler = new PythonProgramCompiler(Python);
        IReadOnlyList<EditTarget> targets = compiler.Catalog(Parent());
        Assert.Contains(targets, t => t.Kind == "statement" && Source.Substring(t.Start, t.Length) == "y = x + 1");
        Assert.Contains(targets, t => t.Kind == "expression" && Source.Substring(t.Start, t.Length) == "y * 2");
        Assert.All(targets, t => Assert.Equal(ProgramSnapshot.Digest(Source.Substring(t.Start, t.Length)), t.ExpectedHash));
    }

    [Fact]
    public void A_valid_edit_applies_and_builds_to_an_artifact()
    {
        var compiler = new PythonProgramCompiler(Python);
        ProgramSnapshot parent = Parent();
        EditTarget target = compiler.Catalog(parent).Single(t => Source.Substring(t.Start, t.Length) == "y * 2");
        ProgramSnapshot child = compiler.Apply(parent, new PatchPlan(parent.Fingerprint, "double via shift", new[] { new SourceEdit(target, "y << 1") }));
        Assert.Contains("return y << 1", child.Files["solver.py"]);
        ProgramBuild build = compiler.Build(child);
        Assert.NotNull(build.Artifact);
        Assert.Equal(string.Empty, build.Feedback);
        Assert.NotEqual(compiler.Build(parent).Artifact!.ImageFingerprint, build.Artifact!.ImageFingerprint);
    }

    [Fact]
    public void A_syntax_error_returns_positioned_feedback_and_no_artifact()
    {
        var compiler = new PythonProgramCompiler(Python);
        ProgramBuild build = compiler.Build(new ProgramSnapshot(new Dictionary<string, string> { ["bad.py"] = "def f(:\n    return 1\n" }));
        Assert.Null(build.Artifact);
        Assert.StartsWith("bad.py:1:", build.Feedback);
        Assert.Contains("SyntaxError", build.Feedback);
    }

    [Fact]
    public void Invalid_patches_are_refused()
    {
        var compiler = new PythonProgramCompiler(Python);
        ProgramSnapshot parent = Parent();
        IReadOnlyList<EditTarget> targets = compiler.Catalog(parent);
        EditTarget statement = targets.Single(t => Source.Substring(t.Start, t.Length) == "y = x + 1");
        EditTarget expression = targets.Single(t => Source.Substring(t.Start, t.Length) == "y * 2");
        PatchPlan Plan(params SourceEdit[] edits) => new(parent.Fingerprint, "h", edits);
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, Plan(new SourceEdit(statement with { Start = statement.Start + 1 }, "y = 2"))));
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, Plan(new SourceEdit(statement, "y = 1\nz = 2"))));
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, Plan(new SourceEdit(expression, "y = 3"))));
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, Plan(new SourceEdit(statement, "y = x + 1"))));
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, new PatchPlan("other", "h", new[] { new SourceEdit(statement, "y = 2") })));
        var outer = targets.First(t => t.Kind == "expression" && Source.Substring(t.Start, t.Length) == "x + 1");
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, Plan(new SourceEdit(statement, "y = 2"), new SourceEdit(outer, "x + 2"))));
    }

    [Fact]
    public void An_invalid_parent_is_rejected_by_the_catalog()
    {
        var compiler = new PythonProgramCompiler(Python);
        Assert.Throws<ArgumentException>(() => compiler.Catalog(new ProgramSnapshot(new Dictionary<string, string> { ["x.py"] = "def (:\n" })));
    }
}