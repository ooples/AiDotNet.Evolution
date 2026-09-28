using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>V1-83 (#185), D8: trace options no test exercised. Each is set both ways and its effect observed.</summary>
public sealed class TraceOptionBehaviourTests
{
    private sealed class MetricTask : IEvolutionTask<TestGenome>
    {
        public string Id => "metric-task";
        public string VersionHash => "metric-task-v1";
        public string EvaluatorVersionHash => "metric-task-evaluator-v1";

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(new TestGenome(genome.Value), genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // Two metrics that change with every child, and a diagnostic on every result, so each option has something to act on.
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, value,
                descriptors: new Dictionary<string, double> { ["x"] = value },
                diagnostics: new[] { new EvolutionDiagnostic("note", "value " + value) },
                metrics: new Dictionary<string, double> { ["a"] = value, ["b"] = value * 2 }));
        }
    }

    private static async Task<(IReadOnlyList<EvolutionTraceRecord> Records, EvolutionTraceSummary Summary)> Trace(Action<EvolutionTraceOptions> configure)
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "trace.jsonl");
        var descriptors = new[] { new EvolutionDescriptorDefinition("x", 0, 100, 20, EvolutionOutOfRangePolicy.Clamp) };
        var options = new EvolutionTraceOptions { Enabled = true, Path = path };
        configure(options);
        EvolutionTraceSummary summary;
        using (var tracer = new EvolutionTraceObserver<TestGenome>(options, "trace-options", descriptors))
        {
            var engine = new EvolutionEngine<TestGenome>(new MetricTask(), new IncrementVariation(), _ => new MapElitesArchive<TestGenome>(descriptors),
                new EvolutionEngineOptions
                {
                    RunId = "trace-options",
                    Seed = 1,
                    MaxEvaluationAttempts = 6,
                    MaxProposals = 12,
                    MaxGenerations = 12,
                    ProposalBatchSize = 1,
                    MaxDegreeOfParallelism = 1,
                    MigrationInterval = 0,
                    CheckpointInterval = 0
                }, observer: tracer);
            await engine.RunAsync(new[] { new TestGenome(1) });
            summary = tracer.Summary;
        }
        return (EvolutionTraceFile.Read(path).Records, summary);
    }

    [Fact]
    public async Task IncludeDescriptors_IncludeLineage_and_IncludeDiagnostics_each_remove_their_fields_when_cleared()
    {
        var (full, _) = await Trace(_ => { });
        var (bare, _) = await Trace(options =>
        {
            options.IncludeDescriptors = false;
            options.IncludeLineage = false;
            options.IncludeDiagnostics = false;
        });
        Assert.Contains(full, record => record.Descriptors.Count > 0 && record.Cell is not null);
        Assert.Contains(full, record => record.ParentIds.Count > 0);
        Assert.Contains(full, record => record.Diagnostics.Count > 0);
        Assert.All(bare, record => Assert.True(record.Descriptors.Count == 0 && record.Cell is null));
        Assert.All(bare, record => Assert.Empty(record.ParentIds));
        Assert.All(bare, record => Assert.Empty(record.Diagnostics));
        Assert.Equal(full.Count, bare.Count);
    }

    [Fact]
    public async Task MaxTrackedMetrics_marks_the_summary_truncated_once_more_metrics_arrive_than_it_tracks()
    {
        var (_, one) = await Trace(options => options.MaxTrackedMetrics = 1);
        var (_, plenty) = await Trace(_ => { });
        Assert.True(one.IsTruncated);
        Assert.False(plenty.IsTruncated);
    }
}
