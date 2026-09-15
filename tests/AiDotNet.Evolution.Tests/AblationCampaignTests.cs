#if NET10_0
using AiDotNet.Evolution.Quality;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class AblationCampaignTests
{
    [Fact]
    public void MatrixHasSelectorsRemovalsAndIdentifiableInteractions()
    {
        var matrix = AblationCampaign.Matrix();
        Assert.Equal(19, matrix.Length);
        Assert.Equal(19, matrix.Select(c => c.Name).Distinct().Count());
        Assert.Equal(Enum.GetValues<EvolutionSelectionPolicyKind>(), matrix.Take(4).Select(c => c.Selection));
        Assert.All(matrix.Where(c => c.Migration), c => Assert.True(c.Islands));
        Assert.Contains(matrix, c => c.Name == "calibration-growth" && c.Calibration && c.Growth);
        Assert.Contains(matrix, c => c.Name == "islands-continuous" && c.Islands && c.Continuous);
        Assert.Equal(6, matrix.Count(c => c.Name.StartsWith("all-no-", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("numeric")]
    [InlineData("program")]
    [InlineData("kernel")]
    public async Task AllFeaturesAndBaselineReconcileActualWorkAndPairedInitialPopulation(string family)
    {
        var matrix = AblationCampaign.Matrix();
        var baseline = await AblationCampaign.RunCase(family, "development", matrix[0], 191, 32);
        var enabled = await AblationCampaign.RunCase(family, "development", matrix.Single(c => c.Name == "all"), 191, 32);
        Assert.Equal(baseline.InitialHash, enabled.InitialHash);
        foreach (var row in new[] { baseline, enabled })
        {
            Assert.Equal("completed", row.Status);
            Assert.InRange(row.Calls, 8, 32);
            Assert.Equal(row.Calls, row.Observations.LongLength);
            Assert.Equal(row.Calls, row.Resources.Spent["cost_units"]);
            Assert.Equal(row.Proposals - 8, row.Resources.Spent["proposal_calls"]);
            Assert.InRange(row.Quality, 0, 1);
            Assert.InRange(row.Diversity, 0, 1);
            Assert.All(row.Observations, observation => Assert.Equal(family == "kernel" ? 4 : 0, observation.TimingsMilliseconds.Length));
        }
    }

    [Theory]
    [InlineData("ratio")]
    [InlineData("curiosity")]
    [InlineData("double")]
    public async Task SelectionPoliciesRunWithInspirationAwareOperator(string name)
    {
        var row = await AblationCampaign.RunCase("numeric", "development", AblationCampaign.Matrix().Single(c => c.Name == name), 37, 32);
        Assert.Equal("completed", row.Status);
        Assert.True(row.InspirationUses > 0);
    }

    [Theory]
    [InlineData("numeric")]
    [InlineData("program")]
    public async Task DeterministicWorkloadsReplayDespiteConcurrentEvaluation(string family)
    {
        var config = AblationCampaign.Matrix().Single(c => c.Name == "all");
        var first = await AblationCampaign.RunCase(family, "development", config, 17, 32);
        var second = await AblationCampaign.RunCase(family, "development", config, 17, 32);
        Assert.Equal("completed", first.Status);
        Assert.Equal(first.StateHash, second.StateHash);
        Assert.Equal(first.Quality, second.Quality);
        Assert.Equal(first.Diversity, second.Diversity);
    }

    [Theory]
    [InlineData("program")]
    [InlineData("kernel")]
    public void CanonicalParametersCannotExploitUnusedFloatBits(string family)
    {
        var builder = new EvolutionSearchSpaceBuilder();
        foreach (string name in AblationWorkload.Names) builder.Add(EvolutionParameter.Real(name, -1, 1));
        var space = builder.Build();
        var workload = new AblationWorkload(family, "development");
        var random = StableRandom.CreateStream(3, 7);
        for (int i = 0; i < 100; i++)
        {
            var normalized = workload.Normalize(space, space.Sample(random));
            Assert.Equal(normalized.Identity, workload.Normalize(space, normalized).Identity);
        }
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("budget")]
    [InlineData("matrix")]
    [InlineData("migration")]
    [InlineData("partition")]
    public void InvalidPlanCannotRun(string corruption)
    {
        var request = new AblationCampaign.Request(AblationCampaign.Protocol, "development", 32, [1, 2], AblationCampaign.Matrix());
        request = corruption switch
        {
            "seed" => request with { Seeds = [1, 1] },
            "budget" => request with { Budget = 1_000_000 },
            "matrix" => request with { Configurations = request.Configurations.Take(2).ToArray() },
            "migration" => request with { Configurations = [new("baseline"), new("invalid", Migration: true)] },
            _ => request with { Partition = "unregistered" }
        };
        Assert.Throws<InvalidDataException>(() => AblationCampaign.Validate(request));
    }
}
#endif
