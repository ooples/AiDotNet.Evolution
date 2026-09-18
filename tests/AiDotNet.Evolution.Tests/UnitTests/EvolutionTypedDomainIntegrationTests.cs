using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionTypedDomainIntegrationTests
{
    private enum Model { Linear = -1, Tree = 4, Forest = 8 }
    private enum Empty { }
    [Flags] private enum Flags { None = 0, Read = 1, Write = 2 }

    [Theory]
    [InlineData(-1e100, 1)]
    [InlineData(-1e300, 1e-100)]
    [InlineData(-1, 1e-100)]
    public void RealEndpointsStayExactDespiteSubtractionCancellation(double minimum, double maximum)
    {
        // Previous decoding rounded each upper endpoint to zero.
        Assert.NotEqual(maximum, minimum + (maximum - minimum));
        var domain = EvolutionParameter.Real("x", minimum, maximum);
        Assert.Equal(minimum, domain.FromNormalized(-1).Number);
        Assert.Equal(maximum, domain.FromNormalized(1).Number);
        Assert.Equal(maximum, domain.FromNormalized(2).Number);
        Assert.Equal(0, domain.Normalize(domain.FromNormalized(0)));
        Assert.Equal(1, domain.Normalize(domain.FromNormalized(1)));
    }

    [Fact]
    public void EnumDomainsSampleRoundTripAndActivateConditionalParameters()
    {
        var model = EvolutionParameter.Enum<Model>("model");
        Assert.Equal(new[] { "Forest", "Linear", "Tree" }, model.Categories);
        var space = new EvolutionSearchSpaceBuilder().Add(model)
            .Add(EvolutionParameter.Integer("depth", 1, 8).When("model", EvolutionParameterValue.Categorical("Tree"))).Build();
        var seen = new HashSet<Model>();
        for (ulong seed = 0; seed < 100; seed++)
        {
            var genome = space.Sample(StableRandom.CreateStream(seed, 0));
            Model selected = genome.Enum<Model>("model");
            seen.Add(selected);
            Assert.Equal(selected == Model.Tree, genome.Values.ContainsKey("depth"));
            Assert.Equal(selected, space.Deserialize(space.Serialize(genome)).Enum<Model>("model"));
            Assert.NotSame(genome, space.Validate(genome));
        }
        Assert.Equal(3, seen.Count);
        Assert.Throws<ArgumentException>(() => EvolutionParameter.Enum<Empty>("empty"));
    }

    [Theory]
    [InlineData("4")]
    [InlineData("tree")]
    [InlineData("Read, Write")]
    public void EnumAccessRejectsNumericAliasesWrongCaseAndUndeclaredFlags(string value)
    {
        var space = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Categorical("choice", new[] { value })).Build();
        var genome = space.Sample(StableRandom.CreateStream(1, 0));
        Assert.Throws<ArgumentException>(() => genome.Enum<Model>("choice"));
        Assert.Throws<ArgumentException>(() => genome.Enum<Flags>("choice"));
        Assert.DoesNotContain("Read, Write", EvolutionParameter.Enum<Flags>("flags").Categories);
    }
}
