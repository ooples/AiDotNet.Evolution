using System.Globalization;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// Relative descriptors that re-pick their references from the population, and the engine re-filing every archive
/// when they do (E-4: OpenEvolve's diversity axis, which our importer used to drop).
/// </summary>
public sealed class DescriptorRebaseTests
{
    // ---- engine ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(EvolutionDispatchMode.Batch)]
    [InlineData(EvolutionDispatchMode.Continuous)]
    public async Task Every_archive_and_the_elite_index_hold_the_current_reading_after_a_rebase(EvolutionDispatchMode dispatch)
    {
        var task = new AnchoredTask(interval: 4);
        EvolutionRunResult<TestGenome> result = await Run(task, Options(30, dispatch));

        Assert.True(task.Rebases >= 3, "the anchor moved " + task.Rebases + " times");
        // Every result the task produced carried a garbage 'rel'; only the engine's commit-time reading can fix it.
        var entries = result.Islands.SelectMany(island => island.Entries).ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.Equal(task.Expected(entry), entry.Evaluation.Descriptors["rel"]));
        Assert.All(entries, entry => Assert.Equal(KeyFor(result.Islands[0], entry.Evaluation.Descriptors), entry.Cell.StableKey));
    }

    [Fact]
    public async Task The_elite_index_is_rekeyed_so_selection_never_mixes_two_readings()
    {
        var task = new AnchoredTask(interval: 3);
        var options = Options(24, EvolutionDispatchMode.Batch);
        options.GlobalEliteCount = 50;
        options.SelectionPolicy = EvolutionSelectionPolicyKind.Ratio;
        var observed = new EliteObservingSelection(task.Expected);

        await new EvolutionEngine<TestGenome>(task, new IncrementVariation(), Archive, options, selection: observed)
            .RunAsync(new[] { new TestGenome(1), new TestGenome(40) });

        Assert.True(task.Rebases >= 2);
        Assert.True(observed.Checked > 20);
        Assert.Empty(observed.Mismatches);
    }

    [Fact]
    public async Task A_resumed_run_files_candidates_exactly_as_the_uninterrupted_run()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Run(new AnchoredTask(interval: 4), Options(14, EvolutionDispatchMode.Batch), store);
        var resumedTask = new AnchoredTask(interval: 4);
        EvolutionEngineOptions resume = Options(30, EvolutionDispatchMode.Batch);
        resume.Resume = true;
        EvolutionRunResult<TestGenome> resumed = await Run(resumedTask, resume, store);

        var wholeTask = new AnchoredTask(interval: 4);
        EvolutionRunResult<TestGenome> whole = await Run(wholeTask, Options(30, EvolutionDispatchMode.Batch),
            new InMemoryEvolutionCheckpointStore());

        Assert.Equal(wholeTask.CaptureRebaseState(), resumedTask.CaptureRebaseState());
        Assert.Equal(whole.StateHash, resumed.StateHash);
    }

    [Theory]
    [InlineData(false, true, "missing")]
    [InlineData(true, false, "does not rebase")]
    public async Task A_checkpoint_without_rebase_state_is_refused_for_a_rebasing_task_and_the_reverse(
        bool writerRebases, bool readerRebases, string reason)
    {
        // Same task identity either way, so only the rebase-state check can tell the two apart.
        var store = new InMemoryEvolutionCheckpointStore();
        await Run(new AnchoredTask(interval: 4, rebases: writerRebases), Options(6, EvolutionDispatchMode.Batch), store);
        EvolutionEngineOptions resume = Options(12, EvolutionDispatchMode.Batch);
        resume.Resume = true;

        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(
            () => Run(new AnchoredTask(interval: 4, rebases: readerRebases), resume, store));
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rebase_state_joins_the_run_state_hash()
    {
        // The salt is carried in the rebase state only; it never changes a reading.
        EvolutionRunResult<TestGenome> a = await Run(new AnchoredTask(interval: 4, salt: "a"), Options(12, EvolutionDispatchMode.Batch));
        EvolutionRunResult<TestGenome> again = await Run(new AnchoredTask(interval: 4, salt: "a"), Options(12, EvolutionDispatchMode.Batch));
        EvolutionRunResult<TestGenome> b = await Run(new AnchoredTask(interval: 4, salt: "b"), Options(12, EvolutionDispatchMode.Batch));

        Assert.Equal(a.StateHash, again.StateHash);
        Assert.NotEqual(a.StateHash, b.StateHash);
    }

    [Fact]
    public void A_rebasing_task_needs_archives_that_can_be_refiled()
    {
        var definition = new CentroidArchiveDefinition(new[]
        {
            new EvolutionDescriptorDefinition("x", 0, 1000, 10),
            new EvolutionDescriptorDefinition("rel", 0, 1000, 10)
        }, new[] { new[] { 0d, 0d }, new[] { 1d, 1d } });
        ArgumentException refused = Assert.Throws<ArgumentException>(() => new EvolutionEngine<TestGenome>(
            new AnchoredTask(interval: 4), new IncrementVariation(), _ => new CentroidArchive<TestGenome>(definition),
            Options(4, EvolutionDispatchMode.Batch)));
        Assert.Contains("IRemeasurableEvolutionArchive", refused.Message, StringComparison.Ordinal);

        // A task that declares nothing to rebase is an ordinary task, whatever the archive.
        _ = new EvolutionEngine<TestGenome>(new AnchoredTask(interval: 4, rebases: false), new IncrementVariation(),
            _ => new CentroidArchive<TestGenome>(definition), Options(4, EvolutionDispatchMode.Batch));
    }

    [Fact]
    public void Remeasuring_by_entry_keeps_the_axes_the_callback_does_not_replace()
    {
        var archive = new MapElitesArchive<TestGenome>(new[]
        {
            new EvolutionDescriptorDefinition("x", 0, 10, 10),
            new EvolutionDescriptorDefinition("rel", 0, 10, 10)
        });
        (EvolutionCandidate<TestGenome> candidate, EvolutionEvaluation evaluation) = MapElitesArchiveTests.Create(1, "a", 1, 2.5);
        archive.TryAdd(candidate, evaluation.WithDescriptors(new Dictionary<string, double> { ["x"] = 2.5, ["rel"] = 1.5 }));

        IRemeasurableEvolutionArchive<TestGenome> remeasurable = archive;
        remeasurable.RemeasureEntries(entry => new Dictionary<string, double>(entry.Evaluation.Descriptors.ToDictionary(p => p.Key, p => p.Value))
        {
            ["rel"] = 7.5
        });

        EvolutionArchiveEntry<TestGenome> moved = Assert.Single(archive.Entries);
        Assert.Equal((2.5, 7.5), (moved.Evaluation.Descriptors["x"], moved.Evaluation.Descriptors["rel"]));
        Assert.Equal(new[] { 2, 7 }, moved.Cell.Bins);
    }

    private static string KeyFor(IEvolutionArchiveView<TestGenome> archive, IReadOnlyDictionary<string, double> descriptors) =>
        new EvolutionCellKey(archive.Descriptors.Select(definition =>
            definition.TryGetBin(descriptors[definition.Name], out int bin) ? bin : throw new InvalidOperationException()).ToArray()).StableKey;

    private static IEvolutionArchive<TestGenome> Archive(int island) => new MapElitesArchive<TestGenome>(new[]
    {
        new EvolutionDescriptorDefinition("x", 0, 200, 20, EvolutionOutOfRangePolicy.Clamp),
        new EvolutionDescriptorDefinition("rel", 0, 200, 20, EvolutionOutOfRangePolicy.Clamp)
    });

    private static EvolutionEngineOptions Options(int attempts, EvolutionDispatchMode dispatch)
    {
        EvolutionEngineOptions options = EvolutionVariationOutcomeTests.Options(attempts, dispatch);
        options.IslandCount = 2;
        options.MigrationInterval = 3;
        options.MaxProposals = attempts;
        options.MaxGenerations = attempts;
        options.CheckpointInterval = 1;
        return options;
    }

    private static Task<EvolutionRunResult<TestGenome>> Run(IEvolutionTask<TestGenome> task, EvolutionEngineOptions options,
        IEvolutionCheckpointStore? store = null, bool twoAxes = true) =>
        new EvolutionEngine<TestGenome>(task, new IncrementVariation(), Archive, options, checkpointStore: store,
                genomeCodec: new TestGenomeCodec())
            .RunAsync(new[] { new TestGenome(1), new TestGenome(40) });

    /// <summary>'rel' is the distance to an anchor the task moves to the best archived value every few evaluations.
    /// Its own results always report rel = 199, so only the engine's commit-time reading can file them right.</summary>
    private sealed class AnchoredTask : IRebasingEvolutionTask<TestGenome>
    {
        private readonly SyntheticEvolutionTask _inner = new();
        private readonly int _interval;
        private int _anchor;
        private long _last;
        private string _salt;

        public AnchoredTask(int interval, bool rebases = true, string salt = "s")
        {
            _interval = interval;
            RebasesDescriptors = rebases;
            _salt = salt;
        }

        public int Rebases { get; private set; }
        public string Id => "anchored";
        public string VersionHash => "anchored-v1";
        public string EvaluatorVersionHash => "anchored-evaluator-v1";
        public bool RebasesDescriptors { get; }

        public double Expected(EvolutionArchiveEntry<TestGenome> entry) =>
            Math.Abs(entry.Candidate.CanonicalGenome.Genome.Value - _anchor);

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome,
            CancellationToken cancellationToken = default) => _inner.CanonicalizeAsync(genome, cancellationToken);

        public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            EvolutionTaskResult result = await _inner.EvaluateAsync(candidate, context, cancellationToken);
            return EvolutionTaskResult.Completed(result.Quality!.Value, new Dictionary<string, double>
            {
                ["x"] = result.Descriptors["x"],
                ["rel"] = 199
            });
        }

        public IReadOnlyDictionary<string, double> MeasureRebasable(TestGenome genome) =>
            new Dictionary<string, double> { ["rel"] = Math.Abs(genome.Value - _anchor) };

        public bool TryRebase(long completedEvaluations, IReadOnlyList<IReadOnlyList<EvolutionArchiveEntry<TestGenome>>> islands)
        {
            if (completedEvaluations - _last < _interval) return false;
            _last = completedEvaluations;
            int best = islands.SelectMany(island => island).Max(entry => entry.Candidate.CanonicalGenome.Genome.Value);
            if (best == _anchor) return false;
            _anchor = best;
            Rebases++;
            return true;
        }

        public string CaptureRebaseState() => string.Join(",", _anchor.ToString(CultureInfo.InvariantCulture),
            _last.ToString(CultureInfo.InvariantCulture), Rebases.ToString(CultureInfo.InvariantCulture), _salt);

        public void RestoreRebaseState(string state)
        {
            string[] parts = state.Split(',');
            if (parts.Length != 4) throw new InvalidDataException("bad anchored state");
            _anchor = int.Parse(parts[0], CultureInfo.InvariantCulture);
            _last = long.Parse(parts[1], CultureInfo.InvariantCulture);
            Rebases = int.Parse(parts[2], CultureInfo.InvariantCulture);
            _salt = parts[3];
        }
    }

    /// <summary>Checks, at the moment each selection is handed the elite index, that every record carries the task's
    /// current reading; then selects uniformly.</summary>
    private sealed class EliteObservingSelection : IEliteIndexAwareEvolutionSelectionPolicy<TestGenome>
    {
        private readonly UniformEvolutionSelectionPolicy<TestGenome> _inner = new();
        private readonly Func<EvolutionArchiveEntry<TestGenome>, double> _expected;
        public EliteObservingSelection(Func<EvolutionArchiveEntry<TestGenome>, double> expected) => _expected = expected;
        public int Checked { get; private set; }
        public List<string> Mismatches { get; } = new();
        public string Id => "elite-observing";
        public string VersionHash => "elite-observing-v1";
        public void UseEliteIndex(IReadOnlyList<EvolutionEliteRecord<TestGenome>> elites, int island)
        {
            foreach (EvolutionEliteRecord<TestGenome> record in elites)
            {
                Checked++;
                double expected = _expected(record.Entry), actual = record.Entry.Evaluation.Descriptors["rel"];
                if (expected != actual) Mismatches.Add(record.Entry.Candidate.CanonicalGenome.Genome.Value + ":" + expected + "!=" + actual);
            }
        }
        public EvolutionSelection<TestGenome>? Select(IEvolutionArchive<TestGenome> archive, StableRandom random, int inspirationCount) =>
            _inner.Select(archive, random, inspirationCount);
    }
}
