#if !NET471
using AiDotNet.Evolution.Host;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class ParameterDomainTests
{
    [Theory]
    [InlineData(0.1, 2.4, 0.1, 1.0)]
    [InlineData(0.1, 2.4, 2.4, 2.0)]
    [InlineData(-2.4, -0.1, -2.4, -2.0)]
    [InlineData(-2.4, -0.1, -0.1, -1.0)]
    [InlineData(0.5, 3.5, 1.0, 1.0)]
    public void IntegralBoundsNeverReintroduceFractionalValues(
        double minimum, double maximum, double value, double expected)
    {
        var parameter = new ParameterDefinition("count", minimum, maximum, 1, true);

        Assert.Equal(expected, parameter.Normalize(value));
    }

    [Theory]
    [InlineData(0.1, 0.2)]
    [InlineData(0.1, 0.9)]
    [InlineData(-0.9, -0.1)]
    public void AnIntegralDomainMustContainAnInteger(double minimum, double maximum)
    {
        Assert.Throws<ArgumentException>(() => new ParameterDefinition("count", minimum, maximum, 1, true));
    }

    [Theory]
    [InlineData(0.5, 3.5, 1.0)]
    [InlineData(0.1, 4.9, 0.01)]
    [InlineData(0.1, 4.9, 0.1)]
    [InlineData(0.1, 4.9, 0.5)]
    [InlineData(0.1, 4.9, 1.5)]
    [InlineData(-3.7, 3.8, 1.5)]
    [InlineData(-2.4, -0.1, 0.01)]
    public void IntegralNormalizationIsBoundedAndIdempotent(double minimum, double maximum, double step)
    {
        var parameter = new ParameterDefinition("count", minimum, maximum, step, true);
        for (int index = -1; index <= 65; index += 1)
        {
            double input = minimum + (maximum - minimum) * index / 64;
            double normalized = parameter.Normalize(input);

            Assert.InRange(normalized, Math.Ceiling(minimum), Math.Floor(maximum));
            Assert.Equal(Math.Round(normalized), normalized);
            Assert.Equal(normalized, parameter.Normalize(normalized));
        }
    }

    [Fact]
    public void NonFiniteIntegralInputsFallBackToAValidCanonicalValue()
    {
        var parameter = new ParameterDefinition("count", 0.1, 2.4, 1, true);
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            double normalized = parameter.Normalize(value);
            Assert.Equal(1.0, normalized);
            Assert.Equal(normalized, parameter.Normalize(normalized));
        }
    }

    [Fact]
    public void NormalizedGenomeCanBeCopiedWithoutChangingIdentity()
    {
        var space = new ParameterSpace(new[] { new ParameterDefinition("count", 0.5, 3.5, 1, true) });
        ParameterGenome original = space.Create(new[] { 1.0 });

        ParameterGenome copy = space.Create(original.Values);

        Assert.Equal(1.0, original.Values[0]);
        Assert.Equal(original.CanonicalId(), copy.CanonicalId());
    }

    [Fact]
    public void ContinuousNormalizationRetainsItsDeclaredOriginAndClampedEndpoint()
    {
        var parameter = new ParameterDefinition("threshold", 0.1, 1, 0.6, false);

        Assert.Equal(0.1, parameter.Normalize(double.NaN));
        Assert.Equal(0.7, parameter.Normalize(0.7), 12);
        Assert.Equal(1.0, parameter.Normalize(1.0));
    }

    [Theory]
    [InlineData(1e308, 1.1e308, 1.05e308)]
    [InlineData(-1.1e308, -1e308, -1.05e308)]
    public void DefaultSeedsUseTheMidpointWithoutOverflowingTheBoundSum(
        double minimum, double maximum, double midpoint)
    {
        var parameter = new ParameterDefinition("x", minimum, maximum, 1e305, false);
        var space = new ParameterSpace(new[] { parameter });
        Assert.False(double.IsFinite(minimum + maximum));

        ParameterGenome seed = space.Create(new Dictionary<string, double>(StringComparer.Ordinal));

        Assert.Equal(parameter.Normalize(midpoint), seed.Values[0]);
        Assert.NotEqual(parameter.Minimum, seed.Values[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1e306)]
    public void OpenRejectsAnUnrepresentableSpanBeforeCreatingARun(double? step)
    {
        var config = new RunConfig
        {
            Parameters = { new ParameterConfig { Name = "x", Min = -1e308, Max = 1e308, Step = step } },
            Descriptors = { new DescriptorConfig { Name = "d", Min = 0, Max = 1 } },
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
        {
            using HostSession session = HostSession.Open(config);
        });

        Assert.Contains("range must be representable as a finite double", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenRejectsAnUnrepresentableQuantizationRatio()
    {
        var config = new RunConfig
        {
            Parameters = { new ParameterConfig { Name = "x", Min = 0, Max = 1, Step = double.Epsilon } },
            Descriptors = { new DescriptorConfig { Name = "d", Min = 0, Max = 1 } },
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
        {
            using HostSession session = HostSession.Open(config);
        });

        Assert.Equal("step", error.ParamName);
        Assert.Contains("range divided by step", error.Message, StringComparison.Ordinal);
    }
}
#endif
