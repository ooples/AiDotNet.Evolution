using System.Globalization;
using System.Text;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-58: OpenEvolve's artifact side channel keeps large and binary artifacts on disk (artifacts_base_path,
/// artifact_size_threshold, max_artifact_storage, artifact_retention_days). Here a store receives them in full while the
/// evaluation keeps a bounded preview and the content address, so checkpoints stay text-only.
/// </summary>
public sealed class ArtifactStoreParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "artifact-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CustomArtifactTask(Func<EvolutionArtifact[]> artifacts) : IEvolutionTask<TestGenome>
    {
        public string Id => "custom-artifact";
        public string VersionHash => "custom-artifact-v1";
        public string EvaluatorVersionHash => "custom-artifact-evaluator-v1";

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome,
            CancellationToken cancellationToken = default) => new(new EvolutionCanonicalGenome<TestGenome>(
            new TestGenome(genome.Value), genome.Value.ToString(CultureInfo.InvariantCulture)));

        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default) =>
            new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, candidate.CanonicalGenome.Genome.Value,
                descriptors: new Dictionary<string, double> { ["x"] = 1 }, artifacts: artifacts()));
    }

    private static async Task<EvolutionEvaluation> FirstEvaluation(Func<EvolutionArtifact[]> artifacts, Action<EvolutionArtifactOptions> configure)
    {
        var options = new EvolutionEngineOptions
        {
            RunId = "artifact-store",
            Seed = 7,
            MaxEvaluationAttempts = 1,
            MaxProposals = 10,
            MaxGenerations = 10,
            ProposalBatchSize = 1,
            MaxDegreeOfParallelism = 1,
            IslandCount = 1,
            MigrationInterval = 0,
            CheckpointInterval = 0
        };
        options.Artifacts.Enabled = true;
        options.Artifacts.MaxArtifactBytes = 256;
        configure(options.Artifacts);
        var observer = new EvaluationRecordingObserver();
        await new EvolutionEngine<TestGenome>(new CustomArtifactTask(artifacts), new ArtifactRecordingVariation(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 10) }), options,
            observer: observer).RunAsync(new[] { new TestGenome(1) });
        return observer.Evaluations[0];
    }

    private static string Reference(string text) =>
        text.Substring(text.IndexOf("sha256:", StringComparison.Ordinal), "sha256:".Length + 64);

    [Fact]
    public async Task Large_text_is_spilled_in_full_and_the_evaluation_keeps_a_bounded_preview()
    {
        string log = string.Concat(Enumerable.Range(0, 400).Select(i => "line " + i + "\n"));
        var store = new DirectoryEvolutionArtifactStore(_root);
        EvolutionEvaluation evaluation = await FirstEvaluation(() => new[] { new EvolutionArtifact("stderr", log) }, a => a.Store = store);

        EvolutionArtifact kept = Assert.Single(evaluation.Artifacts);
        Assert.True(kept.SizeBytes <= 256);
        Assert.True(kept.IsTruncated);
        Assert.StartsWith("line 0\n", kept.Text, StringComparison.Ordinal);
        Assert.True(store.TryRead(Reference(kept.Text), out byte[] full));
        Assert.Equal(log, Encoding.UTF8.GetString(full));
    }

    [Fact]
    public async Task Without_a_store_large_text_is_truncated_exactly_as_before()
    {
        // Spaced text: one long unbroken token would be redacted as credential-shaped, which is a different behaviour.
        string log = string.Concat(Enumerable.Repeat("ok ", 400));
        EvolutionEvaluation evaluation = await FirstEvaluation(() => new[] { new EvolutionArtifact("stderr", log) }, _ => { });
        EvolutionArtifact kept = Assert.Single(evaluation.Artifacts);
        Assert.Equal(log.Substring(0, 256), kept.Text);
        Assert.DoesNotContain("sha256:", kept.Text);
        Assert.True(kept.IsTruncated);
    }

    [Fact]
    public async Task Binary_artifacts_round_trip_byte_exactly_through_the_store()
    {
        byte[] png = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray();
        var store = new DirectoryEvolutionArtifactStore(_root);
        EvolutionEvaluation evaluation = await FirstEvaluation(() => new[] { EvolutionArtifact.FromBytes("plot", png, "image/png") }, a => a.Store = store);
        EvolutionArtifact kept = Assert.Single(evaluation.Artifacts);
        Assert.False(kept.IsBinary);
        Assert.Contains("image/png", kept.Text);
        Assert.True(store.TryRead(Reference(kept.Text), out byte[] back));
        Assert.Equal(png, back);
    }

    [Fact]
    public async Task Binary_content_is_never_retained_inline_and_the_storage_limit_holds()
    {
        byte[] blob = new byte[1000];
        EvolutionEvaluation noStore = await FirstEvaluation(() => new[] { EvolutionArtifact.FromBytes("blob", blob, "application/octet-stream") }, _ => { });
        Assert.Contains("not retained", Assert.Single(noStore.Artifacts).Text);

        var store = new DirectoryEvolutionArtifactStore(_root);
        EvolutionEvaluation limited = await FirstEvaluation(() => new[]
        {
            EvolutionArtifact.FromBytes("a", new byte[600], "application/octet-stream"),
            EvolutionArtifact.FromBytes("b", new byte[600], "application/octet-stream")
        }, a => { a.Store = store; a.MaxStoredBytesPerEvaluation = 1000; });
        Assert.Contains("[stored: sha256:", limited.Artifacts[0].Text);
        Assert.Contains("storage limit", limited.Artifacts[1].Text);
    }

    [Fact]
    public void Aggregate_binary_content_per_result_is_capped()
    {
        EvolutionArtifact[] tooMuch = Enumerable.Range(0, 5)
            .Select(i => EvolutionArtifact.FromBytes("blob" + i, new byte[EvolutionArtifact.MaximumContentBytes], "application/octet-stream"))
            .ToArray();
        Assert.Throws<ArgumentException>(() => new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1,
            descriptors: new Dictionary<string, double> { ["x"] = 1 }, artifacts: tooMuch));
    }

    [Fact]
    public async Task Nothing_is_stored_when_its_reference_cannot_fit_inline()
    {
        var store = new DirectoryEvolutionArtifactStore(_root);
        EvolutionEvaluation evaluation = await FirstEvaluation(() => new[]
        {
            EvolutionArtifact.FromBytes("plot", new byte[100], "image/png"),
            new EvolutionArtifact("stderr", string.Concat(Enumerable.Repeat("ok ", 200)))
        }, a => { a.Store = store; a.MaxArtifactBytes = 40; });
        Assert.Empty(Directory.GetFiles(_root, "*.bin"));
        Assert.DoesNotContain("sha256:", string.Concat(evaluation.Artifacts.Select(artifact => artifact.Text)));
        Assert.All(evaluation.Artifacts, artifact => Assert.True(artifact.IsTruncated));
    }

    private sealed class BinaryResumeTask(CancellationTokenSource? cancelAt = null) : IEvolutionTask<TestGenome>
    {
        public string Id => "binary-resume";
        public string VersionHash => "binary-resume-v1";
        public string EvaluatorVersionHash => "binary-resume-evaluator-v1";

        public static byte[] Payload(int value) => Enumerable.Range(0, 300).Select(i => (byte)(i * value)).ToArray();

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome,
            CancellationToken cancellationToken = default) => new(new EvolutionCanonicalGenome<TestGenome>(
            new TestGenome(genome.Value), genome.Value.ToString(CultureInfo.InvariantCulture)));

        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            if (cancelAt is not null && candidate.EvaluationId >= 4)
            {
                cancelAt.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            int value = candidate.CanonicalGenome.Genome.Value;
            return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, value,
                descriptors: new Dictionary<string, double> { ["x"] = Math.Max(0, Math.Min(100, value)) },
                artifacts: new[] { EvolutionArtifact.FromBytes("plot", Payload(value), "image/png") }));
        }
    }

    [Fact]
    public async Task Binary_artifact_references_survive_checkpoint_and_resume()
    {
        EvolutionEngineOptions Options(bool resume)
        {
            var options = new EvolutionEngineOptions
            {
                RunId = "binary-resume",
                Seed = 91,
                MaxEvaluationAttempts = 8,
                MaxProposals = 100,
                MaxGenerations = 100,
                ProposalBatchSize = 2,
                MaxDegreeOfParallelism = 1,
                IslandCount = 1,
                MigrationInterval = 0,
                CheckpointInterval = 2,
                Resume = resume
            };
            options.Artifacts.Enabled = true;
            options.Artifacts.Store = new DirectoryEvolutionArtifactStore(_root);
            return options;
        }
        MapElitesArchive<TestGenome> Archive() => new(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Clamp) });
        TestGenome[] seeds = Enumerable.Range(1, 8).Select(value => new TestGenome(value)).ToArray();

        EvolutionRunResult<TestGenome> uninterrupted = await new EvolutionEngine<TestGenome>(new BinaryResumeTask(),
            new IncrementVariation(), _ => Archive(), Options(false), checkpointStore: new InMemoryEvolutionCheckpointStore(),
            genomeCodec: new TestGenomeCodec()).RunAsync(seeds);

        var shared = new InMemoryEvolutionCheckpointStore();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EvolutionEngine<TestGenome>(new BinaryResumeTask(cancellation),
            new IncrementVariation(), _ => Archive(), Options(false), checkpointStore: shared, genomeCodec: new TestGenomeCodec())
            .RunAsync(seeds, cancellation.Token));
        EvolutionRunResult<TestGenome> resumed = await new EvolutionEngine<TestGenome>(new BinaryResumeTask(),
            new IncrementVariation(), _ => Archive(), Options(true), checkpointStore: shared, genomeCodec: new TestGenomeCodec())
            .RunAsync(seeds);

        Assert.Equal(uninterrupted.StateHash, resumed.StateHash);
        var reader = new DirectoryEvolutionArtifactStore(_root);
        EvolutionArchiveEntry<TestGenome>[] entries = resumed.Islands.SelectMany(island => island.Entries).ToArray();
        Assert.NotEmpty(entries);
        foreach (EvolutionArchiveEntry<TestGenome> entry in entries)
        {
            EvolutionArtifact kept = Assert.Single(entry.Evaluation.Artifacts);
            Assert.True(reader.TryRead(Reference(kept.Text), out byte[] back));
            Assert.Equal(BinaryResumeTask.Payload(entry.Candidate.CanonicalGenome.Genome.Value), back);
        }
    }

    [Fact]
    public void The_store_is_content_addressed_verified_and_pruned_by_retention()
    {
        var store = new DirectoryEvolutionArtifactStore(_root, TimeSpan.FromDays(30));
        byte[] content = Encoding.UTF8.GetBytes("same");
        string first = store.Put(content);
        Assert.Equal(first, store.Put(content));
        Assert.Single(Directory.GetFiles(_root, "*.bin"));
        Assert.False(store.TryRead("sha256:" + new string('0', 64), out _));
        Assert.False(store.TryRead("md5:abc", out _));

        string path = Directory.GetFiles(_root, "*.bin").Single();
        File.WriteAllText(path, "tampered");
        Assert.False(store.TryRead(first, out _));

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-31));
        Assert.Equal(1, store.Prune(DateTimeOffset.UtcNow));
        Assert.Empty(Directory.GetFiles(_root, "*.bin"));
    }

    [Fact]
    public void A_store_changes_the_compatibility_identity_only_when_configured()
    {
        var plain = new EvolutionArtifactOptions { Enabled = true };
        var stored = new EvolutionArtifactOptions { Enabled = true, Store = new DirectoryEvolutionArtifactStore(_root) };
        Assert.DoesNotContain("spill", plain.SnapshotAndValidate().ToCanonicalString());
        Assert.Contains("spill-v1", stored.SnapshotAndValidate().ToCanonicalString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionArtifactOptions { MaxStoredBytesPerEvaluation = 0 }.SnapshotAndValidate());
    }
}
