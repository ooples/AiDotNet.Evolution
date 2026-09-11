using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionNarrowLogDomainTests
{
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
            "narrow-log" => EvolutionParameter.Logarithmic("x", 1e100, BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(1e100) + 1)),
            _ => EvolutionParameter.Logarithmic("x", 0.001, 10)
        };
        // Exact v1 domain/schema construction, without conditions: verify real backward compatibility,
        // not merely that an arbitrary corrupt hash is rejected.
        string legacyDefinition = EvolutionHash.Combine(new[] { parameter.Name, parameter.Kind.ToString(),
            EvolutionParameterValue.Numeric(parameter.Minimum).Canonical, EvolutionParameterValue.Numeric(parameter.Maximum).Canonical,
            EvolutionHash.Combine(parameter.Categories) });
        string legacySchema = EvolutionHash.Combine(new[] { "typed-search-space-v1", legacyDefinition });
        var space = new EvolutionSearchSpaceBuilder().Add(parameter).Build();
        var genome = space.Sample(StableRandom.CreateStream(12, 0));
        string legacyPayload = space.Serialize(genome).Replace(space.VersionHash, legacySchema);
        if (parameter.Kind == EvolutionParameterKind.Logarithmic)
        {
            Assert.NotEqual(legacySchema, space.VersionHash);
            Assert.Throws<ArgumentException>(() => space.Deserialize(legacyPayload));
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
        double maximum = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(minimum) + 1);
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
}
