using AiDotNet.Evolution;
using AiDotNet.Evolution.Deployment;
using Xunit;

namespace AiDotNet.Tests.UnitTests.Evolution;

/// <summary>
/// US-25 (#43): a candidate that regresses the incumbent on held-out validation is not promoted.
/// </summary>
/// <remarks>
/// The story's metric is a count - promoted winners regressing the incumbent, target zero - so this is a
/// sweep over regression magnitudes rather than one example, and the count is what is asserted. A single
/// case cannot distinguish a promotion gate from an ordering coincidence, and a sweep without a control
/// arm cannot distinguish a gate from a lifecycle that promotes nothing at all.
/// </remarks>
public sealed partial class EvolutionDeploymentLifecycleTests
{
    /// <summary>Scores the incumbent at 1.0 and the candidate at <paramref name="candidateQuality"/>.</summary>
    private static Func<EvolutionDeployableArtifact, int, CancellationToken, ValueTask<EvolutionDeploymentMeasurement>>
        ScoredBySource(double candidateQuality) =>
        (artifact, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            // "base" is what the lifecycle's own factory produces for the incumbent; anything else is the
            // candidate under test.
            double quality = artifact.ReadProgram().Source == "base" ? 1.0 : candidateQuality;
            return new ValueTask<EvolutionDeploymentMeasurement>(Measurement(quality));
        };

    [Fact]
    public async Task No_candidate_that_regresses_the_incumbent_is_ever_promoted()
    {
        // The metric US-25 asks for. Maximizing, so an incumbent at 1.0 and a candidate below it is a
        // regression - swept from a hair's breadth to a collapse, because a gate that only catches the
        // obvious one is not a gate.
        var promoted = new List<double>();
        var rows = new List<(double Quality, EvolutionDeploymentOutcome Outcome, bool Activated)>();
        foreach (double candidateQuality in new[] { 0.999, 0.95, 0.9, 0.75, 0.5, 0.25, 0.0 })
        {
            EvolutionDeploymentDecision decision = await Lifecycle(evaluate: ScoredBySource(candidateQuality))
                .PromoteAsync(Program("candidate"));
            if (decision.Activated) promoted.Add(candidateQuality);
            rows.Add((candidateQuality, decision.Outcome, decision.Activated));
            Assert.Equal(EvolutionDeploymentOutcome.InsufficientImprovement, decision.Outcome);
        }

        Assert.Empty(promoted);
        RecordSweep(rows, incumbentQuality: 1.0);
    }

    /// <summary>Writes the sweep's own rows, so US-25's "0 of 7" is an artifact and not a number in an issue.</summary>
    /// <remarks>
    /// Written only when <c>EVOLUTION_PROMOTION_EVIDENCE</c> names a path, so an ordinary run writes
    /// nothing and the assertions above are unchanged either way. The evidence is emitted by the sweep
    /// that asserts it, never transcribed from a passing run - which is the failure the evidence census
    /// classifies as <c>Unsupported</c>.
    /// </remarks>
    private static void RecordSweep(
        IReadOnlyList<(double Quality, EvolutionDeploymentOutcome Outcome, bool Activated)> rows,
        double incumbentQuality)
    {
        string? path = Environment.GetEnvironmentVariable("EVOLUTION_PROMOTION_EVIDENCE");
        if (string.IsNullOrWhiteSpace(path)) return;

        var payload = new System.Text.StringBuilder();
        payload.Append('{').Append(ESC_NL);
        payload.Append(Field("story", "US-25 (#43): no promoted winner regresses the incumbent"));
        payload.Append(Field("what", "EvolutionDeploymentLifecycle.PromoteAsync over a candidate-quality sweep against one incumbent"));
        payload.Append(FormattableString.Invariant($"  {QUOTE}incumbent_quality{QUOTE}: {incumbentQuality:R},{ESC_NL}"));
        payload.Append(FormattableString.Invariant($"  {QUOTE}candidates{QUOTE}: {rows.Count},{ESC_NL}"));
        payload.Append(FormattableString.Invariant($"  {QUOTE}promoted{QUOTE}: {rows.Count(row => row.Activated)},{ESC_NL}"));
        payload.Append(FormattableString.Invariant($"  {QUOTE}rows{QUOTE}: [{ESC_NL}"));
        for (int index = 0; index < rows.Count; index++)
        {
            (double quality, EvolutionDeploymentOutcome outcome, bool activated) = rows[index];
            payload.Append(FormattableString.Invariant(
                $"    {{ {QUOTE}candidate_quality{QUOTE}: {quality:R}, {QUOTE}outcome{QUOTE}: {QUOTE}{outcome}{QUOTE}, {QUOTE}activated{QUOTE}: {(activated ? "true" : "false")} }}"));
            payload.Append(index == rows.Count - 1 ? ESC_NL : "," + ESC_NL);
        }

        payload.Append("  ]").Append(ESC_NL).Append('}').Append(ESC_NL);
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
        File.WriteAllText(full, payload.ToString());
    }

    private const string QUOTE = "\"";
    private const string ESC_NL = "\n";

    private static string Field(string name, string value) =>
        "  " + QUOTE + name + QUOTE + ": " + QUOTE + value + QUOTE + "," + ESC_NL;

    [Fact]
    public async Task A_candidate_that_improves_the_incumbent_is_promoted()
    {
        // The control arm. Without it the sweep above passes for a lifecycle that promotes nothing, and
        // the suite would be measuring its own fixture rather than the promotion policy.
        EvolutionDeploymentDecision decision = await Lifecycle(evaluate: ScoredBySource(2.0))
            .PromoteAsync(Program("candidate"));

        Assert.True(decision.Activated,
            "an unambiguously better candidate was not promoted, so the sweep above cannot be attributed "
            + "to the regression gate: outcome was " + decision.Outcome);
    }

    [Fact]
    public async Task A_candidate_that_merely_matches_the_incumbent_is_not_promoted_either()
    {
        // The boundary between the two arms above. "Not worse" is not "better", and a policy with a
        // minimum mean gain has to refuse a tie, or every neutral change churns the deployed artifact.
        EvolutionDeploymentDecision decision = await Lifecycle(evaluate: ScoredBySource(1.0))
            .PromoteAsync(Program("candidate"));

        Assert.False(decision.Activated);
        Assert.Equal(EvolutionDeploymentOutcome.InsufficientImprovement, decision.Outcome);
    }
}
