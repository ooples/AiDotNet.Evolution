#if NET8_0_OR_GREATER
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>The program task's reference picks for its relative descriptors (E-4: OpenEvolve's diversity axis).</summary>
public sealed class ProgramDescriptorRebaseTests
{
    // ---- program task -----------------------------------------------------------------------------------------

    [Fact]
    public void Frozen_picks_once_when_the_population_reaches_the_reference_size_starting_from_the_best()
    {
        ProgramEvolutionTask task = ProgramTask(new ProgramDescriptorRebaseOptions { ReferenceSize = 3 });
        var islands = Islands(("a", "x = 1\n", 0.9), ("b", "y = 2\n", 0.2));

        Assert.False(task.TryRebase(2, islands));
        Assert.Empty(task.RebaseReferences);

        islands = Islands(("a", "x = 1\n", 0.9), ("b", "y = 2\n", 0.2), ("c", "long_name = [1, 2, 3]\nz = 4\n", 0.1),
            ("d", "x = 10\n", 0.5));
        // Two evaluations are not yet the reference size; the four programs the islands now hold are.
        Assert.True(task.TryRebase(2, islands));
        // The best first, then the farthest from it (two lines and a new character set), then the farthest from both.
        Assert.Equal(new[] { "x = 1", "long_name = [1, 2, 3]\nz = 4", "y = 2" }, task.RebaseReferences.Select(Trim));

        // Frozen: later batches never re-pick, however much the population moves.
        Assert.False(task.TryRebase(400, Islands(("e", "q = 9\n", 2.0), ("f", "w = 8\nv = 7\n", 1.0), ("g", "k\n", 0.0))));
        Assert.Equal(3, task.RebaseReferences.Count);
    }

    [Fact]
    public void Frozen_also_picks_once_the_run_has_completed_the_reference_size_in_evaluations()
    {
        // The islands may never hold that many distinct programs - a small grid keeps only its cells.
        ProgramEvolutionTask task = ProgramTask(new ProgramDescriptorRebaseOptions { ReferenceSize = 5 });
        var islands = Islands(("a", "x = 1\n", 0.9), ("b", "y = 2\n", 0.2));
        Assert.False(task.TryRebase(4, islands));
        Assert.True(task.TryRebase(5, islands));
        Assert.Equal(2, task.RebaseReferences.Count);
    }

    [Fact]
    public void Periodic_repicks_every_interval_and_reports_a_change_only_when_the_set_changed()
    {
        ProgramEvolutionTask task = ProgramTask(new ProgramDescriptorRebaseOptions
        {
            Mode = ProgramDescriptorRebaseMode.Periodic,
            ReferenceSize = 2,
            Interval = 10
        });
        var first = Islands(("a", "x = 1\n", 0.9), ("b", "y = 2\nz = 3\n", 0.2));
        Assert.True(task.TryRebase(2, first));
        Assert.False(task.TryRebase(11, first));   // not yet due
        Assert.False(task.TryRebase(12, first));   // due, but the same population picks the same set
        var moved = Islands(("c", "best = 100\n", 5.0), ("b", "y = 2\nz = 3\n", 0.2));
        Assert.False(task.TryRebase(21, moved));   // due at 22, counted from the last pick
        Assert.True(task.TryRebase(22, moved));
        Assert.Equal("best = 100", Trim(task.RebaseReferences[0]));
    }

    [Fact]
    public void The_pick_does_not_depend_on_island_or_entry_order_and_keeps_a_genomes_best_entry()
    {
        var options = new ProgramDescriptorRebaseOptions { ReferenceSize = 3 };
        var programs = new[] { ("a", "x = 1\n", 0.5), ("b", "y = 22\n", 0.5), ("c", "zz = 3\nw = 1\n", 0.5), ("d", "k = 4\n", 0.1) };
        ProgramEvolutionTask forward = ProgramTask(options), backward = ProgramTask(options);
        forward.TryRebase(4, Islands(programs));
        backward.TryRebase(4, new[]
        {
            Islands(Enumerable.Reverse(programs).ToArray())[0],
            // The same genome on a second island, weaker: it must not change who starts the walk.
            Islands(("a", "x = 1\n", 0.0))[0]
        });
        Assert.Equal(forward.RebaseReferences, backward.RebaseReferences);
        // Equal best qualities: the ordinally smallest genome identifier starts the walk.
        string start = new[] { "a", "b", "c" }.Select(id => programs.Single(p => p.Item1 == id))
            .OrderBy(p => new ProgramGenome(p.Item2, ProgramLanguage.Python).Id, StringComparer.Ordinal).First().Item2;
        Assert.Equal(Trim(start), Trim(forward.RebaseReferences[0]));

        // A genome on two islands counts with its better entry, whichever island comes last.
        ProgramEvolutionTask duplicated = ProgramTask(options);
        duplicated.TryRebase(4, new[] { Islands(programs)[0], Islands(("d", "k = 4\n", 0.9))[0], Islands(("d", "k = 4\n", 0.0))[0] });
        Assert.Equal("k = 4", Trim(duplicated.RebaseReferences[0]));
    }

    [Fact]
    public void Measurement_follows_the_current_references_and_survives_a_checkpoint()
    {
        var options = new ProgramDescriptorRebaseOptions { ReferenceSize = 2 };
        ProgramEvolutionTask task = ProgramTask(options);
        var probe = new ProgramGenome("probe = 12345\nmore = 1\n", ProgramLanguage.Python);
        double before = task.MeasureRebasable(probe)["diversity"];
        Assert.Equal(new[] { "diversity" }, task.MeasureRebasable(probe).Keys);

        Assert.True(task.TryRebase(2, Islands(("a", "probe = 12345\nmore = 1\n", 0.9), ("b", "q\n", 0.1))));
        double after = task.MeasureRebasable(probe)["diversity"];
        Assert.NotEqual(before, after);

        ProgramEvolutionTask restored = ProgramTask(options);
        restored.RestoreRebaseState(task.CaptureRebaseState());
        Assert.Equal(task.RebaseReferences, restored.RebaseReferences);
        Assert.Equal(after, restored.MeasureRebasable(probe)["diversity"]);
        // Restored as frozen-and-done: it does not pick again.
        Assert.False(restored.TryRebase(99, Islands(("z", "zzz\n", 9.0))));

        Assert.Throws<InvalidDataException>(() => restored.RestoreRebaseState("{not json"));
        Assert.Throws<InvalidDataException>(() => restored.RestoreRebaseState("{\"Schema\":\"other\",\"RebasedAt\":1,\"References\":[]}"));
        Assert.Throws<InvalidDataException>(() => ProgramTask(new ProgramDescriptorRebaseOptions { ReferenceSize = 1 })
            .RestoreRebaseState(task.CaptureRebaseState()));
    }

    [Fact]
    public void The_policy_joins_the_task_identity_only_when_there_is_something_to_rebase()
    {
        string plain = ProgramTask(null).VersionHash;
        Assert.NotEqual(plain, ProgramTask(new ProgramDescriptorRebaseOptions()).VersionHash);
        Assert.NotEqual(ProgramTask(new ProgramDescriptorRebaseOptions()).VersionHash,
            ProgramTask(new ProgramDescriptorRebaseOptions { Mode = ProgramDescriptorRebaseMode.Periodic }).VersionHash);

        var lengthOnly = new ProgramEvolutionTask(new FixedEvaluator(), new ProgramDescriptorSet(new ProgramLengthDescriptor()),
            new ProgramTaskOptions { Language = ProgramLanguage.Python }, rebase: new ProgramDescriptorRebaseOptions());
        Assert.False(lengthOnly.RebasesDescriptors);
        Assert.Equal(new ProgramEvolutionTask(new FixedEvaluator(), new ProgramDescriptorSet(new ProgramLengthDescriptor()),
            new ProgramTaskOptions { Language = ProgramLanguage.Python }).VersionHash, lengthOnly.VersionHash);

        Assert.Throws<ArgumentOutOfRangeException>(() => ProgramTask(new ProgramDescriptorRebaseOptions { ReferenceSize = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProgramTask(new ProgramDescriptorRebaseOptions { Interval = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProgramTask(new ProgramDescriptorRebaseOptions { Mode = (ProgramDescriptorRebaseMode)5 }));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static string Trim(string source) => source.TrimEnd('\n');

    private static ProgramEvolutionTask ProgramTask(ProgramDescriptorRebaseOptions? rebase) => new(
        new FixedEvaluator(),
        new ProgramDescriptorSet(new ProgramLengthDescriptor(),
            new ProgramDiversityDescriptor(new[] { new ProgramGenome("seed = 0\n", ProgramLanguage.Python) })),
        new ProgramTaskOptions { Language = ProgramLanguage.Python },
        rebase: rebase);

    private static IReadOnlyList<IReadOnlyList<EvolutionArchiveEntry<ProgramGenome>>> Islands(
        params (string Name, string Source, double Quality)[] programs)
    {
        var entries = new List<EvolutionArchiveEntry<ProgramGenome>>();
        long id = 0;
        foreach ((string _, string source, double quality) in programs)
        {
            var genome = new ProgramGenome(source, ProgramLanguage.Python);
            var lineage = new EvolutionLineage(null, null, "test", null, 0, 0, (ulong)id);
            var candidate = new EvolutionCandidate<ProgramGenome>(id, new EvolutionCanonicalGenome<ProgramGenome>(genome, genome.Id), lineage);
            var evaluation = new EvolutionEvaluation(id, genome.Id, EvolutionEvaluationStatus.Completed, quality,
                EvolutionOptimizationDirection.Maximize, new Dictionary<string, double> { ["length"] = source.Length },
                Array.Empty<double>(), Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 0), lineage,
                EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");
            entries.Add(new EvolutionArchiveEntry<ProgramGenome>(new EvolutionCellKey(new[] { (int)id }), candidate, evaluation));
            id++;
        }
        return new IReadOnlyList<EvolutionArchiveEntry<ProgramGenome>>[] { entries };
    }

    private sealed class FixedEvaluator : IProgramFitnessEvaluator
    {
        public string Id => "fixed";
        public string VersionHash => "fixed-v1";
        public ValueTask<EvolutionTaskResult> EvaluateAsync(ProgramGenome candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default) =>
            new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 0.5));
    }
}
#endif
