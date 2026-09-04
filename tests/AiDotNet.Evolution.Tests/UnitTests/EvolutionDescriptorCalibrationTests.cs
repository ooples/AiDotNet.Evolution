using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionDescriptorCalibrationTests
{
    [Fact]
    public void AxisCoversTheObservedSpanPlusMargin()
    {
        EvolutionDescriptorDefinition axis = Assert.Single(
            EvolutionDescriptorCalibration.FromObservations(
                Observations(("x", 2.0), ("x", 6.0)),
                options: new EvolutionDescriptorCalibrationOptions { Padding = 0.25, BinCount = 8 }));

        Assert.Equal("x", axis.Name);
        Assert.Equal(1.0, axis.Minimum, 10);
        Assert.Equal(7.0, axis.Maximum, 10);
        Assert.Equal(8, axis.BinCount);
        Assert.Equal(EvolutionOutOfRangePolicy.Grow, axis.OutOfRangePolicy);
        Assert.True(axis.TryGetBin(2.0, out int low));
        Assert.True(axis.TryGetBin(6.0, out int high));
        Assert.True(low > 0);
        Assert.True(high < axis.BinCount - 1);
    }

    [Fact]
    public void ObservationOrderDoesNotChangeTheGrid()
    {
        IReadOnlyList<EvolutionDescriptorDefinition> forwards = EvolutionDescriptorCalibration.FromObservations(
            Observations(("x", 2.0), ("x", 6.0), ("x", 4.0)));
        IReadOnlyList<EvolutionDescriptorDefinition> backwards = EvolutionDescriptorCalibration.FromObservations(
            Observations(("x", 4.0), ("x", 6.0), ("x", 2.0)));

        Assert.Equal(forwards[0].ToCanonicalString(), backwards[0].ToCanonicalString());
    }

    [Fact]
    public void DiscoveredAndExplicitAxisOrdersAreStable()
    {
        var observations = new List<IReadOnlyDictionary<string, double>>
        {
            Values(("recall", 0.2), ("accuracy", 0.4)),
            Values(("latency", 30.0), ("accuracy", 0.8))
        };

        Assert.Equal(new[] { "accuracy", "latency", "recall" },
            EvolutionDescriptorCalibration.FromObservations(observations).Select(axis => axis.Name));
        Assert.Equal(new[] { "latency", "accuracy" },
            EvolutionDescriptorCalibration.FromObservations(
                observations, new[] { "latency", "accuracy" }).Select(axis => axis.Name));
    }

    [Fact]
    public void DegenerateAxisGetsAUsableWindowAndCanGrow()
    {
        EvolutionDescriptorDefinition degenerate = Assert.Single(
            EvolutionDescriptorCalibration.FromObservations(
                Observations(("flag", 0.0), ("flag", 0.0)),
                options: new EvolutionDescriptorCalibrationOptions { DegenerateSpan = 2.0 }));
        Assert.Equal(-1.0, degenerate.Minimum, 10);
        Assert.Equal(1.0, degenerate.Maximum, 10);
        Assert.True(degenerate.BinWidth > 0.01);

        EvolutionDescriptorDefinition seeded = Assert.Single(
            EvolutionDescriptorCalibration.FromObservations(Observations(("x", 0.0), ("x", 10.0))));
        EvolutionDescriptorDefinition widened = seeded.Widen(500.0);
        Assert.True(widened.TryGetBin(500.0, out _));
        Assert.Equal(seeded.BinWidth, widened.BinWidth, 10);
    }

    [Fact]
    public void NonFiniteMeasurementsDoNotDecideBounds()
    {
        var observations = new List<IReadOnlyDictionary<string, double>>
        {
            Values(("x", 5.0)),
            Values(("x", double.NaN)),
            Values(("x", 7.0))
        };

        EvolutionDescriptorDefinition axis = Assert.Single(
            EvolutionDescriptorCalibration.FromObservations(observations,
                options: new EvolutionDescriptorCalibrationOptions { Padding = 0 }));

        Assert.Equal(5.0, axis.Minimum, 10);
        Assert.Equal(7.0, axis.Maximum, 10);
    }

    [Fact]
    public void MissingBlankAndRepeatedAxesAreRefused()
    {
        ArgumentException missing = Assert.Throws<ArgumentException>(() =>
            EvolutionDescriptorCalibration.FromObservations(Observations(("x", 1.0)), new[] { "missing" }));
        Assert.Contains("missing", missing.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            EvolutionDescriptorCalibration.FromObservations(Observations(("x", 1.0)), new[] { " " }));
        Assert.Throws<ArgumentException>(() =>
            EvolutionDescriptorCalibration.FromObservations(Observations(("x", 1.0)), new[] { "x", "x" }));
        Assert.Throws<ArgumentException>(() =>
            EvolutionDescriptorCalibration.FromObservations(Observations(("x", 1.0)), Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() =>
            EvolutionDescriptorCalibration.FromObservations(
                new List<IReadOnlyDictionary<string, double>> { Values() }));
    }

    [Fact]
    public void InvalidCalibrationSettingsAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvolutionDescriptorCalibrationOptions { BinCount = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvolutionDescriptorCalibrationOptions { Padding = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvolutionDescriptorCalibrationOptions { DegenerateSpan = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvolutionDescriptorCalibrationOptions
            {
                OutOfRangePolicy = (EvolutionOutOfRangePolicy)int.MaxValue
            }.Validate());
    }

    [Fact]
    public async Task TaskCalibrationMeasuresEachSeedOnceAndSkipsFailures()
    {
        var task = new CountingCalibrationTask(failOnValue: 30);
        TestGenome[] seeds = { new(10), new(30), new(20) };

        IReadOnlyList<EvolutionDescriptorDefinition> axes = await EvolutionDescriptorCalibration.CalibrateAsync(
            task, seeds, options: new EvolutionDescriptorCalibrationOptions { Padding = 0 });

        Assert.Equal(3, task.Evaluations);
        EvolutionDescriptorDefinition axis = Assert.Single(axes);
        Assert.Equal(10, axis.Minimum, 10);
        Assert.Equal(20, axis.Maximum, 10);
    }

    [Fact]
    public async Task EmptySeedSetIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => EvolutionDescriptorCalibration.CalibrateAsync(
            new CountingCalibrationTask(), Array.Empty<TestGenome>()));
    }

    private static List<IReadOnlyDictionary<string, double>> Observations(
        params (string Name, double Value)[] values)
    {
        var observations = new List<IReadOnlyDictionary<string, double>>(values.Length);
        foreach ((string name, double value) in values) observations.Add(Values((name, value)));
        return observations;
    }

    private static Dictionary<string, double> Values(params (string Name, double Value)[] values)
    {
        var observation = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string name, double value) in values) observation[name] = value;
        return observation;
    }

    private sealed class CountingCalibrationTask : IEvolutionTask<TestGenome>
    {
        private readonly int? _failOnValue;
        private int _evaluations;

        public CountingCalibrationTask(int? failOnValue = null) => _failOnValue = failOnValue;

        public int Evaluations => _evaluations;
        public string Id => "calibration-task";
        public string VersionHash => "calibration-task-v1";
        public string EvaluatorVersionHash => "calibration-evaluator-v1";

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome,
            CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome,
                genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            _evaluations++;
            int value = candidate.CanonicalGenome.Genome.Value;
            if (_failOnValue == value)
                return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Failed("nope", "synthetic failure"));

            return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(
                value, new Dictionary<string, double>(StringComparer.Ordinal) { ["x"] = value }));
        }
    }
}
