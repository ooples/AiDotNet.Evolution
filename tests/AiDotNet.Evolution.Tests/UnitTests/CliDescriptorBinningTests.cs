using AiDotNet.Evolution.Cli;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// E-4 (#347): run.json's <c>search.metricDescriptors</c> chooses each axis's binning, and program length follows the
/// lengths the search produces unless told otherwise. The third pilot's 0-10,000 length axis put every 400-550
/// character program in one cell.
/// </summary>
public sealed class CliDescriptorBinningTests
{
    [Fact]
    public void LengthAdaptsToTheObservedLengthsByDefault()
    {
        EvolutionDescriptorDefinition length = new RunMetricDescriptor { Name = "length", Bins = 10 }.ToDefinition(10000);

        Assert.Equal(EvolutionDescriptorBinning.AdaptiveMinMax, length.Binning);
        Assert.Equal((0.0, 10000.0, 10), (length.Minimum, length.Maximum, length.BinCount));
        Assert.Equal(EvolutionOutOfRangePolicy.Clamp, length.OutOfRangePolicy);
    }

    [Fact]
    public void AFixedLengthAxisKeepsItsBudgetRangeAndRejectsLongerPrograms()
    {
        EvolutionDescriptorDefinition linear = new RunMetricDescriptor
        {
            Name = "length", Bins = 10, Binning = EvolutionDescriptorBinning.Linear
        }.ToDefinition(10000);
        EvolutionDescriptorDefinition logarithmic = new RunMetricDescriptor
        {
            Name = "length", Bins = 4, Binning = EvolutionDescriptorBinning.Logarithmic
        }.ToDefinition(10000);

        Assert.Equal((0.0, 10000.0, EvolutionOutOfRangePolicy.Reject), (linear.Minimum, linear.Maximum, linear.OutOfRangePolicy));
        // A log axis cannot start at 0, so length starts at one character.
        Assert.Equal((1.0, 10000.0, EvolutionDescriptorBinning.Logarithmic), (logarithmic.Minimum, logarithmic.Maximum, logarithmic.Binning));
    }

    [Fact]
    public void AMetricAxisUsesItsOwnBoundsAndEveryBinningOption()
    {
        EvolutionDescriptorDefinition plain = new RunMetricDescriptor { Name = "runtime", Minimum = 2, Maximum = 8, Bins = 3 }
            .ToDefinition(10000);
        EvolutionDescriptorDefinition quantile = new RunMetricDescriptor
        {
            Name = "runtime", Minimum = 2, Maximum = 8, Bins = 3, Binning = EvolutionDescriptorBinning.AdaptiveQuantile,
            SampleCapacity = 64, OutOfRange = EvolutionOutOfRangePolicy.OverflowBins
        }.ToDefinition(10000);
        EvolutionDescriptorDefinition edges = new RunMetricDescriptor
        {
            Name = "runtime", Binning = EvolutionDescriptorBinning.ExplicitEdges, Edges = new List<double> { 0, 1, 10, 100 }
        }.ToDefinition(10000);

        Assert.Equal((EvolutionDescriptorBinning.Linear, 2.0, 8.0, EvolutionOutOfRangePolicy.Clamp),
            (plain.Binning, plain.Minimum, plain.Maximum, plain.OutOfRangePolicy));
        Assert.Equal((64, EvolutionOutOfRangePolicy.OverflowBins), (quantile.SampleCapacity, quantile.OutOfRangePolicy));
        Assert.Equal(new double[] { 0, 1, 10, 100 }, edges.Edges);
    }

    [Fact]
    public void ARunFileNamesTheDescriptorItCannotBuild()
    {
        using var directory = new TemporaryDirectory();
        string path = WriteRun(directory.Path, """
            [ { "name": "length", "bins": 4, "binning": "AdaptiveQuantile" },
              { "name": "runtime", "bins": 4, "edges": [0, 1, 2] } ]
            """);

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => RunCommand.Load(path));

        Assert.Contains("search.metricDescriptors[1] ('runtime') is invalid", refused.Message);
        Assert.Contains("edges apply only to binning ExplicitEdges", refused.Message);
    }

    [Fact]
    public void ARunFileReadsTheBinningByName()
    {
        using var directory = new TemporaryDirectory();
        string path = WriteRun(directory.Path, """
            [ { "name": "length", "bins": 4, "binning": "AdaptiveQuantile", "sampleCapacity": 16 },
              { "name": "runtime", "binning": "ExplicitEdges", "edges": [0, 1, 2], "outOfRange": "Reject" } ]
            """);

        RunFile run = RunCommand.Load(path);

        Assert.Equal(new EvolutionDescriptorBinning?[] { EvolutionDescriptorBinning.AdaptiveQuantile, EvolutionDescriptorBinning.ExplicitEdges },
            run.Search.MetricDescriptors.Select(metric => metric.Binning));
        Assert.Equal(16, run.Search.MetricDescriptors[0].SampleCapacity);
        Assert.Equal(EvolutionOutOfRangePolicy.Reject, run.Search.MetricDescriptors[1].OutOfRange);
    }

    private static string WriteRun(string directory, string descriptors)
    {
        string path = Path.Combine(directory, "run.json");
        File.WriteAllText(path, $$"""
            {
              "schema": "aidotnet-evolve-run-v1",
              "runId": "binning",
              "initialProgram": "initial.py",
              "evaluator": "evaluator.py",
              "model": { "endpoint": "http://127.0.0.1:9/v1", "name": "fake-model" },
              "budget": { "maxEvaluations": 3, "seed": 7, "evaluationTimeLimitSeconds": 30 },
              "search": { "metricDescriptors": {{descriptors}} },
              "output": "out",
              "allowUnsandboxedExecution": true
            }
            """);
        return path;
    }
}
