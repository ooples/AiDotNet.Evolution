using AiDotNet.Evolution.Surrogates;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class NumericSurrogateTests
{
    private static EvolutionSearchSpace Space() => new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Real("x", 0, 1)).Build();
    private static EvolutionCanonicalGenome<EvolutionSearchGenome> Genome(EvolutionSearchSpace space, double x)
    {
        var genome = space.CreateGenome(new[] { new KeyValuePair<string, EvolutionParameterValue>("x", EvolutionParameterValue.Numeric(x)) });
        return new(genome, genome.Identity);
    }
    private static EvolutionSurrogateObservation<EvolutionSearchGenome> Observation(EvolutionSearchSpace space, int id, double x,
        double quality = -0.5, string task = "task", string? sample = null)
    {
        var genome = Genome(space, x); var lineage = new EvolutionLineage(null, null, "measured", null, id, 0, (ulong)id);
        var evaluation = new EvolutionEvaluation(id, genome.Id, EvolutionEvaluationStatus.Completed, quality, EvolutionOptimizationDirection.Maximize,
            new Dictionary<string, double>(), Array.Empty<double>(), Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1),
            lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), task, "eval", "config");
        if (sample is not null) evaluation = evaluation.WithMeasurementOrigin(new EvolutionMeasurementOrigin(new string('a', 64), "run", id.ToString(),
            new[] { sample }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1, "calls", "test-v1"));
        return new(new EvolutionCandidate<EvolutionSearchGenome>(id, genome, lineage), evaluation);
    }
    private static EvolutionSurrogateObservation<EvolutionSearchGenome>[] Records(EvolutionSearchSpace space) =>
        Enumerable.Range(0, 32).Select(i => Observation(space, i, i / 31d)).ToArray();

    [Fact]
    public async Task ConstantFunctionHasDisjointValidationAndExactAuditedCosts()
    {
        var space = Space(); var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1));
        var records = Records(space).ToList(); var fit = await trainer.FitAsync(records);
        var model = Assert.IsType<ValidatedNearestNeighborModel>(fit.Value);
        Assert.True(model.IsReliable); Assert.Equal("accepted", model.Validation.Reason);
        Assert.Equal(20, model.Validation.TrainingGroups); Assert.Equal(6, model.Validation.CalibrationGroups); Assert.Equal(6, model.Validation.ValidationGroups);
        Assert.Equal(32, model.TrainingGenomeIds.Concat(model.CalibrationGenomeIds).Concat(model.ValidationGenomeIds).Distinct().Count());
        Assert.Equal(0, model.Validation.ResidualRadius); Assert.Equal(0, model.Validation.RelativeMeanAbsoluteError); Assert.Equal(1, model.Validation.EmpiricalCoverage);
        Assert.Equal(544, model.Validation.CoordinateWork); Assert.Equal(0.020544m, fit.Actual["cost_units"]);
        var query = new[] { Genome(space, 0.2), Genome(space, 0.5), Genome(space, 0.9) };
        var predicted = await model.PredictAsync(query);
        Assert.All(predicted.Value, value => { Assert.Equal(-0.5, value.Mean); Assert.Equal(0, value.Uncertainty); Assert.True(value.WithinTrainingDomain); });
        Assert.Equal(0.005126m, predicted.Actual["cost_units"]);
        Assert.True(trainer.MaximumTrainingCost(32)["cost_units"] >= fit.Actual["cost_units"]);
        Assert.True(trainer.MaximumInferenceCost(3, 32)["cost_units"] >= predicted.Actual["cost_units"]);
        records.Clear();
        Assert.Equal(predicted.Value.Select(p => p.Mean), (await model.PredictAsync(query)).Value.Select(p => p.Mean));
    }

    [Fact]
    public async Task InputReorderingDoesNotChangeFittedIdentityOrPartitions()
    {
        var space = Space(); var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1));
        var first = Assert.IsType<ValidatedNearestNeighborModel>((await trainer.FitAsync(Records(space))).Value);
        var second = Assert.IsType<ValidatedNearestNeighborModel>((await trainer.FitAsync(Enumerable.Reverse(Records(space)).ToArray())).Value);
        Assert.Equal(first.VersionHash, second.VersionHash); Assert.Equal(first.TrainingGenomeIds, second.TrainingGenomeIds);
        Assert.Equal(first.CalibrationGenomeIds, second.CalibrationGenomeIds); Assert.Equal(first.ValidationGenomeIds, second.ValidationGenomeIds);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(17)]
    public async Task TooFewDistinctGenomesRemainExplicitlyUnreliable(int distinct)
    {
        var space = Space(); var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1));
        var records = Enumerable.Range(0, 34).Select(i => Observation(space, i, (i % distinct) / (double)distinct)).ToArray();
        var model = Assert.IsType<ValidatedNearestNeighborModel>((await trainer.FitAsync(records)).Value);
        Assert.False(model.IsReliable); Assert.Equal("insufficient-distinct-genomes", model.Validation.Reason);
        Assert.Equal(distinct, model.Validation.TrainingGroups); Assert.Empty(model.CalibrationGenomeIds); Assert.Empty(model.ValidationGenomeIds);
    }

    [Fact]
    public async Task ReplicateGenomesStayInOnePartition()
    {
        var space = Space(); var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1));
        var records = Enumerable.Range(0, 64).Select(i => Observation(space, i, (i % 32) / 31d)).ToArray();
        var model = Assert.IsType<ValidatedNearestNeighborModel>((await trainer.FitAsync(records)).Value);
        Assert.True(model.IsReliable);
        Assert.Equal(32, model.TrainingGenomeIds.Concat(model.CalibrationGenomeIds).Concat(model.ValidationGenomeIds).Distinct().Count());
        Assert.Equal(32, model.Validation.TrainingGroups + model.Validation.CalibrationGroups + model.Validation.ValidationGroups);
    }

    [Fact]
    public async Task ValidationErrorAndDistanceGuardsRejectUnreliableRanking()
    {
        var space = Space(); var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1, maximumRelativeMeanAbsoluteError: 0));
        var noisy = Enumerable.Range(0, 32).Select(i => Observation(space, i, i / 31d, i % 2 == 0 ? -1 : 1)).ToArray();
        var weak = Assert.IsType<ValidatedNearestNeighborModel>((await trainer.FitAsync(noisy)).Value);
        Assert.False(weak.IsReliable); Assert.Equal("validation-error", weak.Validation.Reason); Assert.True(weak.Validation.RelativeMeanAbsoluteError > 0);
        var local = Assert.IsType<ValidatedNearestNeighborModel>((await trainer.FitAsync(Enumerable.Range(0, 32).Select(i => Observation(space, i, i / 1000d)).ToArray())).Value);
        Assert.True(local.IsReliable);
        Assert.False((await local.PredictAsync(new[] { Genome(space, 1) })).Value[0].WithinTrainingDomain);
    }

    [Fact]
    public async Task DirectBackendRejectsDuplicatedOriginalSamplesAndMismatchedProvenance()
    {
        var space = Space(); var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1));
        foreach (var records in new[] {
            new[] { Observation(space, 0, 0, sample: "same"), Observation(space, 1, 1, sample: "same") },
            new[] { Observation(space, 0, 0), Observation(space, 1, 1, task: "other") },
            new[] { Observation(space, 0, 0, quality: 2), Observation(space, 1, 1) },
            new[] { Observation(space, 0, 0), Observation(space, 0, 0) },
            new[] { Observation(space, 0, 0) }, Array.Empty<EvolutionSurrogateObservation<EvolutionSearchGenome>>() })
            await Assert.ThrowsAsync<ArgumentException>(async () => await trainer.FitAsync(records));
    }

    [Fact]
    public async Task PredictionPoolsAreBoundedCompatibleAndCancelable()
    {
        var space = Space(); var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1));
        var model = (await trainer.FitAsync(Records(space))).Value;
        await Assert.ThrowsAsync<ArgumentException>(async () => await model.PredictAsync(Array.Empty<EvolutionCanonicalGenome<EvolutionSearchGenome>>()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await model.PredictAsync(new[] { Genome(space, 0), Genome(space, 0) }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await model.PredictAsync(Enumerable.Range(0, 65).Select(i => Genome(space, i / 64d)).ToArray()));
        var other = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Real("other", 0, 1)).Build();
        var otherGenome = other.Sample(new StableRandom(1));
        await Assert.ThrowsAsync<ArgumentException>(async () => await model.PredictAsync(new[] { new EvolutionCanonicalGenome<EvolutionSearchGenome>(otherGenome, otherGenome.Identity) }));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await trainer.FitAsync(Records(space), canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await model.PredictAsync(new[] { Genome(space, 0) }, canceled.Token));
    }

    [Theory]
    [InlineData(2, EvolutionSurrogateSelectionReason.UnreliableModel, "insufficient-distinct-genomes")]
    [InlineData(32, EvolutionSurrogateSelectionReason.Acquisition, "accepted")]
    public async Task SelectorRetainsBackendReliabilityEvidenceOnAcquisitionAndFallback(int count, EvolutionSurrogateSelectionReason expected, string reason)
    {
        var space = Space(); var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1));
        var records = Records(space).Take(count).ToArray();
        var ledger = new EvolutionResourceLedger("numeric-selection", EvolutionResources.Of("cost_units", 10));
        var selector = new EvolutionSurrogateSelector<EvolutionSearchGenome>(trainer, ledger, "task", "eval",
            EvolutionResources.Of("cost_units", 0.01m), trainer.MaximumTrainingCost(32), trainer.MaximumInferenceCost(2, 32),
            minimumSamples: 2, explorationProbability: 0.05);
        ulong seed = 0;
        for (; ; seed++) { var check = new StableRandom(seed); check.NextInt(2); if (check.NextDouble() >= 0.05) break; }
        var selected = await selector.SelectAsync("numeric", records, new StableRandom(seed), _ =>
            new ValueTask<EvolutionResourceResult<IReadOnlyList<EvolutionCanonicalGenome<EvolutionSearchGenome>>>>(
                new EvolutionResourceResult<IReadOnlyList<EvolutionCanonicalGenome<EvolutionSearchGenome>>>(
                    new[] { Genome(space, 0.2), Genome(space, 0.5) }, EvolutionResources.Of("cost_units", 0.01m))));
        Assert.Equal(expected, selected.Reason); Assert.NotNull(selected.ValidationReport);
        Assert.Equal(reason, selected.ValidationReport!.Reason);
        Assert.Equal(count == 32 ? 6 : 0, selected.ValidationReport.Metrics["validation_groups"]);
        Assert.NotEmpty(selected.ValidationReport.PolicyVersionHash);
        Assert.Equal(0, ledger.Snapshot().Unknown);
        Assert.Equal(count == 32 ? 3 : 2, ledger.Snapshot().Receipts.Count);
    }

    [Fact]
    public async Task IndependentValidationDetectsRadiusOverconfidence()
    {
        var space = Space(); var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1, maximumRelativeMeanAbsoluteError: 1));
        var original = Assert.IsType<ValidatedNearestNeighborModel>((await trainer.FitAsync(Records(space))).Value);
        // Change ONLY the validation labels. Partitioning must depend on genome identity, not labels.
        var validation = new HashSet<string>(original.ValidationGenomeIds);
        var shifted = Enumerable.Range(0, 32).Select(i => Observation(space, i, i / 31d, validation.Contains(Genome(space, i / 31d).Id) ? 1 : -0.5)).ToArray();
        var model = Assert.IsType<ValidatedNearestNeighborModel>((await trainer.FitAsync(shifted)).Value);
        Assert.Equal(original.ValidationGenomeIds, model.ValidationGenomeIds);
        Assert.Equal(0, model.Validation.ResidualRadius); Assert.Equal(0, model.Validation.EmpiricalCoverage);
        Assert.Equal(0.75, model.Validation.RelativeMeanAbsoluteError);
        Assert.False(model.IsReliable); Assert.Equal("validation-coverage", model.ValidationReport.Reason);
    }

    [Fact]
    public async Task OriginalSamplesAndTariffsParticipateInFittedIdentity()
    {
        var space = Space(); var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1));
        var first = Enumerable.Range(0, 32).Select(i => Observation(space, i, i / 31d, sample: "sample-" + i)).ToArray();
        var second = Enumerable.Range(0, 32).Select(i => Observation(space, i, i / 31d, sample: "other-" + i)).ToArray();
        var model = (await trainer.FitAsync(first)).Value;
        Assert.NotEqual(model.VersionHash, (await trainer.FitAsync(second)).Value.VersionHash);
        var repriced = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1, coordinateCost: 0.000002m));
        Assert.NotEqual(trainer.VersionHash, repriced.VersionHash);
        Assert.NotEqual(model.VersionHash, (await repriced.FitAsync(first)).Value.VersionHash);
        Assert.Equal(0.021088m, (await repriced.FitAsync(first)).Actual["cost_units"]);
    }

    [Fact]
    public async Task TrainingAndReservationsEnforceBoundsBeforeWork()
    {
        var space = Space(); var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await trainer.FitAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(async () => await trainer.FitAsync(Enumerable.Range(0, 257).Select(i => Observation(space, i, i / 256d)).ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await trainer.FitAsync(new[] { Records(space)[0], null! }));
        var model = (await trainer.FitAsync(Records(space))).Value;
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await model.PredictAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(async () => await model.PredictAsync(new EvolutionCanonicalGenome<EvolutionSearchGenome>[] { null! }));
        foreach (int count in new[] { 0, 1, 257 }) Assert.Throws<ArgumentOutOfRangeException>(() => trainer.MaximumTrainingCost(count));
        foreach (int count in new[] { 0, 65 }) Assert.Throws<ArgumentOutOfRangeException>(() => trainer.MaximumInferenceCost(count));
        foreach (int count in new[] { 0, 257 }) Assert.Throws<ArgumentOutOfRangeException>(() => trainer.MaximumInferenceCost(1, count));
        Assert.Throws<ArgumentNullException>(() => new ValidatedNearestNeighborTrainer(null!, new NumericSurrogateOptions(-1, 1)));
        Assert.Throws<ArgumentNullException>(() => new ValidatedNearestNeighborTrainer(space, null!));
        var large = new EvolutionSearchSpaceBuilder();
        for (int i = 0; i < 65; i++) large.Add(EvolutionParameter.Real("x" + i, 0, 1));
        Assert.Throws<ArgumentException>(() => new ValidatedNearestNeighborTrainer(large.Build(), new NumericSurrogateOptions(-1, 1)));
    }

    [Fact]
    public void ValidationReportsAreDetachedFiniteAndBounded()
    {
        var values = new Dictionary<string, double> { ["error"] = 0.25 };
        var report = new EvolutionSurrogateValidationReport("v1", "accepted", values);
        values["error"] = 99;
        Assert.Equal(0.25, report.Metrics["error"]);
        Assert.Throws<ArgumentException>(() => new EvolutionSurrogateValidationReport("bad\n", "accepted", values));
        Assert.Throws<ArgumentException>(() => new EvolutionSurrogateValidationReport("v1", "bad\n", values));
        Assert.Throws<ArgumentException>(() => new EvolutionSurrogateValidationReport("v1", "valid", new Dictionary<string, double> { ["error"] = double.NaN }));
        Assert.Throws<ArgumentException>(() => new EvolutionSurrogateValidationReport("v1", "valid", Enumerable.Range(0, 33).ToDictionary(i => i.ToString(), _ => 0.0)));
    }

    [Theory]
    [InlineData("bounds")]
    [InlineData("neighbors")]
    [InlineData("distance")]
    [InlineData("quantile")]
    [InlineData("coverage")]
    [InlineData("error")]
    [InlineData("price")]
    public void InvalidOptionsAreRejected(string invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NumericSurrogateOptions(-1, invalid == "bounds" ? -1 : 1,
            neighbors: invalid == "neighbors" ? 0 : 3, maximumDistance: invalid == "distance" ? double.NaN : 0.25,
            calibrationQuantile: invalid == "quantile" ? 1 : 0.9, minimumValidationCoverage: invalid == "coverage" ? 0 : 0.6,
            maximumRelativeMeanAbsoluteError: invalid == "error" ? -1 : 0.15, coordinateCost: invalid == "price" ? -1 : 0.000001m));
    }
}
