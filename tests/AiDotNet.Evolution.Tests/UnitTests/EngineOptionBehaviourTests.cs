using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-83 (#185), D8: engine options that no test exercised before. Each test sets the option both ways and observes
/// the difference, so a declared option that does nothing fails here.
/// </summary>
public sealed class EngineOptionBehaviourTests
{
    private static EvolutionEngineOptions Options(int budget = 12) => new()
    {
        RunId = "option-behaviour", Seed = 3, MaxEvaluationAttempts = budget, MaxProposals = budget * 2, MaxGenerations = budget * 2,
        ProposalBatchSize = 2, MaxDegreeOfParallelism = 1, MigrationInterval = 0, CheckpointInterval = 0
    };

    private static MapElitesArchive<TestGenome> Archive() =>
        new(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 20, EvolutionOutOfRangePolicy.Clamp) });

    // Completes with the value as quality, except that odd values fail with a diagnostic, and every evaluation reports
    // the given artifacts.
    private sealed class OddFailTask(params EvolutionArtifact[] artifacts) : IEvolutionTask<TestGenome>
    {
        public string Id => "odd-fail";
        public string VersionHash => "odd-fail-v1";
        public string EvaluatorVersionHash => "odd-fail-evaluator-v1";

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(new TestGenome(genome.Value), genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            return new(value % 2 == 1
                ? EvolutionTaskResult.Failed("odd", "odd value " + value)
                : new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, value,
                    descriptors: new Dictionary<string, double> { ["x"] = value }, artifacts: artifacts));
        }
    }

    [Fact]
    public async Task FailurePolicy_fail_fast_stops_at_the_first_failure_and_continue_runs_on()
    {
        EvolutionEngineOptions fast = Options();
        fast.FailurePolicy = EvolutionFailurePolicy.FailFast;
        EvolutionRunResult<TestGenome> stopped = await new EvolutionEngine<TestGenome>(new OddFailTask(), new IncrementVariation(), _ => Archive(), fast)
            .RunAsync(new[] { new TestGenome(0) });
        EvolutionRunResult<TestGenome> ran = await new EvolutionEngine<TestGenome>(new OddFailTask(), new IncrementVariation(), _ => Archive(), Options())
            .RunAsync(new[] { new TestGenome(0) });
        Assert.Equal(EvolutionStopReason.CandidateFailure, stopped.StopReason);
        Assert.NotEqual(EvolutionStopReason.CandidateFailure, ran.StopReason);
        Assert.True(ran.Counters.EvaluationAttempts > stopped.Counters.EvaluationAttempts);
    }

    [Fact]
    public async Task MaxRetainedFailures_bounds_the_failure_diagnostics_kept()
    {
        EvolutionEngineOptions bounded = Options(budget: 20);
        bounded.MaxRetainedFailures = 2;
        EvolutionRunResult<TestGenome> kept = await new EvolutionEngine<TestGenome>(new OddFailTask(), new IncrementVariation(), _ => Archive(), bounded)
            .RunAsync(new[] { new TestGenome(0) });
        EvolutionRunResult<TestGenome> unbounded = await new EvolutionEngine<TestGenome>(new OddFailTask(), new IncrementVariation(), _ => Archive(), Options(budget: 20))
            .RunAsync(new[] { new TestGenome(0) });
        Assert.Equal(2, kept.RetainedFailures.Count);
        Assert.True(unbounded.RetainedFailures.Count > 2, $"only {unbounded.RetainedFailures.Count} failures occurred");
    }

    [Fact]
    public async Task Artifacts_SanitizeSecrets_redacts_a_key_only_when_set()
    {
        string secret = "sk-" + new string('q', 48);
        async Task<string> Stored(bool sanitize)
        {
            EvolutionEngineOptions options = Options(budget: 1);
            options.Artifacts.Enabled = true;
            options.Artifacts.SanitizeSecrets = sanitize;
            var observer = new EvaluationRecordingObserver();
            await new EvolutionEngine<TestGenome>(new OddFailTask(new EvolutionArtifact("log", "token " + secret)), new IncrementVariation(),
                _ => Archive(), options, observer: observer).RunAsync(new[] { new TestGenome(0) });
            return observer.Evaluations[0].Artifacts.Single().Text;
        }
        Assert.DoesNotContain(secret, await Stored(sanitize: true), StringComparison.Ordinal);
        Assert.Contains(secret, await Stored(sanitize: false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Artifacts_DeliverToNextProposal_hands_a_parent_its_output_only_when_set()
    {
        async Task<int> Delivered(bool deliver)
        {
            EvolutionEngineOptions options = Options(budget: 4);
            options.Artifacts.Enabled = true;
            options.Artifacts.DeliverToNextProposal = deliver;
            var variation = new ArtifactRecordingVariation();
            await new EvolutionEngine<TestGenome>(new OddFailTask(new EvolutionArtifact("log", "ran")), variation, _ => Archive(), options)
                .RunAsync(new[] { new TestGenome(0) });
            return variation.Received.Count;
        }
        Assert.True(await Delivered(deliver: true) > 0);
        Assert.Equal(0, await Delivered(deliver: false));
    }

    [Fact]
    public async Task PreventRepeatedMigration_stops_an_arrival_from_being_sent_on()
    {
        // Three islands in a ring, each seeded with one program that every proposal repeats. An island's best can be a
        // program that arrived by migration; sending it on again carries it round the whole ring. Prevented, an arrival is
        // never a migration source, so each program reaches its origin and one neighbour and no further.
        async Task<int> WidestSpread(bool prevent)
        {
            EvolutionEngineOptions options = Options(budget: 30);
            options.IslandCount = 3;
            options.MigrationInterval = 1;
            options.MigrantsPerIsland = 1;
            options.MigrationRate = 1.0;
            options.MigrationTopology = EvolutionMigrationTopology.Ring;
            options.PreventRepeatedMigration = prevent;
            EvolutionRunResult<TestGenome> result = await new EvolutionEngine<TestGenome>(new OddFailTask(), new RepeatParentVariation(),
                _ => Archive(), options).RunAsync(new[] { new TestGenome(10), new TestGenome(20), new TestGenome(30) });
            return result.Islands.SelectMany(island => island.Entries.Select(entry => entry.Evaluation.GenomeId).Distinct())
                .GroupBy(id => id).Max(group => group.Count());
        }
        Assert.Equal(2, await WidestSpread(prevent: true));
        Assert.Equal(3, await WidestSpread(prevent: false));
    }
    private sealed class RepeatParentVariation : IVariationOperator<TestGenome>
    {
        public string Id => "repeat-parent";
        public string VersionHash => "repeat-parent-v1";

        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            new(new TestGenome(context.Parent.Candidate.CanonicalGenome.Genome.Value));
    }
}
