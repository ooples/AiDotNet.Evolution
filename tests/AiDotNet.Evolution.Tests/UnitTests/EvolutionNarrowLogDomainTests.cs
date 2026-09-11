using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionNarrowLogDomainTests
{
    private static double Offset(double value, long ulps) => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) + ulps);
    private static long Ulps(double value, double origin) => BitConverter.DoubleToInt64Bits(value) - BitConverter.DoubleToInt64Bits(origin);

    [Theory]
    [InlineData("real")]
    [InlineData("integer")]
    [InlineData("category")]
    [InlineData("log")]
    [InlineData("narrow-log")]
    [InlineData("fixed-log")]
    public void OnlyLogarithmicSchemasInvalidateOldCodecIdentity(string kind)
    {
        var parameter = kind switch
        {
            "real" => EvolutionParameter.Real("x", -1, 1),
            "integer" => EvolutionParameter.Integer("x", 1, 4),
            "category" => EvolutionParameter.Categorical("x", new[] { "a", "b" }),
            "fixed-log" => EvolutionParameter.Logarithmic("x", 1e100, 1e100),
            "narrow-log" => EvolutionParameter.Logarithmic("x", 1e100, Offset(1e100, 1)),
            _ => EvolutionParameter.Logarithmic("x", 0.001, 10)
        };
        // Exact v1 domain/schema construction, without conditions: verify real backward compatibility,
        // not merely that an arbitrary corrupt hash is rejected.
        string[] domain = new[] { parameter.Name, parameter.Kind.ToString(),
            EvolutionParameterValue.Numeric(parameter.Minimum).Canonical, EvolutionParameterValue.Numeric(parameter.Maximum).Canonical,
            EvolutionHash.Combine(parameter.Categories) };
        string legacySchema = EvolutionHash.Combine(new[] { "typed-search-space-v1", EvolutionHash.Combine(domain) });
        var space = new EvolutionSearchSpaceBuilder().Add(parameter).Build();
        var genome = space.Sample(StableRandom.CreateStream(12, 0));
        string legacyPayload = space.Serialize(genome).Replace(space.VersionHash, legacySchema);
        if (parameter.Kind == EvolutionParameterKind.Logarithmic)
        {
            Assert.NotEqual(legacySchema, space.VersionHash);
            Assert.Throws<ArgumentException>(() => space.Deserialize(legacyPayload));
            // The superseded ratio-free v2 mapping produced different normalized coordinates for the same bounds,
            // so its genomes and checkpoints must also be rejected rather than silently reinterpreted.
            string supersededSchema = EvolutionHash.Combine(new[] { "typed-search-space-v1",
                EvolutionHash.Combine(domain.Concat(new[] { "log-domain-v2-finite-narrow" })) });
            Assert.NotEqual(supersededSchema, space.VersionHash);
            Assert.Throws<ArgumentException>(() => space.Deserialize(space.Serialize(genome).Replace(space.VersionHash, supersededSchema)));
        }
        else
        {
            Assert.Equal(legacySchema, space.VersionHash);
            Assert.Equal(genome.Identity, space.Deserialize(legacyPayload).Identity);
        }
    }

    [Theory]
    [InlineData(1e100)]
    [InlineData(1e-100)]
    [InlineData(1e300)]
    public void DistinctBoundsWhoseLogsRoundEqualStillHaveFiniteNormalizedCoordinates(double minimum)
    {
        double maximum = Offset(minimum, 1);
        var parameter = EvolutionParameter.Logarithmic("x", minimum, maximum);
        Assert.Equal(Math.Log(minimum), Math.Log(maximum));
        Assert.Equal(0, parameter.Normalize(EvolutionParameterValue.Numeric(minimum)));
        Assert.Equal(1, parameter.Normalize(EvolutionParameterValue.Numeric(maximum)));
        Assert.Equal(minimum, parameter.FromNormalized(0).Number);
        Assert.Equal(maximum, parameter.FromNormalized(1).Number);
        var space = new EvolutionSearchSpaceBuilder().Add(parameter).Build();
        for (ulong seed = 0; seed < 100; seed++)
        {
            var genome = space.Sample(StableRandom.CreateStream(seed, 0));
            Assert.Equal(genome.Identity, space.Validate(genome).Identity);
            Assert.All(space.EncodeFeatures(genome), coordinate => Assert.InRange(coordinate, 0, 1));
        }
    }

    // The previous rule fired only on exactly equal rounded logarithms, so a domain one ulp past that cliff
    // (130 ulps at 1e100, 25 at 1e-100, 223 at 1e300) collapsed to two sampled values and never reached its
    // lower bound. Widths on both sides of every cliff must keep every representable point.
    [Theory]
    [InlineData(1e100, 129)]
    [InlineData(1e100, 130)]
    [InlineData(1e100, 131)]
    [InlineData(1e-100, 24)]
    [InlineData(1e-100, 25)]
    [InlineData(1e-100, 26)]
    [InlineData(1e300, 222)]
    [InlineData(1e300, 223)]
    [InlineData(1e300, 224)]
    public void NarrowLogDomainsOnBothSidesOfTheLogResolutionCliffKeepEveryRepresentablePoint(double minimum, int widthInUlps)
    {
        double maximum = Offset(minimum, widthInUlps);
        var parameter = EvolutionParameter.Logarithmic("x", minimum, maximum);
        var coordinates = new List<double>();
        for (int i = 0; i <= widthInUlps; i++)
        {
            double point = Offset(minimum, i);
            double coordinate = parameter.Normalize(EvolutionParameterValue.Numeric(point));
            Assert.InRange(coordinate, 0, 1);
            Assert.Equal(point, parameter.FromNormalized(coordinate).Number);
            coordinates.Add(coordinate);
        }
        // Strictly increasing: every representable point keeps its own normalized coordinate and is decodable.
        Assert.Equal(widthInUlps + 1, coordinates.Distinct().Count());
        for (int i = 1; i <= widthInUlps; i++) Assert.True(coordinates[i] > coordinates[i - 1]);
        Assert.Equal(minimum, parameter.FromNormalized(0).Number);
        Assert.Equal(maximum, parameter.FromNormalized(1).Number);

        var space = new EvolutionSearchSpaceBuilder().Add(parameter).Build();
        var sampled = new HashSet<long>();
        for (ulong seed = 0; seed < 4000; seed++) sampled.Add(Ulps(space.Sample(StableRandom.CreateStream(seed, 0)).Number("x"), minimum));
        Assert.True(sampled.Count > widthInUlps / 2, "sampling collapsed to " + sampled.Count.ToString() + " values");
        Assert.Contains(0L, sampled);
        Assert.Contains((long)widthInUlps, sampled);
    }

    [Fact]
    public void OrdinaryLogDomainsDecodeBothBoundsExactlyAndKeepIdentityWhenMutatingOutward()
    {
        var domains = new List<(double Minimum, double Maximum)>
        {
            (3, 7), (1e-8, 1), (0.001, 10), (1e-5, 1e-1), (1e100, 1e101), (1e-4, 1e4),
            (1e-300, 1e300), (double.Epsilon, double.MaxValue), (1e100, Offset(1e100, 300))
        };
        var random = new Random(11);
        for (int i = 0; i < 2000; i++)
        {
            double minimum = Math.Exp(random.NextDouble() * 200 - 100);
            domains.Add((minimum, minimum * Math.Exp(random.NextDouble() * 20 + 1e-6)));
        }
        foreach ((double minimum, double maximum) in domains)
        {
            var parameter = EvolutionParameter.Logarithmic("x", minimum, maximum);
            Assert.Equal(minimum, parameter.FromNormalized(0).Number);
            Assert.Equal(maximum, parameter.FromNormalized(1).Number);
            Assert.Equal(minimum, parameter.FromNormalized(-5).Number);
            Assert.Equal(maximum, parameter.FromNormalized(5).Number);
            Assert.Equal(0, parameter.Normalize(EvolutionParameterValue.Numeric(minimum)));
            Assert.Equal(1, parameter.Normalize(EvolutionParameterValue.Numeric(maximum)));
            // A parent sitting on a bound and mutating outward stays on that bound instead of drifting
            // just inside it and acquiring a new identity.
            var space = new EvolutionSearchSpaceBuilder().Add(parameter).Build();
            foreach (double bound in new[] { minimum, maximum })
            {
                double coordinate = parameter.Normalize(EvolutionParameterValue.Numeric(bound));
                double outward = bound == minimum ? coordinate - 0.37 : coordinate + 0.37;
                Assert.Equal(bound, parameter.FromNormalized(outward).Number);
                var parent = space.CreateGenome(new Dictionary<string, EvolutionParameterValue> { ["x"] = EvolutionParameterValue.Numeric(bound) });
                var mutated = space.CreateGenome(new Dictionary<string, EvolutionParameterValue> { ["x"] = parameter.FromNormalized(outward) });
                Assert.Equal(parent.Identity, mutated.Identity);
            }
        }
        // Wide domains stay logarithmic rather than silently becoming linear interpolation.
        Assert.Equal(Math.Sqrt(21), EvolutionParameter.Logarithmic("x", 3, 7).FromNormalized(0.5).Number, 12);
        Assert.Equal(0.1, EvolutionParameter.Logarithmic("x", 0.001, 10).FromNormalized(0.5).Number, 12);
    }

    [Fact]
    public async Task SearchOverANearCollapsedLogDomainVisitsManyDistinctValues()
    {
        double minimum = 1e100;
        double maximum = Offset(minimum, 300);
        var space = new EvolutionSearchSpaceBuilder()
            .Add(EvolutionParameter.Logarithmic("near", minimum, maximum))
            .Add(EvolutionParameter.Real("x0", -5, 5))
            .Build();
        var visited = new HashSet<long>();
        var task = new EvolutionSearchTask(space, "near-collapsed", "v1", "v1", (genome, _, _) =>
        {
            visited.Add(Ulps(genome.Number("near"), minimum));
            return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(
                -Math.Abs(space.Parameters[0].Normalize(genome.Values["near"]) - 0.3) - genome.Number("x0") * genome.Number("x0"),
                new Dictionary<string, double> { ["x"] = genome.Number("x0") }, costUnits: 1));
        });
        var engine = new EvolutionEngine<EvolutionSearchGenome>(task, EvolutionSearchPresets.Create(space),
            _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", -5, 5, 10) }),
            new EvolutionEngineOptions
            {
                RunId = "near-collapsed",
                Seed = 5,
                MaxEvaluationAttempts = 120,
                MaxProposals = 2400,
                MaxGenerations = 2400,
                ProposalBatchSize = 1,
                MaxDegreeOfParallelism = 1,
                InspirationCount = 2,
                MigrationInterval = 0
            }, genomeCodec: space);
        var result = await engine.RunAsync(new[] { space.Sample(StableRandom.CreateStream(1, 0)) });
        Assert.Empty(result.RetainedFailures);
        Assert.True(visited.Count > 20, "search visited only " + visited.Count.ToString() + " distinct values");
        Assert.All(visited, offset => Assert.InRange(offset, 0, 300));
    }
}
