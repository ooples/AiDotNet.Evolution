#if NET10_0
using AiDotNet.Evolution.Quality;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class ParetoCampaignTests
{
    [Theory]
    [InlineData(2, "scalar-single")]
    [InlineData(2, "scalar-map64")]
    [InlineData(2, "pareto64")]
    [InlineData(3, "scalar-single")]
    [InlineData(3, "scalar-map64")]
    [InlineData(3, "pareto64")]
    public async Task SharedFixturesChargeAllCallsAndReplayExactly(int dimensions, string method)
    {
        var a = await ParetoCampaign.RunCase(dimensions, method, 7, 32, 1);
        var b = await ParetoCampaign.RunCase(dimensions, method, 7, 32, 4);
        Assert.Equal("completed", a.Status); Assert.Equal("completed", b.Status);
        Assert.Equal(a.StateHash, b.StateHash); Assert.Equal(32, a.Calls); Assert.Equal(32, a.Cost);
        Assert.InRange(a.Hypervolume, 0, 1); Assert.NotEmpty(a.Elites);
        Assert.All(a.Samples, sample => Assert.Equal(1, sample.Attempts));
    }

    [Fact]
    public async Task MethodsShareStartingPopulationAndBudgets()
    {
        var a = await ParetoCampaign.RunCase(2, "scalar-single", 1, 16, 1);
        var b = await ParetoCampaign.RunCase(2, "scalar-map64", 1, 16, 1);
        var c = await ParetoCampaign.RunCase(2, "pareto64", 1, 16, 1);
        Assert.Equal(a.InitialHash, b.InitialHash); Assert.Equal(a.InitialHash, c.InitialHash);
        Assert.Equal(a.Samples.Take(8).Select(s => s.GenomeId), c.Samples.Take(8).Select(s => s.GenomeId));
        Assert.Equal(a.Calls, b.Calls); Assert.Equal(a.Calls, c.Calls);
    }
}
#endif
