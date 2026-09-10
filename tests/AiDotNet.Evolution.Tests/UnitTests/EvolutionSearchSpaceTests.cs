using System.Globalization;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionSearchSpaceTests
{
    internal static EvolutionSearchSpace Mixed() => new EvolutionSearchSpaceBuilder()
        .Add(EvolutionParameter.Categorical("model", new[] { "tree", "linear" }))
        .Add(EvolutionParameter.Integer("depth", 1, 20).When("model", EvolutionParameterValue.Categorical("tree")))
        .Add(EvolutionParameter.Logarithmic("rate", 0.0001, 10).When("model", EvolutionParameterValue.Categorical("linear")))
        .Add(EvolutionParameter.Real("mix", -1, 1))
        .Add(EvolutionParameter.Integer("fixed", 7, 7))
        .Build();
    internal static EvolutionSearchSpace Continuous(int dimensions = 2)
    {
        var builder = new EvolutionSearchSpaceBuilder();
        for (int i = 0; i < dimensions; i++) builder.Add(EvolutionParameter.Real("x" + i, -5, 5));
        return builder.Build();
    }

    [Fact]
    public void SamplingIsValidReproducibleAndUsesEverySupportedDomain()
    {
        EvolutionSearchSpace space = Mixed();
        var first = StableRandom.CreateStream(42, 1); var second = StableRandom.CreateStream(42, 1);
        var models = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            EvolutionSearchGenome genome = space.Sample(first);
            Assert.Equal(genome.Identity, space.Sample(second).Identity);
            Assert.Equal(genome.Identity, space.Validate(genome).Identity);
            Assert.InRange(genome.Number("mix"), -1, 1); Assert.Equal(7, genome.Number("fixed"));
            models.Add(genome.Category("model"));
            if (genome.Category("model") == "tree")
            {
                Assert.False(genome.Values.ContainsKey("rate"));
                Assert.Equal(Math.Truncate(genome.Number("depth")), genome.Number("depth"));
                Assert.InRange(genome.Number("depth"), 1, 20);
            }
            else { Assert.False(genome.Values.ContainsKey("depth")); Assert.InRange(genome.Number("rate"), 0.0001, 10); }
        }
        Assert.Equal(2, models.Count);
    }

    [Fact]
    public void InactiveValuesDoNotAlterIdentityButUnknownNamesAreRejected()
    {
        var space = Mixed();
        var values = new Dictionary<string, EvolutionParameterValue>
        {
            ["model"] = EvolutionParameterValue.Categorical("tree"),
            ["depth"] = EvolutionParameterValue.Numeric(2),
            ["mix"] = EvolutionParameterValue.Numeric(0),
            ["fixed"] = EvolutionParameterValue.Numeric(7)
        };
        EvolutionSearchGenome first = space.CreateGenome(values);
        values["rate"] = EvolutionParameterValue.Categorical("invalid-but-inactive");
        Assert.Equal(first.Identity, space.CreateGenome(values).Identity);
        values["mix"] = EvolutionParameterValue.Numeric(-0.0);
        Assert.Equal(first.Identity, space.CreateGenome(values).Identity);
        Assert.Equal(0, first.Number("mix"));
        values["unexpected"] = EvolutionParameterValue.Numeric(2);
        Assert.Throws<ArgumentException>(() => space.CreateGenome(values));
    }

    [Fact]
    public void ActivationCascadesAndMultipleConditionsAreValidatedInDependencyOrder()
    {
        var space = new EvolutionSearchSpaceBuilder()
            .Add(EvolutionParameter.Categorical("family", new[] { "a", "b" }))
            .Add(EvolutionParameter.Integer("layers", 1, 2).When("family", EvolutionParameterValue.Categorical("a")))
            .Add(EvolutionParameter.Real("dropout", 0, 1).When("family", EvolutionParameterValue.Categorical("a"))
                .When("layers", EvolutionParameterValue.Numeric(2))).Build();
        var b = space.CreateGenome(new Dictionary<string, EvolutionParameterValue> { ["family"] = EvolutionParameterValue.Categorical("b") });
        Assert.Single(b.Values);
        Assert.Throws<ArgumentException>(() => space.CreateGenome(new Dictionary<string, EvolutionParameterValue>
        { ["family"] = EvolutionParameterValue.Categorical("a"), ["layers"] = EvolutionParameterValue.Numeric(2) }));
        Assert.Throws<ArgumentException>(() => new EvolutionSearchSpaceBuilder()
            .Add(EvolutionParameter.Integer("child", 1, 2).When("parent", EvolutionParameterValue.Numeric(1)))
            .Add(EvolutionParameter.Integer("parent", 1, 2)).Build());
        Assert.Throws<ArgumentException>(() => new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Integer("parent", 1, 2))
            .Add(EvolutionParameter.Integer("child", 1, 2).When("parent", EvolutionParameterValue.Numeric(3))).Build());
    }

    [Fact]
    public void SpaceAndGenomeSnapshotsAreIndependentOfInputsAndBuilderEdits()
    {
        var choices = new[] { "a", "b" };
        var builder = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Categorical("kind", choices));
        var first = builder.Build(); choices[0] = "changed";
        builder.Add(EvolutionParameter.Real("other", 0, 1));
        Assert.Single(first.Parameters); Assert.Equal("a", first.Parameters[0].Categories[0]);
        var genome = first.Sample(StableRandom.CreateStream(1, 1));
        EvolutionSearchGenome snapshot = genome.CreateOwnedSnapshot();
        Assert.NotSame(genome, snapshot); Assert.NotSame(genome.Values, snapshot.Values); Assert.Equal(genome.Identity, snapshot.Identity);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, EvolutionParameterValue>)genome.Values).Clear());
        Assert.NotEqual(first.VersionHash, builder.Build().VersionHash);
    }

    [Fact]
    public void CodecRoundTripsAndRejectsIncompatibleOrAmbiguousPayloads()
    {
        EvolutionSearchSpace space = Mixed();
        EvolutionSearchGenome genome = space.Sample(StableRandom.CreateStream(9, 2));
        string json = space.Serialize(genome);
        Assert.Equal(json, space.Serialize(space.Deserialize(json)));
        Assert.Equal(genome.Identity, space.Deserialize(json).Identity);
        Assert.Throws<ArgumentException>(() => Continuous().Deserialize(json));
        Assert.Throws<ArgumentException>(() => space.Deserialize("{\"schema\":\"wrong\",\"values\":{}}"));
        Assert.Throws<ArgumentException>(() => space.Deserialize("[]"));
        var simple = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Real("x", 0, 1)).Build();
        Assert.Throws<ArgumentException>(() => simple.Deserialize("{\"schema\":\"" + simple.VersionHash + "\",\"values\":{\"x\":0,\"x\":1}}"));
        Assert.Throws<ArgumentException>(() => simple.Deserialize("{\"schema\":\"" + simple.VersionHash + "\",\"values\":{\"x\":null}}"));
        Assert.Throws<ArgumentException>(() => simple.Deserialize(new string(' ', 256 * 1024 + 1)));
    }

    [Fact]
    public void FeaturesDistinguishInactiveAndMinimumValuesWithoutInventingCategoryOrder()
    {
        var space = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Categorical("kind", new[] { "a", "b" }))
            .Add(EvolutionParameter.Real("x", 0, 1).When("kind", EvolutionParameterValue.Categorical("a"))).Build();
        var a = space.CreateGenome(new Dictionary<string, EvolutionParameterValue> { ["kind"] = EvolutionParameterValue.Categorical("a"), ["x"] = EvolutionParameterValue.Numeric(0) });
        var b = space.CreateGenome(new Dictionary<string, EvolutionParameterValue> { ["kind"] = EvolutionParameterValue.Categorical("b") });
        Assert.Equal(new[] { 1d, 1, 0, 1, 0 }, space.EncodeFeatures(a));
        Assert.Equal(new[] { 1d, 0, 1, 0, 0 }, space.EncodeFeatures(b));
        Assert.Equal(5, space.FeatureCount);
    }

    [Fact]
    public void NumericNormalizationHonorsLogarithmicAndIntegerDomains()
    {
        var logarithmic = EvolutionParameter.Logarithmic("rate", 0.01, 100);
        Assert.Equal(1, logarithmic.FromNormalized(0.5).Number, 12);
        Assert.Equal(0.5, logarithmic.Normalize(EvolutionParameterValue.Numeric(1)), 12);
        var integer = EvolutionParameter.Integer("x", -2, 2);
        Assert.Equal(1, integer.FromNormalized(0.75).Number);
        Assert.False(integer.Contains(EvolutionParameterValue.Numeric(1.5)));
        Assert.Equal(-2, integer.FromNormalized(-100).Number); Assert.Equal(2, integer.FromNormalized(100).Number);
        Assert.Equal(0, EvolutionParameter.Real("fixed", 2, 2).Normalize(EvolutionParameterValue.Numeric(2)));
        Assert.Throws<ArgumentException>(() => integer.Normalize(EvolutionParameterValue.Numeric(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => logarithmic.FromNormalized(double.NaN));
    }

    [Fact]
    public void IdentityIsCultureIndependentAndNumericAndCategoricalValuesRemainDistinct()
    {
        var space = Continuous(); var genome = space.Sample(StableRandom.CreateStream(1, 0));
        string identity = genome.Identity; string schema = space.VersionHash;
        CultureInfo prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(identity, space.Deserialize(space.Serialize(genome)).Identity);
            Assert.Equal(schema, Continuous().VersionHash);
        }
        finally { CultureInfo.CurrentCulture = prior; }
        Assert.NotEqual(EvolutionParameterValue.Numeric(1), EvolutionParameterValue.Categorical("1"));
        Assert.Equal(EvolutionParameterValue.Numeric(1), EvolutionParameterValue.Numeric(1));
        Assert.Equal(EvolutionParameterValue.Categorical("x"), EvolutionParameterValue.Categorical("x"));
        Assert.Throws<InvalidOperationException>(() => EvolutionParameterValue.Numeric(1).Category);
        Assert.Throws<InvalidOperationException>(() => EvolutionParameterValue.Categorical("x").Number);
    }

    [Fact]
    public void MalformedAndUnboundedDefinitionsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new EvolutionSearchSpaceBuilder().Build());
        Assert.Throws<ArgumentException>(() => EvolutionParameter.Real(" ", 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionParameter.Real("x", 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionParameter.Real("x", 0, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionParameter.Logarithmic("x", 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionParameterValue.Numeric(double.NaN));
        Assert.Throws<ArgumentException>(() => EvolutionParameter.Categorical("x", new[] { "a", "a" }));
        Assert.Throws<ArgumentException>(() => EvolutionParameter.Categorical("x", Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => EvolutionParameter.Categorical("x", Enumerable.Range(0, 257).Select(i => "c" + i)));
        var builder = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Real("x", 0, 1));
        Assert.Throws<ArgumentException>(() => builder.Add(EvolutionParameter.Real("x", 1, 2)));
        Assert.Throws<ArgumentException>(() => EvolutionParameter.Real("x", 0, 1).When("p", Array.Empty<EvolutionParameterValue>()));
        Assert.Throws<ArgumentException>(() => EvolutionParameter.Real("x", 0, 1).When("p", EvolutionParameterValue.Numeric(1)).When("p", EvolutionParameterValue.Numeric(2)));
    }

    [Theory]
    [InlineData("mutation")]
    [InlineData("crossover")]
    [InlineData("restart")]
    public async Task SuppliedOperatorsAreDeterministicValidAndIndependentlyOwned(string kind)
    {
        var space = Mixed();
        var parent = space.Sample(StableRandom.CreateStream(4, 0)); var donor = space.Sample(StableRandom.CreateStream(8, 0));
        IVariationOperator<EvolutionSearchGenome> variation = kind switch
        {
            "mutation" => new SearchSpaceMutation(space, 1),
            "crossover" => new SearchSpaceCrossover(space),
            _ => new SearchSpaceRestart(space)
        };
        string before = space.Serialize(parent);
        var identities = new HashSet<string>();
        for (ulong seed = 0; seed < 100; seed++)
        {
            var first = await variation.ProposeAsync(Context(parent, donor, seed));
            var second = await variation.ProposeAsync(Context(parent, donor, seed));
            Assert.Equal(first.Identity, second.Identity); Assert.NotSame(parent, first);
            Assert.Equal(first.Identity, space.Validate(first).Identity); identities.Add(first.Identity);
        }
        Assert.True(identities.Count > 1); Assert.Equal(before, space.Serialize(parent));
    }

    [Fact]
    public async Task CrossoverWithoutDonorReturnsOwnedParentAndCancellationDoesNoWork()
    {
        var space = Mixed(); var parent = space.Sample(StableRandom.CreateStream(1, 0));
        var context = Context(parent, null, 1);
        var variation = new SearchSpaceCrossover(space);
        var result = await variation.ProposeAsync(context);
        Assert.Equal(parent.Identity, result.Identity); Assert.NotSame(parent, result);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => variation.ProposeAsync(context, cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SearchSpaceMutation(space).ProposeAsync(context, cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SearchSpaceRestart(space).ProposeAsync(context, cancel.Token).AsTask());
    }

    [Theory]
    [InlineData(EvolutionOptimizationDirection.Maximize)]
    [InlineData(EvolutionOptimizationDirection.Minimize)]
    public async Task LocalRefinementKeepsAValidIncumbentAndChargesAllTrials(EvolutionOptimizationDirection direction)
    {
        var space = Continuous(1);
        var seed = space.CreateGenome(new Dictionary<string, EvolutionParameterValue> { ["x0"] = EvolutionParameterValue.Numeric(4) });
        var ledger = new EvolutionResourceLedger("local", EvolutionResources.Of("cost_units", 100));
        int calls = 0;
        var refiner = new SearchSpaceLocalRefiner(space, ledger, (genome, _) =>
        {
            calls++; double square = genome.Number("x0") * genome.Number("x0");
            return new ValueTask<EvolutionTaskResult>(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed,
                direction == EvolutionOptimizationDirection.Maximize ? -square : square, direction, costUnits: 1));
        }, "square-v1", 1, steps: 32, direction: direction);
        EvolutionSearchGenome result = await refiner.RefineAsync(seed, new EvolutionRefinementContext(0, StableRandom.CreateStream(4, 0)));
        Assert.True(Math.Abs(result.Number("x0")) < 4); Assert.Equal(4, seed.Number("x0")); Assert.NotSame(seed, result);
        Assert.Equal(33, calls); Assert.Equal(33, ledger.Snapshot().Spent["cost_units"]);
        Assert.All(ledger.Snapshot().Receipts, receipt => Assert.Equal(EvolutionResourceStage.Refinement, receipt.Stage));
    }

    [Fact]
    public async Task RefinerDoesNotPromoteInfeasibleTrialsAndStopsBeforeUnfundedWork()
    {
        var space = Continuous(1); var seed = space.Sample(StableRandom.CreateStream(1, 0));
        var ledger = new EvolutionResourceLedger("local", EvolutionResources.Of("cost_units", 2));
        int calls = 0;
        var refiner = new SearchSpaceLocalRefiner(space, ledger, (_, _) =>
        {
            calls++;
            return new ValueTask<EvolutionTaskResult>(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1e10,
                constraintViolations: new[] { 1d }, costUnits: 1));
        }, "invalid-v1", 1, steps: 10);
        var result = await refiner.RefineAsync(seed, new EvolutionRefinementContext(0, StableRandom.CreateStream(2, 0)));
        Assert.Equal(seed.Identity, result.Identity); Assert.Equal(2, calls); Assert.Equal(1, ledger.Snapshot().Denied);
    }

    [Fact]
    public async Task TypedTaskIntegratesWithDeterministicEngineAndCheckpointCodec()
    {
        var space = Continuous();
        var seed = space.Sample(StableRandom.CreateStream(7, 0));
        var store = new InMemoryEvolutionCheckpointStore();
        var first = await Run(space, seed, 12, 1, store, false);
        var resumed = await Run(space, seed, 24, 4, store, true);
        var complete = await Run(space, seed, 24, 1, new InMemoryEvolutionCheckpointStore(), false);
        Assert.NotNull(first.Best); Assert.Equal(complete.StateHash, resumed.StateHash);
        Assert.Equal(complete.Best!.Evaluation.GenomeId, resumed.Best!.Evaluation.GenomeId);
    }

    internal static EvolutionVariationContext<EvolutionSearchGenome> Context(EvolutionSearchGenome parent, EvolutionSearchGenome? donor, ulong seed, long generation = 1) =>
        new(Entry(parent), donor is null ? Array.Empty<EvolutionArchiveEntry<EvolutionSearchGenome>>() : new[] { Entry(donor) },
            StableRandom.CreateStream(seed, 0), generation, 0);
    internal static EvolutionArchiveEntry<EvolutionSearchGenome> Entry(EvolutionSearchGenome genome)
    {
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0);
        var candidate = new EvolutionCandidate<EvolutionSearchGenome>(0, new EvolutionCanonicalGenome<EvolutionSearchGenome>(genome, genome.Identity), lineage);
        var evaluation = new EvolutionEvaluation(0, genome.Identity, EvolutionEvaluationStatus.Completed, 0, EvolutionOptimizationDirection.Maximize,
            new Dictionary<string, double> { ["x"] = 0 }, Array.Empty<double>(), Array.Empty<double>(),
            new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage, EvolutionCacheStatus.NotChecked, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");
        return new(new EvolutionCellKey(new[] { 0 }), candidate, evaluation);
    }
    private static Task<EvolutionRunResult<EvolutionSearchGenome>> Run(EvolutionSearchSpace space, EvolutionSearchGenome seed, int budget,
        int workers, IEvolutionCheckpointStore store, bool resume)
    {
        var task = new EvolutionSearchTask(space, "square", "v1", "v1", (genome, _, _) => new ValueTask<EvolutionTaskResult>(
            EvolutionTaskResult.Completed(-genome.Values.Values.Sum(value => value.Number * value.Number),
                new Dictionary<string, double> { ["x"] = genome.Number("x0") }, costUnits: 1)));
        var engine = new EvolutionEngine<EvolutionSearchGenome>(task, new SearchSpaceMutation(space),
            _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", -5, 5, 10) }),
            new EvolutionEngineOptions
            {
                RunId = "typed",
                Seed = 10,
                MaxEvaluationAttempts = budget,
                MaxProposals = budget * 4,
                ProposalBatchSize = 1,
                MaxDegreeOfParallelism = workers,
                Resume = resume,
                MigrationInterval = 0
            },
            checkpointStore: store, genomeCodec: space);
        return engine.RunAsync(new[] { seed });
    }
}
