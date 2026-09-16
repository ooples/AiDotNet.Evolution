using System.Runtime.Loader;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests;

public sealed class ImprovementTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "evolution-us17-tests");
    private static ImprovementOptions Options(int repairs = 2) => new(Guid.NewGuid().ToString("N"),
        "Measured redundant helper work in Run(int).", "trusted-fixture-v1", Root, repairs);
    private static EvolutionResourceLedger Ledger(decimal cap = 1000) => new("test", ProgramImprovement.Units(cap));
    private static EvolutionResourceResult<T> Result<T>(T value, decimal cost = 1,
        EvolutionResourceOutcome outcome = EvolutionResourceOutcome.Completed) => new(value, ProgramImprovement.Units(cost), outcome);
    private static ValueTask<EvolutionResourceResult<VerificationReceipt>> Verify(VerificationRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Only authored fixtures, never untrusted/model-generated programs, run in this test process.
        var context = new AssemblyLoadContext("trusted-fixture", isCollectible: true);
        try
        {
            using var image = new MemoryStream(request.Artifact.GetImage());
            var method = context.LoadFromStream(image).GetType("Algorithm")!.GetMethod("Run")!;
            bool correct = new[] { -7, 0, 11 }.All(x => (int)method.Invoke(null, new object[] { x })! == x + 1);
            return new(Result(new VerificationReceipt(request.Nonce, request.Artifact.Fingerprint, "trusted-fixture-v1",
                correct, 10, correct ? "" : "Public test Run(x) != x + 1.")));
        }
        finally { context.Unload(); }
    }

    [Fact]
    public async Task RepairsCompilerThenPublicTestFailureWithoutExposingHeldOutFeedback()
    {
        var compiler = CompilerTests.Compiler();
        var parent = CompilerTests.Source();
        var requests = new List<SearchRequest>();
        int hiddenCalls = 0;
        ProgramPlanner planner = (request, _) =>
        {
            requests.Add(request);
            string replacement = request.Attempt switch { 1 => "return unknown;", 2 => "return x;", _ => "return 1 + x;" };
            return new(Result(CompilerTests.Plan(compiler, parent, ("Helpers/Helper.cs", "return x + 1;", replacement))));
        };
        ProgramVerifier hidden = (request, _) => new(Result(new VerificationReceipt(request.Nonce,
            request.Artifact.Fingerprint, "trusted-fixture-v1", true, ++hiddenCalls == 1 ? 20 : 10, "HIDDEN_SENTINEL")));
        var options = Options();
        var ledger = Ledger();
        var result = await ProgramImprovement.RunAsync(parent, () => compiler, planner, Verify, hidden, ledger, options);
        Assert.True(result.Promoted);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(2, hiddenCalls);
        Assert.Contains("CS0103", requests[1].Feedback);
        Assert.Contains("Public test", requests[2].Feedback);
        Assert.All(requests, request => Assert.DoesNotContain("HIDDEN_SENTINEL", request.Feedback));
        Assert.All(requests, request => Assert.Same(parent, request.Parent));
        Assert.True(File.Exists(Path.Combine(Root, options.RunId, "attempt-1-build.json")));
        Assert.True(File.Exists(Path.Combine(Root, options.RunId, "attempt-2-test.json")));
        Assert.Equal(ledger.Snapshot().Admitted, ledger.Snapshot().Settled);
        Assert.Contains("compiler", File.ReadAllText(Path.Combine(Root, options.RunId, "ledger.json")));
    }

    [Fact]
    public async Task PublicFailuresExhaustBoundAndNeverCallHiddenVerifier()
    {
        var compiler = CompilerTests.Compiler();
        var parent = CompilerTests.Source();
        int calls = 0;
        var result = await ProgramImprovement.RunAsync(parent, () => compiler, (_, _) =>
        {
            calls++;
            return new(Result(CompilerTests.Plan(compiler, parent, ("Helpers/Helper.cs", "return x + 1;", "return x;"))));
        }, Verify, (_, _) => throw new Exception("Hidden verifier must not run."), Ledger(), Options(1));
        Assert.Equal(2, calls);
        Assert.False(result.Promoted);
        Assert.Null(result.Candidate);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 10)]
    [InlineData(true, 20)]
    public async Task HiddenFailureEqualityAndRegressionNeverPromoteOrRepair(bool correct, double duration)
    {
        var compiler = CompilerTests.Compiler();
        var parent = CompilerTests.Source();
        int plans = 0, confirmations = 0;
        var result = await ProgramImprovement.RunAsync(parent, () => compiler, (_, _) =>
        {
            plans++;
            return new(Result(CompilerTests.Plan(compiler, parent, ("Helpers/Helper.cs", "return x + 1;", "return 1 + x;"))));
        }, Verify, (request, _) => new(Result(new VerificationReceipt(request.Nonce, request.Artifact.Fingerprint,
            "trusted-fixture-v1", ++confirmations == 1 || correct, confirmations == 1 ? 10 : duration, "secret"))), Ledger(), Options());
        Assert.False(result.Promoted);
        Assert.Equal(1, plans);
        Assert.Equal(2, confirmations);
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("nonce")]
    [InlineData("oracle")]
    [InlineData("nan")]
    [InlineData("zero")]
    [InlineData("feedback")]
    public async Task InvalidVerificationReceiptsFailClosed(string kind)
    {
        ProgramVerifier invalid = (request, _) => new(Result(new VerificationReceipt(
            kind == "nonce" ? "stale" : request.Nonce,
            kind == "artifact" ? "stale" : request.Artifact.Fingerprint,
            kind == "oracle" ? "changed" : "trusted-fixture-v1", true,
            kind == "nan" ? double.NaN : kind == "zero" ? 0 : 1,
            kind == "feedback" ? new string('x', 4097) : "")));
        var ledger = Ledger();
        await Assert.ThrowsAsync<InvalidDataException>(() => ProgramImprovement.RunAsync(CompilerTests.Source(),
            CompilerTests.Compiler, (_, _) => throw new Exception("No dispatch."), invalid, invalid, ledger, Options()));
        Assert.Equal(ledger.Snapshot().Admitted, ledger.Snapshot().Settled);
    }

    [Fact]
    public async Task BudgetRefusesDispatchAndMissingReceiptsRetainMaximum()
    {
        int calls = 0;
        await Assert.ThrowsAsync<EvolutionResourceBudgetException>(() => ProgramImprovement.RunAsync(CompilerTests.Source(),
            () => { calls++; return CompilerTests.Compiler(); }, (_, _) => throw new Exception(), Verify, Verify, Ledger(1), Options()));
        Assert.Equal(0, calls);
        var ledger = Ledger();
        await Assert.ThrowsAsync<IOException>(() => ProgramImprovement.RunAsync(CompilerTests.Source(), CompilerTests.Compiler,
            (_, _) => throw new IOException("Model disconnected."), Verify, Verify, ledger, Options()));
        Assert.Equal(ledger.Snapshot().Admitted, ledger.Snapshot().Settled);
        Assert.Equal(1, ledger.Snapshot().Unknown);
        Assert.Equal(10, ledger.Snapshot().Receipts.Last().Charged[ProgramImprovement.CostResource]);
    }

    [Fact]
    public async Task FailedReceiptCannotSmuggleSuccessAndOverrunsRemainCharged()
    {
        var compiler = CompilerTests.Compiler();
        var parent = CompilerTests.Source();
        var plan = CompilerTests.Plan(compiler, parent, ("Helpers/Helper.cs", "return x + 1;", "return 1 + x;"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProgramImprovement.RunAsync(parent, () => compiler,
            (_, _) => new(Result(plan, outcome: EvolutionResourceOutcome.Failed)), Verify, Verify, Ledger(), Options()));
        var ledger = Ledger();
        await Assert.ThrowsAsync<EvolutionResourceLimitExceededException>(() => ProgramImprovement.RunAsync(parent, () => compiler,
            (_, _) => new(Result(plan, 11)), Verify, Verify, ledger, Options()));
        Assert.True(ledger.Snapshot().MaximumViolated);
        Assert.Equal(11, ledger.Snapshot().Receipts.Last().Charged[ProgramImprovement.CostResource]);
    }

    [Fact]
    public async Task EvidenceRunCannotBeReused()
    {
        var options = Options(0);
        var compiler = CompilerTests.Compiler();
        var parent = CompilerTests.Source();
        ProgramPlanner planner = (_, _) => new(Result(CompilerTests.Plan(compiler, parent,
            ("Helpers/Helper.cs", "return x + 1;", "return x;"))));
        await ProgramImprovement.RunAsync(parent, () => compiler, planner, Verify, Verify, Ledger(), options);
        await Assert.ThrowsAsync<IOException>(() => ProgramImprovement.RunAsync(parent, () => compiler,
            planner, Verify, Verify, Ledger(), options));
    }

    [Fact]
    public async Task AlternativeCompilerCannotSubstituteStaleOrDifferentContractArtifacts()
    {
        var compiler = CompilerTests.Compiler();
        var parent = CompilerTests.Source();
        var plan = CompilerTests.Plan(compiler, parent, ("Helpers/Helper.cs", "return x + 1;", "return 1 + x;"));
        var stale = new SubstitutingCompiler(compiler, compiler.Build(parent).Artifact!);
        await Assert.ThrowsAsync<InvalidDataException>(() => ProgramImprovement.RunAsync(parent, () => stale,
            (_, _) => new(Result(plan)), Verify, Verify, Ledger(), Options()));
        var different = new SubstitutingCompiler(compiler, null);
        var result = await ProgramImprovement.RunAsync(parent, () => different, (_, _) => new(Result(plan)),
            Verify, (_, _) => throw new Exception("Hidden dispatch forbidden."), Ledger(), Options(0));
        Assert.False(result.Promoted);
    }

    private sealed class SubstitutingCompiler(CSharpProgramCompiler inner, ProgramArtifact? stale) : IProgramCompiler
    {
        private int _builds;
        public IReadOnlyList<EditTarget> Catalog(ProgramSnapshot source, CancellationToken token) => inner.Catalog(source, token);
        public ProgramSnapshot Apply(ProgramSnapshot source, PatchPlan plan, CancellationToken token) => inner.Apply(source, plan, token);
        public ProgramBuild Build(ProgramSnapshot source, CancellationToken token)
        {
            var build = inner.Build(source, token);
            if (++_builds == 1) return build;
            var artifact = build.Artifact!;
            return new(stale ?? new ProgramArtifact(source, artifact.CompilerFingerprint, "changed-api", artifact.GetImage()), "");
        }
    }
}
