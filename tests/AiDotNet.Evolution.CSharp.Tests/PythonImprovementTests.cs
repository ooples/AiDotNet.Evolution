using System.Diagnostics;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests;

/// <summary>V1-22: the compiler-guided repair loop (US-17) end to end for a Python task.</summary>
public sealed class PythonImprovementTests
{
    private static readonly string Python = Environment.GetEnvironmentVariable("EVOLUTION_PYTHON")
        ?? (OperatingSystem.IsWindows() ? "python" : "python3");
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "evolution-v1-22-tests");

    private static EvolutionResourceResult<T> Result<T>(T value, decimal cost = 1) =>
        new(value, ProgramImprovement.Units(cost), EvolutionResourceOutcome.Completed);

    // Only this authored fixture runs; model-generated programs would go to an isolated sandbox instead.
    private static ValueTask<EvolutionResourceResult<VerificationReceipt>> Verify(VerificationRequest request, CancellationToken token)
    {
        string source = request.Artifact.Source.Files["solver.py"];
        var start = new ProcessStartInfo(Python) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string argument in new[] { "-I", "-c", "import sys; ns = {}; exec(sys.stdin.read(), ns); print(all(ns['solve'](x) == x + 1 for x in (-7, 0, 11)))" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Write(source);
        process.StandardInput.Close();
        bool correct = process.StandardOutput.ReadToEnd().Trim() == "True";
        process.WaitForExit();
        return new(Result(new VerificationReceipt(request.Nonce, request.Artifact.Fingerprint, "python-fixture-v1", correct, 10,
            correct ? "" : "Public test solve(x) != x + 1.")));
    }

    [Fact]
    public async Task Repairs_a_compile_error_then_a_public_test_failure_for_a_python_task()
    {
        var compiler = new PythonProgramCompiler(Python);
        var parent = new ProgramSnapshot(new Dictionary<string, string> { ["solver.py"] = "def solve(x):\n    return sum(1 for _ in range(1)) + x\n" });
        EditTarget target = compiler.Catalog(parent).Single(t => t.Kind == "statement");
        var requests = new List<SearchRequest>();
        int hidden = 0;
        ProgramPlanner planner = (request, _) =>
        {
            requests.Add(request);
            // ast.parse accepts `nonlocal y`; compile() rejects it, so attempt 1 is a genuine compiler failure.
            string replacement = request.Attempt switch { 1 => "nonlocal y", 2 => "return x", _ => "return x + 1" };
            return new(Result(new PatchPlan(parent.Fingerprint, "attempt " + request.Attempt, new[] { new SourceEdit(target, replacement) })));
        };
        ProgramVerifier heldOut = (request, _) => new(Result(new VerificationReceipt(request.Nonce, request.Artifact.Fingerprint,
            "python-fixture-v1", true, ++hidden == 1 ? 20 : 10, "HIDDEN_SENTINEL")));
        var options = new ImprovementOptions(Guid.NewGuid().ToString("N"), "Measured redundant generator work in solve.", "python-fixture-v1", Root, 2);
        var ledger = new EvolutionResourceLedger("python-repair", ProgramImprovement.Units(1000));

        ImprovementResult result = await ProgramImprovement.RunAsync(parent, () => compiler, planner, Verify, heldOut, ledger, options);

        Assert.True(result.Promoted);
        Assert.Equal(3, result.Attempts);
        Assert.Contains("SyntaxError", requests[1].Feedback);
        Assert.Contains("nonlocal", requests[1].Feedback);
        Assert.Contains("Public test", requests[2].Feedback);
        Assert.All(requests, request => Assert.DoesNotContain("HIDDEN_SENTINEL", request.Feedback));
        Assert.Equal(ledger.Snapshot().Admitted, ledger.Snapshot().Settled);
    }
}