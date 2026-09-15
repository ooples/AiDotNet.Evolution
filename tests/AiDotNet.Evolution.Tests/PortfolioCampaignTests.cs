#if NET10_0
using AiDotNet.Evolution.Quality;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class PortfolioCampaignTests
{
    [Theory]
    [InlineData("numeric", "mutation")]
    [InlineData("numeric", "crossover")]
    [InlineData("numeric", "restart")]
    [InlineData("numeric", "refinement")]
    [InlineData("numeric", "adaptive-parent")]
    [InlineData("numeric", "adaptive-archive")]
    [InlineData("program", "refinement")]
    [InlineData("program", "adaptive-parent")]
    [InlineData("kernel", "refinement")]
    [InlineData("kernel", "adaptive-archive")]
    public async Task Every_real_search_and_refinement_invocation_is_charged(string family, string method)
    {
        var row = await PortfolioCampaign.RunCase(family, "development", method, 31, 32);
        Assert.Equal("completed", row.Status);
        Assert.InRange(row.Quality, 0, 1);
        Assert.Equal(row.ProposalCalls + row.ObjectiveCalls, row.Resources.Spent["cost_units"]);
        Assert.Equal(row.ObjectiveCalls, row.Observations.Length);
        Assert.InRange(row.Resources.Spent["cost_units"], 29, 32);
        if (method.StartsWith("adaptive-", StringComparison.Ordinal))
        {
            Assert.True(row.Credits.Length >= row.ProposalCalls);
            Assert.Equal(row.Credits.Length, row.Credits.Select(c => c.Generation).Distinct().Count());
            Assert.All(row.Credits, c => Assert.NotNull(c.ProposalCost));
        }
    }

    [Fact]
    public async Task Paired_starts_and_deterministic_study_replay_hold()
    {
        var first = await PortfolioCampaign.RunCase("numeric", "confirmation", "adaptive-parent", 53, 32);
        var replay = await PortfolioCampaign.RunCase("numeric", "confirmation", "adaptive-parent", 53, 32);
        var baseline = await PortfolioCampaign.RunCase("numeric", "confirmation", "mutation", 53, 32);
        Assert.Equal(first.InitialHash, baseline.InitialHash);
        Assert.Equal(first.State, replay.State);
        Assert.Equal(first.Quality, replay.Quality);
        Assert.NotEqual(first.Task, new AblationWorkload("numeric", "confirmation").Id);
    }
}
#endif
