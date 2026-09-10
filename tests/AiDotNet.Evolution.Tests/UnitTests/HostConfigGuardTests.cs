#if !NET471
using AiDotNet.Evolution.Host;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// The guards on a run's declared shape, each with the input that trips it.
/// </summary>
/// <remarks>
/// These reject configuration that would otherwise produce a run whose results mean
/// something other than what the client asked for -- a parameter with no name, a
/// descriptor with no bins, a space with two knobs of the same name whose identities
/// would collide. Every one is unreachable from a happy path, which is precisely why
/// they need their own tests: a guard nobody exercises is a guard nobody knows is
/// wired up.
/// </remarks>
public sealed class HostConfigGuardTests
{
    private static RunConfig Minimal() => new()
    {
        Parameters = { new ParameterConfig { Name = "x", Min = 0, Max = 1 } },
        Descriptors = { new DescriptorConfig { Name = "x", Min = 0, Max = 1, Bins = 4 } },
        MaxProposals = 4,
        MaxEvaluations = 4,
        BatchSize = 2,
    };

    [Fact]
    public void AParameterNeedsAName() =>
        Assert.Throws<ArgumentException>(() => new ParameterDefinition("  ", 0, 1, 0.1, false));

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.0, double.PositiveInfinity)]
    public void AParameterNeedsFiniteBounds(double min, double max) =>
        Assert.Throws<ArgumentException>(() => new ParameterDefinition("x", min, max, 0.1, false));

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.0)]
    public void AParameterNeedsARange(double min, double max) =>
        Assert.Throws<ArgumentException>(() => new ParameterDefinition("x", min, max, 0.1, false));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void AParameterNeedsAPositiveStep(double step) =>
        Assert.Throws<ArgumentException>(() => new ParameterDefinition("x", 0, 1, step, false));

    [Fact]
    public void AParameterSpaceNeedsAtLeastOneParameter() =>
        Assert.Throws<ArgumentException>(() => new ParameterSpace(new List<ParameterDefinition>()));

    [Fact]
    public void TwoParametersCannotShareAName()
    {
        // Identity is built from the ordered values, so a duplicate name makes two
        // distinct genomes indistinguishable in the map the caller reads back.
        ArgumentException error = Assert.Throws<ArgumentException>(() => new ParameterSpace(
            new List<ParameterDefinition>
            {
                new("x", 0, 1, 0.1, false),
                new("x", 0, 1, 0.1, false),
            }));

        Assert.Contains("more than once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFromRawValuesChecksTheCount()
    {
        var space = new ParameterSpace(new List<ParameterDefinition> { new("x", 0, 1, 0.1, false) });

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => space.Create(new List<double> { 0.1, 0.2 }));

        Assert.Contains("Expected 1 values", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunNeedsAtLeastOneParameter()
    {
        RunConfig config = Minimal();
        config.Parameters = new List<ParameterConfig>();

        Assert.Throws<ArgumentException>(() => HostSession.Open(config));
    }

    [Fact]
    public void ARunNeedsAtLeastOneDescriptor()
    {
        RunConfig config = Minimal();
        config.Descriptors = new List<DescriptorConfig>();

        Assert.Throws<ArgumentException>(() => HostSession.Open(config));
    }

    [Fact]
    public void MoreDescriptorsThanTheLimitAreRefused()
    {
        // The descriptor grid is worse than linear: every dimension multiplies the cell
        // count, so this bound is not the same kind of generosity as the parameter one.
        RunConfig config = Minimal();
        config.Descriptors = new List<DescriptorConfig>();
        for (int i = 0; i <= ProtocolLimits.MaxDimensions; i += 1)
            config.Descriptors.Add(new DescriptorConfig { Name = $"d{i}", Min = 0, Max = 1, Bins = 2 });

        ArgumentException error = Assert.Throws<ArgumentException>(() => HostSession.Open(config));
        Assert.Contains("descriptors", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADescriptorNeedsAName()
    {
        RunConfig config = Minimal();
        config.Descriptors = new List<DescriptorConfig>
        {
            new() { Name = "   ", Min = 0, Max = 1, Bins = 4 },
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() => HostSession.Open(config));
        Assert.Contains("name", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ADescriptorNeedsAPositiveBinCount(int bins)
    {
        RunConfig config = Minimal();
        config.Descriptors = new List<DescriptorConfig>
        {
            new() { Name = "d", Min = 0, Max = 1, Bins = bins },
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() => HostSession.Open(config));
        Assert.Contains("bin count", error.Message, StringComparison.Ordinal);
    }
}
#endif
