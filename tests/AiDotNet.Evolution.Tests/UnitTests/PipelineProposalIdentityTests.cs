using System.Globalization;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-20, protocol v3: pipeline proposal identities commit the proposal context, including the source archive's contents
/// through a per-elite digest sum. They must be deterministic, sensitive to archive contents, and scale to large archives.
/// </summary>
public sealed class PipelineProposalIdentityTests
{
    private sealed class RecordingVariation : IVariationOperator<TestGenome>
    {
        public List<string> Identities { get; } = new();
        public string Id => "recording-increment";
        public string VersionHash => "recording-increment-v1";

        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default)
        {
            lock (Identities) Identities.Add(context.ProposalIdentity ?? throw new InvalidOperationException("pipeline context without identity"));
            return new(new TestGenome(context.Parent.Candidate.CanonicalGenome.Genome.Value + 1));
        }
    }

    private static async Task<List<string>> Identities(IEnumerable<int> seeds, int proposals)
    {
        var variation = new RecordingVariation();
        int[] values = seeds.ToArray();
        var options = new EvolutionEngineOptions
        {
            RunId = "pipeline-identity",
            Seed = 11,
            Dispatch = EvolutionDispatchMode.Pipeline,
            MaxProposals = values.Length + proposals,
            MaxEvaluationAttempts = values.Length + proposals,
            MaxGenerations = values.Length + proposals,
            MaxDegreeOfParallelism = 1,
            IslandCount = 1,
            CheckpointInterval = 0,
            MigrationInterval = 0,
            Pipeline = new EvolutionPipelineOptions { WaveSize = 2, MaxProposalConcurrency = 1, ProposalQueueCapacity = 2, EvaluationQueueCapacity = 2 }
        };
        var engine = new EvolutionEngine<TestGenome>(new SyntheticEvolutionTask(), variation,
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 10_000, 10_000) }), options);
        await engine.RunAsync(values.Select(value => new TestGenome(value)));
        return variation.Identities;
    }

    [Fact]
    public async Task Identities_are_deterministic_unique_and_follow_archive_contents()
    {
        List<string> first = await Identities(new[] { 3, 40, 71 }, 6);
        List<string> again = await Identities(new[] { 3, 40, 71 }, 6);
        Assert.NotEmpty(first);
        Assert.Equal(first, again);
        Assert.Equal(first.Count, first.Distinct(StringComparer.Ordinal).Count());
        Assert.All(first, identity => Assert.Matches("^[0-9a-f]{64}$", identity));

        // One different seed changes the archive once it is committed (the first wave may be planned before it is),
        // so the identity sequences must diverge.
        List<string> other = await Identities(new[] { 3, 40, 72 }, 6);
        Assert.NotEqual(first, other);
    }

    private static MapElitesArchive<TestGenome> Archive(IEnumerable<(long Id, double Quality, double X)> entries)
    {
        var archive = new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 10_000, 10_000) });
        foreach (var (id, quality, x) in entries)
        {
            var (candidate, evaluation) = MapElitesArchiveTests.Create(id, "g" + id, quality, x);
            Assert.Equal(EvolutionArchiveInsertionResult.Inserted, archive.TryAdd(candidate, evaluation));
        }
        return archive;
    }

    private static string Fingerprint(IEvolutionArchiveView<TestGenome> archive) =>
        EvolutionEngine<TestGenome>.PipelineArchiveContext.ArchiveFingerprint(archive);

    [Fact]
    public void The_archive_commitment_covers_every_elite_and_ignores_insertion_order()
    {
        var entries = Enumerable.Range(1, 40).Select(i => ((long)i, i * 0.5, i * 7.0 + 0.5)).ToArray();
        string forward = Fingerprint(Archive(entries));
        Assert.Equal(forward, Fingerprint(Archive(Enumerable.Reverse(entries))));

        // Only a non-best elite changes: same count, same version, same best. The commitment must still change.
        var changed = entries.Select(e => e.Item1 == 5 ? (e.Item1, e.Item2 + 0.25, e.Item3) : e).ToArray();
        Assert.NotEqual(forward, Fingerprint(Archive(changed)));
    }

    [Fact]
    public void The_digest_sum_carries_across_limbs_exactly()
    {
        // Rebuild the commitment independently with BigInteger: sum of the per-elite SHA-256 digests, little-endian, mod 2^256.
        var entries = Enumerable.Range(1, 3000).Select(i => ((long)i, i * 0.5, i + 0.5)).ToArray();
        MapElitesArchive<TestGenome> archive = Archive(entries);
        var modulus = System.Numerics.BigInteger.One << 256;
        var sum = System.Numerics.BigInteger.Zero;
        foreach (EvolutionArchiveEntry<TestGenome> entry in archive.Entries)
        {
            string entryFingerprint = EvolutionEngine<TestGenome>.FingerprintPipelineEvaluationForTests(entry.Evaluation);
            string hex = EvolutionHash.Combine(new[] { entry.Cell.StableKey, entryFingerprint });
            byte[] digest = Enumerable.Range(0, 32).Select(i => Convert.ToByte(hex.Substring(2 * i, 2), 16)).ToArray();
            sum = (sum + new System.Numerics.BigInteger(digest.Concat(new byte[] { 0 }).ToArray())) % modulus; // little-endian, unsigned
        }
        string expectedSum = sum.ToString("x64", CultureInfo.InvariantCulture);
        expectedSum = expectedSum.Substring(expectedSum.Length - 64);
        string expected = EvolutionHash.Combine(new[]
        {
            "pipeline-archive-v3", archive.DefinitionHash, archive.Version.ToString(CultureInfo.InvariantCulture),
            archive.Count.ToString(CultureInfo.InvariantCulture), expectedSum
        });
        Assert.Equal(expected, Fingerprint(archive));
    }

    [Fact]
    public async Task Large_archives_produce_identities_without_a_component_cap()
    {
        // 2,600 elites: every 64-bit limb of the digest sum overflows and carries many times.
        List<string> identities = await Identities(Enumerable.Range(0, 2600).Select(value => value * 3), 4);
        Assert.NotEmpty(identities);
        Assert.Equal(identities, await Identities(Enumerable.Range(0, 2600).Select(value => value * 3), 4));
        Assert.All(identities, identity => Assert.Matches("^[0-9a-f]{64}$", identity));
    }
}
