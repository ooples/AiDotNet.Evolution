using AiDotNet.Evolution;
using AiDotNet.Evolution.Deployment;
using Xunit;

namespace AiDotNet.Tests.UnitTests.Evolution;

/// <summary>
/// US-03 (#21) second metric: finalists independently verified on held-out and adversarial cases, 100%.
/// </summary>
/// <remarks>
/// "100%" is a coverage claim, so what has to be counted is the proportion of decisions that retained
/// their verification evidence - including the ones that refused. A gate that verifies only the candidates
/// it ends up promoting leaves every rejection unexplainable afterwards, which is the state this metric
/// exists to rule out.
/// </remarks>
public sealed partial class EvolutionDeploymentLifecycleTests
{
    /// <summary>Scores the incumbent at 1.0 and the candidate at <paramref name="quality"/>.</summary>
    private static Func<EvolutionDeployableArtifact, int, CancellationToken, ValueTask<EvolutionDeploymentMeasurement>>
        VerificationScored(double quality, bool correct = true, bool fresh = true) =>
        (artifact, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            bool incumbent = artifact.ReadProgram().Source == "base";
            return new ValueTask<EvolutionDeploymentMeasurement>(
                incumbent ? Measurement(1.0) : Measurement(quality, correct, fresh));
        };

    [Theory]
    // A promotion, a refusal on quality, a refusal because the candidate failed its correctness check, and
    // a refusal because the measurement was not fresh. Every one is a finalist that was considered.
    [InlineData(2.0, true, true, true)]
    [InlineData(0.5, true, true, false)]
    [InlineData(2.0, false, true, false)]
    [InlineData(2.0, true, false, false)]
    public async Task Every_finalist_decision_retains_its_verification_evidence(
        double quality, bool correct, bool fresh, bool expectActivated)
    {
        EvolutionDeploymentDecision decision = await Lifecycle(evaluate: VerificationScored(quality, correct, fresh))
            .PromoteAsync(Program("candidate"));

        Assert.Equal(expectActivated, decision.Activated);
        // The coverage claim: evidence is retained whichever way the decision went, so a refusal can be
        // audited later rather than being a verdict with nothing behind it.
        Assert.False(string.IsNullOrWhiteSpace(decision.EvidenceId),
            "no verification evidence was retained for outcome " + decision.Outcome);
    }

    [Fact]
    public async Task The_retained_evidence_is_readable_and_names_both_sides_of_the_comparison()
    {
        // Coverage is not enough on its own: an evidence id pointing at something unreadable, or at a
        // comparison against an unnamed incumbent, would satisfy the count above and verify nothing.
        EvolutionDeploymentArtifactRegistry registry = Registry();
        var lifecycle = new EvolutionDeploymentLifecycle(registry, Envelope(), Policy(),
            envelope => Program("base", envelope), VerificationScored(2.0));

        EvolutionDeploymentDecision decision = await lifecycle.PromoteAsync(Program("candidate"));

        Assert.True(decision.Activated);
        string json = System.Text.Encoding.UTF8.GetString(registry.ReadEvidence(decision.EvidenceId!));
        using System.Text.Json.JsonDocument evidence = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(Program("candidate").Id, evidence.RootElement.GetProperty("CandidateId").GetString());
        Assert.Equal(Program("base").Id, evidence.RootElement.GetProperty("IncumbentId").GetString());
        // Paired measurements, not a single number: the policy's p-value has nothing to work on otherwise.
        Assert.True(evidence.RootElement.GetProperty("Pairs").GetArrayLength() > 1);
    }
}
