using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests;

public sealed class CompilerTests
{
    internal static CSharpProgramCompiler Compiler() => new(new[] { typeof(object).Assembly.Location }, "test-runtime-" + Environment.Version);
    internal static ProgramSnapshot Source(string body = "return Helper.Get(x);") => new(new Dictionary<string, string>
    {
        ["Algorithm.cs"] = "public static class Algorithm { public static int Run(int x) { " + body + " } }",
        ["Helpers/Helper.cs"] = "internal static class Helper { internal static int Get(int x) { return x + 1; } }"
    });
    internal static PatchPlan Plan(CSharpProgramCompiler compiler, ProgramSnapshot parent, params (string File, string Old, string New)[] edits)
    {
        var catalog = compiler.Catalog(parent);
        return new(parent.Fingerprint, "Eliminate redundant helper work while preserving x + 1.", edits.Select(edit =>
            new SourceEdit(catalog.First(t => t.File == edit.File &&
                parent.Files[t.File].Substring(t.Start, t.Length) == edit.Old), edit.New)).ToArray());
    }

    [Fact]
    public void MultiFilePatchIsAtomicAndPreservesContract()
    {
        var compiler = Compiler();
        var parent = Source();
        var plan = Plan(compiler, parent, ("Algorithm.cs", "return Helper.Get(x);", "return Helper.Get(x) + 1;"),
            ("Helpers/Helper.cs", "return x + 1;", "return x;"));
        var candidate = compiler.Apply(parent, plan);
        Assert.Contains("return x + 1;", parent.Files["Helpers/Helper.cs"]);
        Assert.NotEqual(parent.Fingerprint, candidate.Fingerprint);
        var original = compiler.Build(parent).Artifact!;
        var updated = compiler.Build(candidate).Artifact!;
        Assert.Equal(original.ApiFingerprint, updated.ApiFingerprint);
        Assert.Equal(original.CompilerFingerprint, updated.CompilerFingerprint);
        Assert.NotEqual(original.ImageFingerprint, updated.ImageFingerprint);
        Assert.Equal(updated.Fingerprint, compiler.Build(candidate).Artifact!.Fingerprint);
        var bytes = updated.GetImage();
        bytes[0] = 0;
        Assert.NotEqual(bytes[0], updated.GetImage()[0]);
    }

    [Theory]
    [InlineData("../escape.cs")]
    [InlineData("/absolute.cs")]
    [InlineData("C:/file.cs")]
    [InlineData("folder\\file.cs")]
    [InlineData("a//b.cs")]
    public void RejectsNoncanonicalNames(string name) => Assert.Throws<ArgumentException>(() =>
        new ProgramSnapshot(new Dictionary<string, string> { [name] = "class C {}" }));

    [Fact]
    public void SnapshotOwnsInputAndBindsAllFiles()
    {
        var files = new Dictionary<string, string> { ["a.cs"] = "class A {}" };
        var source = new ProgramSnapshot(files);
        files["a.cs"] = "class B {}";
        Assert.Equal("class A {}", source.Files["a.cs"]);
        Assert.NotEqual(source.Fingerprint, new ProgramSnapshot(files).Fingerprint);
        Assert.Throws<ArgumentException>(() => new ProgramSnapshot(new Dictionary<string, string>
        { ["A.cs"] = "a", ["a.cs"] = "b" }));
        Assert.Throws<ArgumentException>(() => new ProgramSnapshot(new Dictionary<string, string>
        { ["a.cs"] = new string('a', ProgramSnapshot.MaximumCharacters + 1) }));
    }

    [Theory]
    [InlineData("#if true\nreturn x;\n#endif")]
    [InlineData("return x; } public static int Other() { return 0;")]
    [InlineData("return x; return x;")]
    [InlineData("")]
    public void RejectsBoundaryEscapes(string replacement)
    {
        var compiler = Compiler();
        var parent = Source();
        var plan = Plan(compiler, parent, ("Helpers/Helper.cs", "return x + 1;", replacement));
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, plan));
    }

    [Fact]
    public void RejectsStaleOverlappingAndNoopPatches()
    {
        var compiler = Compiler();
        var parent = Source();
        var plan = Plan(compiler, parent, ("Helpers/Helper.cs", "return x + 1;", "return x;"));
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, plan with { ParentFingerprint = "wrong" }));
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, plan with { Edits = new[] { plan.Edits[0], plan.Edits[0] } }));
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, plan with
        { Edits = new[] { plan.Edits[0] with { Target = plan.Edits[0].Target with { ExpectedHash = "wrong" } } } }));
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent,
            Plan(compiler, parent, ("Helpers/Helper.cs", "return x + 1;", "return x + 1;"))));
    }

    [Fact]
    public void BuildReturnsOnlyBoundedCompilerDiagnostics()
    {
        var build = Compiler().Build(Source("return SECRET_DO_NOT_ECHO;"));
        Assert.Null(build.Artifact);
        Assert.Contains("CS0103", build.Feedback);
        Assert.DoesNotContain("SECRET", build.Feedback);
    }

    [Fact]
    public void ApiAndReferenceTargetChangesInvalidateIdentity()
    {
        var source = Source();
        var artifact = Compiler().Build(source).Artifact!;
        var changed = new ProgramSnapshot(source.Files.Select(f => new KeyValuePair<string, string>(f.Key,
            f.Value.Replace("public static int Run", "public static long Run"))));
        Assert.NotEqual(artifact.ApiFingerprint, Compiler().Build(changed).Artifact!.ApiFingerprint);
        var other = new CSharpProgramCompiler(new[] { typeof(object).Assembly.Location }, "different-target");
        Assert.NotEqual(artifact.CompilerFingerprint, other.Build(source).Artifact!.CompilerFingerprint);
        Assert.Throws<ArgumentException>(() => new CSharpProgramCompiler(
            new[] { typeof(object).Assembly.Location, typeof(object).Assembly.Location }, "target"));
    }

    [Fact]
    public void CancelledCompilerCannotEmit()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Compiler().Build(Source(), cancellation.Token));
    }
}
