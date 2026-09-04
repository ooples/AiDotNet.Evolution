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
    public void ARestoreRefusesACheckpointHoldingMoreElitesThanTheArchiveCanKeep()
    {
        MapElitesArchive<TestGenome> full = Archive(capacity: 0);
        Add(full, 1, "a", 1, 0.1, 0);
        Add(full, 2, "b", 2, 0.5, 0);
        Add(full, 3, "c", 3, 0.9, 0);

        MapElitesArchive<TestGenome> tooSmall = Archive(capacity: 2);
        Assert.Throws<InvalidDataException>(() => tooSmall.Restore(full.Entries.ToArray(), full.Version));
    }

    [Fact]
    public async Task ReadingACheckpointFromADifferentEngineVersionIsRefused()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await ReadableEngine(store).RunAsync(new[] { new TestGenome(1), new TestGenome(2) });
        EvolutionCheckpoint written = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("audit-run"));
        JsonObject payload = Assert.IsType<JsonObject>(JsonNode.Parse(written.Payload));
        JsonNode? schemaNode = payload["SchemaVersion"];
        Assert.NotNull(schemaNode);
        int documentSchemaVersion = schemaNode!.GetValue<int>();
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
}
