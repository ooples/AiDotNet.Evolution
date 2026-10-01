using System.Diagnostics;
using System.Text;
using Xunit;
using static AiDotNet.Evolution.CSharp.Tests.CompilerTestSupport;

namespace AiDotNet.Evolution.CSharp.Tests;

/// <summary>
/// Each price is raised on its own and the settled cost must rise by exactly that price times the work the
/// ledger counted for it, so a price that is read but multiplied by the wrong counter fails here too.
/// </summary>
public sealed class CSharpCostOptionBehaviourTests
{
    private static readonly string[] Counters =
        { "model_calls", "parse_calls", "build_calls", "audit_calls", "input_tokens", "output_tokens" };

    [Fact]
    public async Task Each_price_charges_its_own_counter()
    {
        (decimal baseline, IReadOnlyDictionary<string, decimal> work) = await Spend(_ => { });
        foreach (string counter in Counters) Assert.True(work[counter] > 0, counter + " was never exercised");

        const decimal Raise = 0.5m;
        Assert.Equal(baseline + Raise * work["model_calls"],
            (await Spend(options => options.ModelCallCostUnits += Raise)).Cost);
        Assert.Equal(baseline + Raise * work["parse_calls"],
            (await Spend(options => options.ParseCostUnits += Raise)).Cost);
        Assert.Equal(baseline + Raise * work["build_calls"],
            (await Spend(options => options.CompilationCostUnits += Raise)).Cost);
        Assert.Equal(baseline + Raise * work["audit_calls"],
            (await Spend(options => options.AuditCostUnits += Raise)).Cost);
        Assert.Equal(baseline + 0.001m * work["input_tokens"],
            (await Spend(options => options.InputTokenCostUnits += 0.001m)).Cost);
        Assert.Equal(baseline + 0.001m * work["output_tokens"],
            (await Spend(options => options.OutputTokenCostUnits += 0.001m)).Cost);
        Assert.Equal(baseline + Raise, (await Spend(options => options.SetupCostUnits += Raise)).Cost);
    }

    [Fact]
    public void CompilationTimeoutSeconds_bounds_a_slow_compile()
    {
        CSharpPatchAttempt Attempt(int seconds, int depth)
        {
            CSharpProgramEvolutionOptions options = Options();
            options.CompilationTimeoutSeconds = seconds;
            var compiler = new CSharpPatchCompiler(options, Program());
            CSharpPatchPreparation prepared = compiler.Prepare(Parent(SlowSource(depth)), CancellationToken.None);
            return compiler.Apply(prepared, Patch(prepared), CancellationToken.None);
        }

        // Load and JIT the compiler first so the timed arms measure binding, not start-up.
        Assert.Equal(string.Empty, Attempt(30, depth: 1).Feedback);

        // Calibrate to this host: deepen until an unbounded compile takes long enough to discriminate a one-second
        // bound. Each level roughly doubles binding time, so the level that first passes the target stays well inside
        // the thirty-second patient bound on a slow host, and a fast host simply goes deeper.
        TimeSpan target = TimeSpan.FromSeconds(3);
        int depth = FirstDepth;
        CSharpPatchAttempt patient;
        TimeSpan full;
        while (true)
        {
            var timer = Stopwatch.StartNew();
            patient = Attempt(30, depth);
            full = timer.Elapsed;
            Assert.DoesNotContain("timed out", patient.Feedback, StringComparison.Ordinal);
            if (full > target) break;
            Assert.True(depth < LastDepth, "Even depth " + depth + " compiled in " + full + "; the slow source no longer discriminates.");
            depth++;
        }

        var bounded = Stopwatch.StartNew();
        CSharpPatchAttempt limited = Attempt(1, depth);
        Assert.Contains("timed out", limited.Feedback, StringComparison.Ordinal);
        Assert.True(bounded.Elapsed < full, "Timed out after " + bounded.Elapsed + " but the full compile took " + full);
    }

    private const int FirstDepth = 8;
    private const int LastDepth = 20;
    // Two overloads that differ only in the lambda's parameter type force the binder to try both at every
    // level; only the int reading of every parameter type-checks the innermost body, so the call is not
    // ambiguous, yet binding cost grows exponentially with depth while the source stays a few hundred characters.
    private static string SlowSource(int depth)
    {
        var body = new StringBuilder();
        for (int level = 0; level < depth; level++) body.Append("M(x").Append(level).Append(" => ");
        for (int level = 0; level < depth; level++) body.Append("x").Append(level).Append(" * 0 + ");
        body.Append("1");
        for (int level = 0; level < depth; level++) body.Append(')');
        return "public static class C {\n" +
               "    static int M(System.Func<int, int> f) => 0;\n" +
               "    static int M(System.Func<string, int> f) => 0;\n" +
               "    public static int F() { return " + body + "; }\n" +
               "}";
    }

    private static async Task<(decimal Cost, IReadOnlyDictionary<string, decimal> Work)> Spend(
        Action<CSharpProgramEvolutionOptions> configure)
    {
        CSharpProgramEvolutionOptions options = Options();
        configure(options);
        EvolutionResourceLedger ledger = Ledger(1_000_000m);
        CSharpProposalSource source = CSharpProposalSource.Create(new ScriptedClient(), options, Program(), ledger);
        await new ResourceMeteredVariationOperator<Programs.ProgramGenome>(
            source, ledger, source.MaximumProposalResources, source.CostUnitVersionHash).ProposeAsync(Context());
        IReadOnlyDictionary<string, decimal> spent = ledger.Snapshot().Spent;
        return (spent["cost_units"], spent);
    }
}
