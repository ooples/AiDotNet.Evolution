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

    private static async Task<(IReadOnlyList<EvolutionTraceRecord> Records, EvolutionTraceSummary Summary, EvolutionTraceFormat Format)> Trace(Action<EvolutionTraceOptions> configure)
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
        var file = EvolutionTraceFile.Read(path);
        return (file.Records, summary, file.Format);
    }

    [Fact]
    public async Task IncludeDescriptors_IncludeLineage_and_IncludeDiagnostics_each_remove_only_their_own_fields()
    {
        // Each flag is cleared on its own, so an observer that wired one flag to another's fields fails here.
        var (full, _, _) = await Trace(_ => { });
        var (noDescriptors, _, _) = await Trace(options => options.IncludeDescriptors = false);
        var (noLineage, _, _) = await Trace(options => options.IncludeLineage = false);
        var (noDiagnostics, _, _) = await Trace(options => options.IncludeDiagnostics = false);

        Assert.Contains(full, HasDescriptors);
        Assert.Contains(full, HasLineage);
        Assert.Contains(full, HasDiagnostics);

        Assert.DoesNotContain(noDescriptors, HasDescriptors);
        Assert.Contains(noDescriptors, HasLineage);
        Assert.Contains(noDescriptors, HasDiagnostics);

        Assert.Contains(noLineage, HasDescriptors);
        Assert.DoesNotContain(noLineage, HasLineage);
        Assert.Contains(noLineage, HasDiagnostics);

        Assert.Contains(noDiagnostics, HasDescriptors);
        Assert.Contains(noDiagnostics, HasLineage);
        Assert.DoesNotContain(noDiagnostics, HasDiagnostics);

        Assert.Equal(full.Count, noDescriptors.Count);
        Assert.Equal(full.Count, noLineage.Count);
        Assert.Equal(full.Count, noDiagnostics.Count);
    }

    [Fact]
    public async Task MaxTrackedMetrics_limits_the_summary_without_dropping_records()
    {
        var (oneRecords, one, _) = await Trace(options => options.MaxTrackedMetrics = 1);
        var (plentyRecords, plenty, _) = await Trace(_ => { });

        Assert.True(one.IsMetricSummaryTruncated);
        Assert.False(one.IsTruncated);
        Assert.Equal(0, one.RecordsDropped);
        Assert.Single(one.TotalMetricDeltas);
        Assert.Equal(plentyRecords.Count, oneRecords.Count);
        Assert.Equal(plenty.RecordsWritten, one.RecordsWritten);

        Assert.False(plenty.IsMetricSummaryTruncated);
        Assert.False(plenty.IsTruncated);
        Assert.Equal(2, plenty.TotalMetricDeltas.Count);
    }

    [Fact]
    public async Task Format_decides_how_the_trace_file_is_written()
    {
        var (lines, linesSummary, linesFormat) = await Trace(_ => { });
        var (document, documentSummary, documentFormat) = await Trace(options => options.Format = EvolutionTraceFormat.Json);

        Assert.Equal(EvolutionTraceFormat.JsonLines, linesFormat);
        Assert.Equal(EvolutionTraceFormat.JsonLines, linesSummary.Format);
        Assert.Equal(EvolutionTraceFormat.Json, documentFormat);
        Assert.Equal(EvolutionTraceFormat.Json, documentSummary.Format);
        // The format changes the encoding, not the content.
        Assert.Equal(lines.Count, document.Count);
    }
    private static bool HasDescriptors(EvolutionTraceRecord record) => record.Descriptors.Count > 0 || record.Cell is not null;

    private static bool HasLineage(EvolutionTraceRecord record) => record.ParentIds.Count > 0;

    private static bool HasDiagnostics(EvolutionTraceRecord record) => record.Diagnostics.Count > 0;
}