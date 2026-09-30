using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class RunPolicyContractTests
{
    [Fact]
    public async Task FailFast_stops_at_the_first_failure_and_Continue_carries_on()
    {
        async Task<EvolutionRunResult<TestGenome>> Run(EvolutionFailurePolicy policy)
        {
            var options = new EvolutionEngineOptions
            {
                RunId = "failure-policy",
                Seed = 77,
                MaxEvaluationAttempts = 6,
                MaxProposals = 6,
                MaxGenerations = 6,
                ProposalBatchSize = 1,
                MaxDegreeOfParallelism = 1,
                MaxRetries = 0,
                CheckpointInterval = 0,
                FailurePolicy = policy
            };
            var engine = new EvolutionEngine<TestGenome>(new FailOnceEvolutionTask(), new IncrementVariation(),
                _ => new MapElitesArchive<TestGenome>(new[]
                {
                    new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Clamp)
                }), options);
            return await engine.RunAsync(new[] { new TestGenome(1), new TestGenome(2) });
        }

        // Every candidate's first attempt fails and retries are off, so every evaluation fails.
        EvolutionRunResult<TestGenome> failFast = await Run(EvolutionFailurePolicy.FailFast);
        Assert.Equal(EvolutionStopReason.CandidateFailure, failFast.StopReason);
        Assert.Equal(1, failFast.Counters.EvaluationAttempts);

        EvolutionRunResult<TestGenome> carryOn = await Run(EvolutionFailurePolicy.Continue);
        Assert.NotEqual(EvolutionStopReason.CandidateFailure, carryOn.StopReason);
        Assert.True(carryOn.Counters.EvaluationAttempts > 1);
    }

    [Fact]
    public void The_early_stopping_report_counts_every_outcome_a_reading_can_have()
    {
        // Each reading of the criterion ends in one of these outcomes, and the report must total each of them, so
        // an unmeasurable criterion can never pass for one that saw no improvement.
        // That the totals are right in a real run is checked in EvolutionEarlyStoppingCriterionTests, where improved
        // plus not-improved readings must equal the measured ones.
        foreach (string outcome in Enum.GetNames(typeof(EvolutionEarlyStoppingOutcome)))
        {
            System.Reflection.PropertyInfo? readings = typeof(EvolutionEarlyStoppingReport).GetProperty(outcome + "Readings");
            Assert.NotNull(readings);
            Assert.Equal(typeof(long), readings.PropertyType);
        }

        Assert.Equal(
            new[] { EvolutionEarlyStoppingOutcome.Improved, EvolutionEarlyStoppingOutcome.NotImproved, EvolutionEarlyStoppingOutcome.Unmeasurable },
            Enum.GetValues(typeof(EvolutionEarlyStoppingOutcome)).Cast<EvolutionEarlyStoppingOutcome>());
    }
}
