#if NET10_0
using AiDotNet.Evolution.Quality;
using System.Text.Json;
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
    public void EachConstrainedFamilyStartsFromExactlyOneFeasibleSharedAnchor()
    {
        // The two shared anchors are the opposite corners of the box. Neither corner is feasible for
        // both constrained families, so the published semantics must not claim two feasible anchors:
        // the low corner is feasible only for knapsack and the high corner only for robust-design.
        var knapsack = new SuiteNumericTask("knapsack", 42);
        var design = new SuiteNumericTask("robust-design", 42);
        double[] low = Enumerable.Repeat(-5d, 8).ToArray(), high = Enumerable.Repeat(5d, 8).ToArray();
        Assert.Equal(0, knapsack.Evaluate(low, 0).Violation);
        Assert.True(knapsack.Evaluate(high, 0).Violation > 0);
        Assert.Equal(0, design.Evaluate(high, 0).Violation);
        Assert.True(design.Evaluate(low, 0).Violation > 0);
    }

    [Fact]
    public async Task UnregisteredSuiteObjectivesAreRejectedAsAnInvalidContractNotAnArgumentFault()
    {
        string directory = Directory.CreateTempSubdirectory("us01-contract-tests-").FullName;
        try
        {
            string request = Path.Combine(directory, "request.json"), output = Path.Combine(directory, "output.json");
            File.WriteAllText(request, Request("development", crossFamily: true));
            await Assert.ThrowsAsync<InvalidDataException>(() => RepresentativeSuite.RunAsync([request, output]));
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("knapsack")]
    [InlineData("robust-design")]
    public async Task AnInfeasibleEvaluationCannotImproveTheReportedBestFeasibleLoss(string id)
    {
        // Removing the ConstraintViolations gate in Progress.OnEventAsync is not detectable from the
        // final loss alone, because an infeasible candidate usually is not the best one anyway. Assert
        // it on the TRACE: the running best may only change on a sample whose violations are all <= 0,
        // so any sample carrying a positive violation must leave the preceding value untouched.
        var task = new SuiteNumericTask(id, 42);
        RunRecord record = await QualityExperiment.RunAsync(QualityTask.Sphere, QualityMethod.RandomSearch, 72, 64, task);

        Assert.Equal("completed", record.Status);
        Assert.Contains(record.Samples, sample => sample.ConstraintViolations.Any(value => value > 0));

        double? previous = null;
        bool everFeasible = false;
        foreach (SampleRecord sample in record.Samples)
        {
            bool infeasible = sample.ConstraintViolations.Any(value => value > 0);
            if (infeasible)
                Assert.Equal(previous, sample.BestLoss);
            else if (sample.Quality.HasValue)
                everFeasible = true;
            previous = sample.BestLoss;
        }

        Assert.True(everFeasible, "the fixture must reach at least one feasible sample for this to mean anything");
        Assert.NotNull(record.FinalLoss);
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

    private static string Request(string partition, string[]? methods = null, bool crossFamily = false)
    {
        string[] ids = partition switch
        {
            "development" => ["block-trap", "knapsack", "rastrigin"],
            "selection" => ["coupled-absolute", "diffusion-control", "stochastic-regression"],
            _ => ["inventory-risk", "robust-design", "spin-glass"]
        };
        if (crossFamily) ids[0] = "matrix-missing";
        return JsonSerializer.Serialize(new
        {
            schema = "aidotnet-numeric-suite-request-v1",
            partition,
            mode = "contract-smoke",
            plan_hash = new string('a', 64),
            configuration_hash = new string('b', 64),
            source_revision = new string('c', 40),
            budget = 8,
            instances = ids.Select(id => new { id, seed = 42, search_seeds = new[] { 72 } }).ToArray(),
            methods = methods ?? ["RandomSearch", "HillClimb", "FixedMapElites"]
        });
    }

    [Theory]
    [InlineData("development")]
    [InlineData("selection")]
    [InlineData("final")]
    public async Task RunnerExecutesExactlyDeclaredMethodsAndRetainsEnvironmentAndRawMeasurements(string partition)
    {
        string directory = Directory.CreateTempSubdirectory("us01-runner-tests-").FullName;
        try
        {
            string request = Path.Combine(directory, "request.json"), output = Path.Combine(directory, "output.json");
            File.WriteAllText(request, Request(partition));
            Assert.Equal(0, await RepresentativeSuite.RunAsync([request, output]));
            using var report = JsonDocument.Parse(File.ReadAllText(output));
            var root = report.RootElement;
            Assert.Equal("completed", root.GetProperty("status").GetString());
            Assert.Equal(9, root.GetProperty("runs").GetArrayLength());
            Assert.Equal(64, root.GetProperty("environment").GetProperty("benchmark_assembly_hash").GetString()!.Length);
            Assert.Equal(JsonValueKind.Object, root.GetProperty("environment").GetProperty("dependency_manifest").ValueKind);
            foreach (var run in root.GetProperty("runs").EnumerateArray())
            {
                var measurement = run.GetProperty("measurement");
                Assert.Contains(measurement.GetProperty("method").GetString(), new[] { "RandomSearch", "HillClimb", "FixedMapElites" });
                Assert.Equal(8, measurement.GetProperty("samples").GetArrayLength());
                foreach (var sample in measurement.GetProperty("samples").EnumerateArray())
                {
                    Assert.Equal(JsonValueKind.Number, sample.GetProperty("quality").ValueKind);
                    Assert.Equal(JsonValueKind.Array, sample.GetProperty("constraint_violations").ValueKind);
                    Assert.Equal(2, sample.GetProperty("descriptors").EnumerateObject().Count());
                }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidRequestCannotCreateAnOutputOrDispatchAnExperiment(bool unknownTask)
    {
        string directory = Directory.CreateTempSubdirectory("us01-request-tests-").FullName;
        try
        {
            string request = Path.Combine(directory, "request.json"), output = Path.Combine(directory, "output.json");
            File.WriteAllText(request, Request("development", unknownTask ? null : ["RandomSearch", "HillClimb", "invented"], unknownTask));
            await Assert.ThrowsAnyAsync<Exception>(() => RepresentativeSuite.RunAsync([request, output]));
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExistingEvidenceIsNeverOverwritten()
    {
        string directory = Directory.CreateTempSubdirectory("us01-overwrite-tests-").FullName;
        try
        {
            string request = Path.Combine(directory, "request.json"), output = Path.Combine(directory, "output.json");
            File.WriteAllText(request, Request("development"));
            File.WriteAllText(output, "retained original");
            await Assert.ThrowsAsync<IOException>(() => RepresentativeSuite.RunAsync([request, output]));
            Assert.Equal("retained original", File.ReadAllText(output));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
#endif
