using System.Diagnostics;
using System.Text.Json;
using AiDotNet.Evolution;

/// <summary>
/// V1-50 (#163): the ensemble's own cost per proposal, against the same null models called directly.
/// </summary>
/// <remarks>
/// A paired A/B in one process: the baseline draws a model with the same seeded stream and calls it
/// directly, so the arms differ only by the ensemble's draw, its per-model accounting and its
/// outcome routing. Both arms propose and then observe, because routing an outcome is part of what
/// a proposal costs here. Reporting the arm alone would charge the ensemble for the null model's
/// own call and for the context allocation, which it does not cause.
/// </remarks>
internal static class EnsembleOverhead
{
    internal static int Run(int proposals, int models, int repeats)
    {
        var rows = new List<object>();
        for (int repeat = 0; repeat < repeats; repeat++)
        {
            // Fresh sources each repeat, so neither arm inherits the other's warmed accounting.
            double arm = Measure(proposals, models, through: true);
            double baseline = Measure(proposals, models, through: false);
            rows.Add(new
            {
                Repeat = repeat,
                Models = models,
                Proposals = proposals,
                EnsembleMicrosecondsPerProposal = arm,
                DirectMicrosecondsPerProposal = baseline,
                OverheadMicrosecondsPerProposal = arm - baseline,
            });
        }

        Console.WriteLine(JsonSerializer.Serialize(new { Measurement = "ensemble-overhead", Rows = rows }));
        return 0;
    }

    private static double Measure(int proposals, int models, bool through)
    {
        var sources = Enumerable.Range(0, models)
            .Select(i => new NullSource("m" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        var ensemble = new EvolutionWeightedModelEnsemble<int>(
            "ensemble", sources.Select(s => new EvolutionModelWeight<int>(s, 1.0)).ToArray());

        // One untimed pass pays for JIT, the archive entry's allocations and the first dictionary growth.
        Drive(ensemble, sources, proposals: 64, through, firstGeneration: 1);

        var clock = Stopwatch.StartNew();
        Drive(ensemble, sources, proposals, through, firstGeneration: 1_000);
        clock.Stop();
        return clock.Elapsed.TotalMilliseconds * 1000.0 / proposals;
    }

    private static void Drive(
        EvolutionWeightedModelEnsemble<int> ensemble, NullSource[] sources, int proposals, bool through, long firstGeneration)
    {
        var random = StableRandom.CreateStream(42, 0);
        for (int i = 0; i < proposals; i++)
        {
            long generation = firstGeneration + i;
            EvolutionVariationContext<int> context = Context(random, generation);
            if (through)
            {
                ensemble.ProposeAsync(context).GetAwaiter().GetResult();
                ensemble.Observe(Evaluation(generation), EvolutionArchiveInsertionResult.Inserted);
            }
            else
            {
                // The same draw, without the ensemble: one uniform from the same stream picks the
                // model, so the baseline pays for the stream read and the model's own call too.
                int index = (int)(context.Random.NextUInt32() % (uint)sources.Length);
                sources[index].ProposeAsync(context).GetAwaiter().GetResult();
                sources[index].Observe(Evaluation(generation), EvolutionArchiveInsertionResult.Inserted);
            }
        }
    }

    private static EvolutionVariationContext<int> Context(StableRandom random, long generation)
    {
        EvolutionEvaluation measured = Evaluation(0);
        var parent = new EvolutionArchiveEntry<int>(
            new EvolutionCellKey(new[] { 0 }),
            new EvolutionCandidate<int>(0, new EvolutionCanonicalGenome<int>(1, "g0"), measured.Lineage),
            measured);
        return new EvolutionVariationContext<int>(
            parent, Array.Empty<EvolutionArchiveEntry<int>>(), random, generation, island: 0);
    }

    private static EvolutionEvaluation Evaluation(long generation)
        => new(generation, "g" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EvolutionEvaluationStatus.Completed, 0.5, EvolutionOptimizationDirection.Maximize,
            new Dictionary<string, double> { ["x"] = 1 }, Array.Empty<double>(), Array.Empty<double>(),
            new EvolutionEvaluationCost(TimeSpan.Zero, 1, 2),
            new EvolutionLineage(null, null, "overhead", null, generation, 0, (ulong)generation),
            EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "evaluator", "config");

    /// <summary>A model that returns immediately, so the measured time is the ensemble's own work.</summary>
    private sealed class NullSource(string id) : ICostedEvolutionProposalSource<int>
    {
        private static readonly EvolutionResources Receipt = EvolutionResources.Of("cost_units", 1m);
        private int _proposed;

        public string Id { get; } = id;

        public string VersionHash => "null-model-v1";

        public ValueTask<EvolutionResourceResult<int>> ProposeAsync(
            EvolutionVariationContext<int> context, CancellationToken cancellationToken = default)
            => new(new EvolutionResourceResult<int>(++_proposed, Receipt));

        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
        {
        }

        public string CaptureState() => "{}";

        public void RestoreState(string state)
        {
        }
    }
}
