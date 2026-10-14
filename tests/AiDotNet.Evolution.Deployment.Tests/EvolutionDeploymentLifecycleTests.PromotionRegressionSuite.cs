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
        foreach (double candidateQuality in new[] { 0.999, 0.95, 0.9, 0.75, 0.5, 0.25, 0.0 })
        {
            EvolutionDeploymentDecision decision = await Lifecycle(evaluate: ScoredBySource(candidateQuality))
                .PromoteAsync(Program("candidate"));
            if (decision.Activated) promoted.Add(candidateQuality);
            Assert.Equal(EvolutionDeploymentOutcome.InsufficientImprovement, decision.Outcome);
        }

        Assert.Empty(promoted);
    }

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
