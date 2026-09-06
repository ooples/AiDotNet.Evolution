using System.Collections;
using System.Text.Json.Nodes;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>Pins core defects found by adversarial review so they cannot silently return.</summary>
public sealed class EvolutionAuditRegressionTests
{
    [Fact]
    public void AWatchedMetricHasItsOwnDirectionRatherThanTheArchives()
    {
        var higherBetter = new EvolutionEarlyStoppingOptions { MetricName = "accuracy" };
        var lowerBetter = new EvolutionEarlyStoppingOptions { MetricName = "accuracy", MetricIsLowerBetter = true };

        Assert.False(higherBetter.SnapshotAndValidate().MetricIsLowerBetter);
        Assert.True(lowerBetter.SnapshotAndValidate().MetricIsLowerBetter);
        Assert.NotEqual(Hash(higherBetter), Hash(lowerBetter));
    }

    [Fact]
    public void GrowthDoesNotWidenTheGridForACandidateAnotherAxisRejects()
    {
        var archive = new MapElitesArchive<TestGenome>(new[]
        {
            new EvolutionDescriptorDefinition("x", 0, 1, 5, EvolutionOutOfRangePolicy.Grow),
            new EvolutionDescriptorDefinition("y", 0, 1, 5, EvolutionOutOfRangePolicy.Reject)
        });

        Assert.Equal(25, archive.TotalGridCells);
        Assert.Equal(EvolutionArchiveInsertionResult.Rejected, Add(archive, 1, "a", 1, x: 5.0, y: 99));
        Assert.Equal(25, archive.TotalGridCells);
        Assert.Equal(1.0, archive.Descriptors[0].Maximum, 12);
        Assert.Empty(archive.Entries);
    }

    [Fact]
    public void MultiAxisGrowthIsAtomicWhenTheCombinedGridWouldExceedItsLimit()
    {
        var archive = new MapElitesArchive<TestGenome>(new[]
        {
            new EvolutionDescriptorDefinition("x", 0, 2, 2, EvolutionOutOfRangePolicy.Grow),
            new EvolutionDescriptorDefinition("y", 0, 2, 2, EvolutionOutOfRangePolicy.Grow)
        }, maximumGridCells: 50);

        Assert.Equal(EvolutionArchiveInsertionResult.Rejected, Add(archive, 1, "a", 1, x: 9, y: 9));

        Assert.Equal(4, archive.TotalGridCells);
        Assert.Equal(2, archive.Descriptors[0].Maximum);
        Assert.Equal(2, archive.Descriptors[1].Maximum);
        Assert.Equal(0, archive.Version);
        Assert.Empty(archive.Entries);
    }

    [Fact]
    public void ARestoreRefusesACheckpointHoldingMoreElitesThanTheArchiveCanKeep()
    {
        MapElitesArchive<TestGenome> full = Archive(capacity: 0);
        Add(full, 1, "a", 1, 0.1, 0);
        Add(full, 2, "b", 2, 0.5, 0);
        Add(full, 3, "c", 3, 0.9, 0);

        MapElitesArchive<TestGenome> tooSmall = Archive(capacity: 2);
        Assert.Throws<InvalidDataException>(() =>
            tooSmall.Restore(full.Entries.ToArray(), full.Descriptors, full.Version));
        Assert.Empty(tooSmall.Entries);
        Assert.Equal(0, tooSmall.Version);
    }

    [Fact]
    public void AReferenceGenomeMustDeclareTheImmutableOwnershipContract()
    {
        Assert.Throws<ArgumentException>(() =>
            new EvolutionCanonicalGenome<MutableGenome>(new MutableGenome(), "mutable"));

        var immutable = new EvolutionCanonicalGenome<TestGenome>(new TestGenome(1), "immutable");
        Assert.Equal(1, immutable.Genome.Value);
    }

    [Fact]
    public void AValueGenomeCannotHideAMutableReferenceFromTheOwnershipContract()
    {
        Assert.Throws<ArgumentException>(() =>
            new EvolutionCanonicalGenome<MutableValueGenome>(
                new MutableValueGenome(new[] { 1 }), "mutable-value"));

        var primitive = new EvolutionCanonicalGenome<int>(1, "primitive");
        Assert.Equal(1, primitive.Genome);

        var declaredImmutable = new EvolutionCanonicalGenome<DeclaredImmutableValueGenome>(
            new DeclaredImmutableValueGenome(new[] { 1 }), "declared-immutable-value");
        Assert.Equal(1, declaredImmutable.Genome.GetValue(0));
    }

    [Fact]
    public void ArchiveRestoreSnapshotsReadOnlyListsByIndex()
    {
        MapElitesArchive<TestGenome> source = Archive(capacity: 0);
        Add(source, 1, "a", 1, 0.1, 0.2);
        var restored = Archive(capacity: 0);

        restored.Restore(
            new IndexOnlyReadOnlyList<EvolutionArchiveEntry<TestGenome>>(source.Entries),
            new IndexOnlyReadOnlyList<EvolutionDescriptorDefinition>(source.Descriptors),
            source.Version);

        Assert.Single(restored.Entries);
        Assert.Equal(source.Entries[0].Evaluation.GenomeId, restored.Entries[0].Evaluation.GenomeId);
    }

    [Fact]
    public void CollectionBoundsDoNotTrustADishonestReportedCount()
    {
        KeyValuePair<string, string>[] entries = Enumerable.Range(0, EvolutionDiagnostic.MaximumDataEntries + 1)
            .Select(index => new KeyValuePair<string, string>("key-" + index, "value"))
            .ToArray();
        var dishonest = new DishonestReadOnlyDictionary(entries);

        Assert.Throws<ArgumentException>(() => new EvolutionDiagnostic("bounded", "bounded", data: dishonest));
    }

    [Fact]
    public async Task ReadingACheckpointFromADifferentEngineVersionIsRefused()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await ReadableEngine(store).RunAsync(new[] { new TestGenome(1), new TestGenome(2) });
        EvolutionCheckpoint written = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("audit-run"));
        JsonObject payload = Assert.IsType<JsonObject>(JsonNode.Parse(written.Payload));
        JsonValue schemaNode = Assert.IsAssignableFrom<JsonValue>(payload["SchemaVersion"]);
        int documentSchemaVersion = schemaNode.GetValue<int>();
        payload["SchemaVersion"] = documentSchemaVersion - 1;
        string oldPayload = payload.ToJsonString();

        var older = new EvolutionCheckpoint(
            written.RunId, written.Sequence, written.CompatibilityHash, oldPayload);

        Assert.Throws<InvalidDataException>(() =>
            EvolutionEngine<TestGenome>.ReadCheckpoint(older, new TestGenomeCodec()));
    }

    private static string Hash(EvolutionEarlyStoppingOptions stopping) =>
        new EvolutionEngineOptions { EarlyStopping = stopping }.GetConfigurationHash();

    private static MapElitesArchive<TestGenome> Archive(int capacity) => new(new[]
    {
        new EvolutionDescriptorDefinition("x", 0, 1, 5, EvolutionOutOfRangePolicy.Clamp),
        new EvolutionDescriptorDefinition("y", 0, 1, 5, EvolutionOutOfRangePolicy.Clamp)
    }, EvolutionOptimizationDirection.Maximize, capacity);

    private static EvolutionArchiveInsertionResult Add(
        MapElitesArchive<TestGenome> archive, long id, string genomeId, double quality, double x, double y)
    {
        var lineage = new EvolutionLineage(null, null, "test", null, 0, 0, (ulong)id);
        var candidate = new EvolutionCandidate<TestGenome>(id,
            new EvolutionCanonicalGenome<TestGenome>(new TestGenome((int)id), genomeId), lineage);
        var evaluation = new EvolutionEvaluation(id, genomeId, EvolutionEvaluationStatus.Completed, quality,
            EvolutionOptimizationDirection.Maximize,
            new Dictionary<string, double>(StringComparer.Ordinal) { ["x"] = x, ["y"] = y },
            Array.Empty<double>(), Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 0), lineage,
            EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");
        return archive.TryAdd(candidate, evaluation);
    }

    private static EvolutionEngine<TestGenome> ReadableEngine(IEvolutionCheckpointStore store) => new(
        new SyntheticEvolutionTask(), new IncrementVariation(), _ => new MapElitesArchive<TestGenome>(new[]
        {
            new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Clamp)
        }), new EvolutionEngineOptions
        {
            RunId = "audit-run",
            Seed = 5,
            MaxEvaluationAttempts = 6,
            MaxProposals = 50,
            MaxGenerations = 50,
            ProposalBatchSize = 2,
            IslandCount = 1,
            MigrationInterval = 0,
            MigrantsPerIsland = 1,
            CheckpointInterval = 0
        }, checkpointStore: store, genomeCodec: new TestGenomeCodec());

    private sealed class MutableGenome
    {
        public int Value { get; set; }
    }

    private readonly struct MutableValueGenome
    {
        internal MutableValueGenome(int[] values) => Values = values;

        internal int[] Values { get; }
    }

    private readonly struct DeclaredImmutableValueGenome : IImmutableEvolutionGenome
    {
        private readonly int[] _values;

        internal DeclaredImmutableValueGenome(int[] values) => _values = values.ToArray();

        internal int GetValue(int index) => _values[index];
    }

    private sealed class IndexOnlyReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly T[] _items;

        internal IndexOnlyReadOnlyList(IEnumerable<T> items) => _items = items.ToArray();

        public int Count => _items.Length;

        public T this[int index] => _items[index];

        public IEnumerator<T> GetEnumerator() =>
            throw new InvalidOperationException("The collection must be copied through its indexed contract.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DishonestReadOnlyDictionary : IReadOnlyDictionary<string, string>
    {
        private readonly KeyValuePair<string, string>[] _entries;

        internal DishonestReadOnlyDictionary(KeyValuePair<string, string>[] entries) => _entries = entries;

        public int Count => 0;

        public IEnumerable<string> Keys => _entries.Select(entry => entry.Key);

        public IEnumerable<string> Values => _entries.Select(entry => entry.Value);

        public string this[string key] => throw new KeyNotFoundException(key);

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, string>>)_entries).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
