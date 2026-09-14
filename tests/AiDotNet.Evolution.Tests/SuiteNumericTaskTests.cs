#if NET10_0
using AiDotNet.Evolution.Quality;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class SuiteNumericTaskTests
{
    [Theory]
    [InlineData("block-trap", "development", 1)]
    [InlineData("rastrigin", "development", 1)]
    [InlineData("knapsack", "development", 1)]
    [InlineData("stochastic-regression", "selection", 16)]
    [InlineData("diffusion-control", "selection", 256)]
    [InlineData("coupled-absolute", "selection", 1)]
    [InlineData("spin-glass", "final", 1)]
    [InlineData("inventory-risk", "final", 128)]
    [InlineData("robust-design", "final", 16)]
    public void FixedDefinitionsHaveVersionedInstancesAndDeterministicMeasurements(string id, string partition, int work)
    {
        var first = new SuiteNumericTask(id, 42);
        var replay = new SuiteNumericTask(id, 42);
        var independent = new SuiteNumericTask(id, 43);
        var x = Enumerable.Repeat(0.5, 8).ToArray();
        Assert.Equal(partition, first.Partition);
        Assert.Equal(work, first.WorkUnits);
        Assert.Equal(first.VersionHash, replay.VersionHash);
        Assert.NotEqual(first.VersionHash, independent.VersionHash);
        Assert.Equal(first.Evaluate(x, 4), replay.Evaluate(x, 4));
        var measured = first.Evaluate(x, 4);
        Assert.True(double.IsFinite(measured.Loss));
        Assert.True(double.IsFinite(measured.Violation));
        Assert.True(measured.Violation >= 0);
    }

    [Theory]
    [InlineData("stochastic-regression")]
    [InlineData("inventory-risk")]
    public void NoisyFamiliesDrawNewRepeatableNoisePerEvaluation(string id)
    {
        var task = new SuiteNumericTask(id, 72);
        var x = Enumerable.Repeat(0d, 8).ToArray();
        Assert.NotEqual(task.Evaluate(x, 0).Loss, task.Evaluate(x, 1).Loss);
        Assert.Equal(task.Evaluate(x, 0), new SuiteNumericTask(id, 72).Evaluate(x, 0));
    }

    [Fact]
    public void TrapHasALocalBasinThatOpposesSingleBitProgress()
    {
        var random = new StableRandom(42);
        var optimum = Enumerable.Range(0, 8).Select(_ => random.NextDouble() >= 0.5 ? 5d : -5d).ToArray();
        var local = optimum.Select(value => -value).ToArray();
        var task = new SuiteNumericTask("block-trap", 42);
        Assert.Equal(0, task.Evaluate(optimum, 0).Loss);
        double localLoss = task.Evaluate(local, 0).Loss;
        local[0] = optimum[0];
        Assert.True(task.Evaluate(local, 0).Loss > localLoss);
    }

    [Fact]
    public void SharedAnchorsAreFeasibleAndConstraintViolationIsExplicit()
    {
        var knapsack = new SuiteNumericTask("knapsack", 42);
        var design = new SuiteNumericTask("robust-design", 42);
        Assert.Equal(0, knapsack.Evaluate(Enumerable.Repeat(-5d, 8).ToArray(), 0).Violation);
        Assert.Equal(0, design.Evaluate(Enumerable.Repeat(5d, 8).ToArray(), 0).Violation);
        Assert.True(knapsack.Evaluate(Enumerable.Repeat(5d, 8).ToArray(), 0).Violation > 0);
    }

    [Fact]
    public void MalformedOrUnregisteredObjectivesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new SuiteNumericTask("invented", 42));
        var task = new SuiteNumericTask("rastrigin", 42);
        Assert.Throws<ArgumentException>(() => task.Evaluate(new double[7], 0));
        Assert.Throws<ArgumentException>(() => task.Evaluate(Enumerable.Repeat(double.NaN, 8).ToArray(), 0));
        Assert.Throws<ArgumentException>(() => task.Evaluate(new double[8], -1));
    }

    [Theory]
    [InlineData("knapsack")]
    [InlineData("diffusion-control")]
    [InlineData("inventory-risk")]
    public async Task ActualEngineMethodsShareStartsAndReconcileDeclaredWork(string id)
    {
        var task = new SuiteNumericTask(id, 42);
        var results = new List<RunRecord>();
        foreach (QualityMethod method in Enum.GetValues<QualityMethod>())
            results.Add(await QualityExperiment.RunAsync(QualityTask.Sphere, method, 72, 16, task));
        Assert.Single(results.Select(value => value.InitialPopulationHash).Distinct());
        Assert.All(results, value =>
        {
            Assert.Equal("completed", value.Status);
            Assert.Equal(id, value.Task);
            Assert.Equal(16, value.EvaluatorCalls);
            Assert.Equal(16m * task.WorkUnits, value.Resources.Spent["cost_units"]);
            Assert.NotNull(value.FinalLoss);
        });
    }
}
#endif
