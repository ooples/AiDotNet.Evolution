#if NET10_0
using System.Text.Json;
using AiDotNet.Evolution.Quality;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class SuiteNumericServiceTests
{
    [Theory]
    [InlineData("block-trap")]
    [InlineData("rastrigin")]
    [InlineData("knapsack")]
    [InlineData("stochastic-regression")]
    [InlineData("diffusion-control")]
    [InlineData("coupled-absolute")]
    [InlineData("spin-glass")]
    [InlineData("inventory-risk")]
    [InlineData("robust-design")]
    public async Task ExternalServiceUsesExactCoreInitializationMeasurementsAndCosts(string id)
    {
        var task = new SuiteNumericTask(id, 42);
        RunRecord core = await QualityExperiment.RunAsync(QualityTask.Sphere, QualityMethod.RandomSearch, 37, 8, task);
        var lines = QualityExperiment.SharedInitialUnits(37, suite: true).Select(values => JsonSerializer.Serialize(values));
        using var input = new StringReader(string.Join("\n", lines) + "\nnull\n");
        using var output = new StringWriter();
        Assert.Equal(0, NumericObjectiveService.Run(new[] { id, "37", "8", "42" }, true, input, output));
        string[] responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(10, responses.Length);
        using var manifest = JsonDocument.Parse(responses[0]);
        Assert.Equal(core.InitialPopulationHash, manifest.RootElement.GetProperty("InitialPopulationHash").GetString());
        Assert.Equal(task.VersionHash, manifest.RootElement.GetProperty("VersionHash").GetString());
        using var summary = JsonDocument.Parse(responses[^1]);
        var samples = summary.RootElement.GetProperty("Samples");
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(core.Samples[i].Quality, samples[i].GetProperty("Quality").GetDouble());
            Assert.Equal(core.Samples[i].ConstraintViolations.Single(), samples[i].GetProperty("ConstraintViolations")[0].GetDouble());
            Assert.Equal(core.Samples[i].GenomeId, samples[i].GetProperty("GenomeId").GetString());
        }
        Assert.Equal(core.FinalLoss, summary.RootElement.GetProperty("BestLoss").GetDouble());
        Assert.Equal(8 * task.WorkUnits, summary.RootElement.GetProperty("Resources").GetProperty("Spent").GetProperty("cost_units").GetInt32());
    }

    [Theory]
    [InlineData("unknown", "0", "8", "1")]
    [InlineData("rastrigin", "-1", "8", "1")]
    [InlineData("rastrigin", "0", "4097", "1")]
    [InlineData("rastrigin", "0", "8", "4294967296")]
    public void InvalidSuiteContractIsRejectedBeforeManifest(string task, string seed, string budget, string instance)
    {
        using var output = new StringWriter();
        Assert.Equal(2, NumericObjectiveService.Run(new[] { task, seed, budget, instance }, true, new StringReader(""), output));
        Assert.Equal("", output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]\n")]
    [InlineData("[2,2,2,2,2,2,2,2]\n")]
    [InlineData("[0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5]\n")]
    public void DisconnectOrInvalidFirstCandidateCannotBeReportedAsSuccess(string request)
    {
        using var output = new StringWriter();
        Assert.Equal(1, NumericObjectiveService.Run(new[] { "rastrigin", "0", "8", "42" }, true, new StringReader(request), output));
        using var error = JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.Equal("error", error.RootElement.GetProperty("Kind").GetString());
        Assert.Equal(0, error.RootElement.GetProperty("EvaluatorCalls").GetInt32());
    }
}
#endif
