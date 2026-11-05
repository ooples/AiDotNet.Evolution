using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionEngineReachabilityTests
{
    [Fact]
    public async Task RatioSelectionConfiguredThroughOptionsExercisesTheExploitationBranch()
    {
        var ratioVariation = new RecordingParentVariation();
        EvolutionRunResult<TestGenome> ratioResult =
            await Engine(RatioOptions(), ratioVariation).RunAsync(Seeds());
        IReadOnlyList<int> ratioParents = ratioVariation.Parents;

        EvolutionEngineOptions uniformOptions = RatioOptions();
        uniformOptions.SelectionPolicy = EvolutionSelectionPolicyKind.Uniform;
        var uniformVariation = new RecordingParentVariation();
        await Engine(uniformOptions, uniformVariation).RunAsync(Seeds());
        IReadOnlyList<int> uniformParents = uniformVariation.Parents;

        Assert.NotEmpty(ratioParents);
        Assert.Equal(30, ratioParents[0]);
        Assert.Equal(ratioParents.OrderBy(value => value).ToArray(), ratioParents.ToArray());
        Assert.Equal(ratioParents.Count, ratioParents.Distinct().Count());
        Assert.NotEmpty(ratioResult.GlobalElites);
        Assert.NotEqual(ratioParents.ToArray(), uniformParents.ToArray());
        Assert.Contains(uniformParents, value => value < 30);
    }

    [Theory]
    [InlineData(EvolutionExploitationSource.GlobalTopK)]
    [InlineData(EvolutionExploitationSource.IslandPreferredTopK)]
    public async Task AnIndexBackedExploitationSourceGetsAnIndexAtTheDefaultCountAndResumesIt(EvolutionExploitationSource source)
    {
        EvolutionEngineOptions options = RatioOptions();
        options.Selection.ExploitationSource = source;
        options.Selection.ExploitationEliteCount = 3;
        options.CheckpointInterval = 1;
        Assert.Equal(0, options.GlobalEliteCount);
        var store = new InMemoryEvolutionCheckpointStore();

        EvolutionRunResult<TestGenome> first = await new EvolutionEngine<TestGenome>(new SyntheticEvolutionTask(),
            new RecordingParentVariation(), _ => Archive(), options, checkpointStore: store, genomeCodec: new TestGenomeCodec())
            .RunAsync(Seeds());
        // The index holds the exploitation pool, so the configured source is the one that draws the parent.
        Assert.Equal(3, first.GlobalElites.Count);

        async Task<EvolutionRunResult<TestGenome>> Resume(int attempts)
        {
            EvolutionEngineOptions resume = RatioOptions();
            resume.Selection.ExploitationSource = source;
            resume.Selection.ExploitationEliteCount = 3;
            resume.CheckpointInterval = 1;
            resume.MaxEvaluationAttempts = attempts;
            resume.Resume = true;
            return await new EvolutionEngine<TestGenome>(new SyntheticEvolutionTask(), new RecordingParentVariation(), _ => Archive(),
                resume, checkpointStore: store, genomeCodec: new TestGenomeCodec()).RunAsync(Seeds());
        }

        // With no budget left the resumed run evaluates nothing, so its index is exactly what the checkpoint restored.
        EvolutionRunResult<TestGenome> restored = await Resume(12);
        Assert.Equal(first.GlobalElites.Select(record => (record.Island, record.Entry.Evaluation.GenomeId)),
            restored.GlobalElites.Select(record => (record.Island, record.Entry.Evaluation.GenomeId)));
        Assert.Equal(first.StateHash, restored.StateHash);

        Assert.Equal(3, (await Resume(20)).GlobalElites.Count);
    }

    [Theory]
    [InlineData(EvolutionEliteBranch.UniformPopulation, true)]
    [InlineData(EvolutionEliteBranch.UniformGlobal, false)]
    [InlineData(EvolutionEliteBranch.UniformIsland, false)]
    public async Task OnlyThePopulationBranchParentsFromAnotherIslandWithoutAnIndexOrMigration(
        EvolutionEliteBranch branch, bool crosses)
    {
        EvolutionEngineOptions options = RatioOptions();
        options.IslandCount = 2;
        options.MaxEvaluationAttempts = 24;
        options.Selection.ExploitationRatio = 0;
        options.Selection.EliteRatio = 1;
        options.Selection.ExploitationSource = EvolutionExploitationSource.IslandTopK;
        options.Selection.EliteBranch = branch;
        Assert.Equal((0, 0), (options.GlobalEliteCount, options.MigrationInterval));
        var variation = new RecordingIslandParentVariation();

        // Seeds 10 and 20 land on island 0 and seed 80 on island 1. Each child is its parent plus one, so within this
        // budget island 0 holds values below 80 and island 1 holds 80 and above.
        await new EvolutionEngine<TestGenome>(new SyntheticEvolutionTask(), variation, _ => Archive(), options)
            .RunAsync(new[] { new TestGenome(10), new TestGenome(80), new TestGenome(20) });

        // A child that repeats an evaluated genome costs no attempt, so a run may make more proposals than its budget;
        // fewer than 60 keeps island 0 below 80.
        Assert.InRange(variation.Draws.Count, 21, 59);
        Assert.Contains(variation.Draws, draw => draw.Island == 1);
        Assert.Equal(crosses, variation.Draws.Any(draw => draw.Island == 0 && draw.Parent >= 80));
        Assert.Equal(crosses, variation.Draws.Any(draw => draw.Island == 1 && draw.Parent < 80));
    }

    [Fact]
    public async Task ThePopulationIsSuppliedForEachSelectionAndWithdrawnAfterIt()
    {
        EvolutionEngineOptions options = RatioOptions();
        options.IslandCount = 2;
        var selection = new PopulationRecordingSelection(throwOnCall: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Engine(options, new RecordingParentVariation(), selection).RunAsync(Seeds()));

        // Two archives before each draw and none after it, including the draw that threw.
        Assert.Equal(new[] { 2, 0, 2, 0, 2, 0 }, selection.Supplied);
        Assert.Equal(new[] { 2, 2, 2 }, selection.SeenAtSelect);
    }

    [Fact]
    public async Task AnExplicitlySuppliedSelectionPolicyWinsOverTheOptions()
    {
        EvolutionEngineOptions uniformOptions = RatioOptions();
        uniformOptions.SelectionPolicy = EvolutionSelectionPolicyKind.Uniform;
        var uniformVariation = new RecordingParentVariation();
        await Engine(uniformOptions, uniformVariation).RunAsync(Seeds());

        var overriddenVariation = new RecordingParentVariation();
        await Engine(RatioOptions(), overriddenVariation,
            new UniformEvolutionSelectionPolicy<TestGenome>()).RunAsync(Seeds());

        Assert.Equal(uniformVariation.Parents.ToArray(), overriddenVariation.Parents.ToArray());
        Assert.Equal(
            Engine(uniformOptions, new RecordingParentVariation()).CompatibilityHash,
            Engine(RatioOptions(), new RecordingParentVariation(),
                new UniformEvolutionSelectionPolicy<TestGenome>()).CompatibilityHash);
        Assert.NotEqual(
            Engine(uniformOptions, new RecordingParentVariation()).CompatibilityHash,
            Engine(RatioOptions(), new RecordingParentVariation()).CompatibilityHash);
    }

    [Fact]
    public void OpenEvolveDefaultsMatchTheDocumentedUpstreamNumbers()
    {
        EvolutionEngineOptions options = EvolutionEngineOptions.CreateOpenEvolveDefaults();

        Assert.Equal(42UL, options.Seed);
        Assert.Equal(10_000, options.MaxEvaluationAttempts);
        Assert.Equal(10_000, options.MaxProposals);
        Assert.Equal(10_000, options.MaxGenerations);
        Assert.Equal(100, options.CheckpointInterval);
        Assert.Equal(3, options.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(300), options.EvaluationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), options.RetryBaseDelay);
        Assert.Equal(1.0, options.RetryBackoffMultiplier);
        Assert.Equal(EvolutionRetryStatuses.Failed, options.RetryOn);
        Assert.Equal(5, options.IslandCount);
        Assert.Equal(50, options.MigrationInterval);
        Assert.Equal(EvolutionMigrationTrigger.IslandGenerations, options.MigrationTrigger);
        Assert.Equal(100, options.GlobalEliteCount);
        Assert.Equal(1_000, options.HistorySize);
        Assert.True(options.Artifacts.Enabled);
        Assert.Equal(1, options.ProposalBatchSize);
        Assert.Equal(1, options.MaxDegreeOfParallelism);
        Assert.Equal(5, options.InspirationCount);
        Assert.Equal(EvolutionSelectionPolicyKind.Ratio, options.SelectionPolicy);
        Assert.Equal(0.2, options.Selection.ExplorationRatio);
        Assert.Equal(0.7, options.Selection.ExploitationRatio);
        Assert.Equal(0.1, options.Selection.EliteRatio);
        Assert.Equal(3, options.Selection.TopInspirationCount);
        Assert.Equal(2, options.Selection.DiverseInspirationCount);
        Assert.Equal(1e-3, options.EarlyStopping.MinimumImprovement);
    }

    [Fact]
    public void OpenEvolveDefaultsLeaveTheClassDefaultsAloneAndStayValid()
    {
        var untouched = new EvolutionEngineOptions();

        Assert.Equal(1, untouched.IslandCount);
        Assert.Equal(0, untouched.MaxRetries);
        Assert.Null(untouched.EvaluationTimeout);
        Assert.Equal(0, untouched.GlobalEliteCount);
        Assert.Equal(0, untouched.HistorySize);
        Assert.False(untouched.Artifacts.Enabled);
        Assert.Equal(EvolutionSelectionPolicyKind.Uniform, untouched.SelectionPolicy);
        Assert.False(EvolutionEngineOptions.CreateOpenEvolveDefaults().Cascade.Enabled);

        EvolutionEngineOptions upstream = EvolutionEngineOptions.CreateOpenEvolveDefaults();
        upstream.MaxEvaluationAttempts = 4;
        upstream.MaxProposals = 4;
        upstream.MaxGenerations = 4;
        upstream.EvaluationTimeout = TimeSpan.FromSeconds(5);
        Exception? exception = Record.Exception(() => new EvolutionEngine<TestGenome>(
            new SyntheticEvolutionTask(), new RecordingParentVariation(), _ => Archive(), upstream));

        Assert.Null(exception);
    }

    private static EvolutionEngine<TestGenome> Engine(EvolutionEngineOptions options,
        RecordingParentVariation variation, ISelectionPolicy<TestGenome>? selection = null) =>
        new(new SyntheticEvolutionTask(), variation, _ => Archive(), options, selection: selection);

    private static MapElitesArchive<TestGenome> Archive() => new(new[]
    {
        new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Clamp)
    });

    private static TestGenome[] Seeds() => new[] { new TestGenome(10), new TestGenome(20), new TestGenome(30) };

    private static EvolutionEngineOptions RatioOptions() => new()
    {
        RunId = "selection-wiring",
        Seed = 77,
        MaxEvaluationAttempts = 12,
        MaxProposals = 100,
        MaxGenerations = 100,
        ProposalBatchSize = 1,
        MaxDegreeOfParallelism = 1,
        IslandCount = 1,
        MigrationInterval = 0,
        CheckpointInterval = 0,
        InspirationCount = 0,
        SelectionPolicy = EvolutionSelectionPolicyKind.Ratio,
        Selection = new EvolutionSelectionOptions
        {
            ExplorationRatio = 0,
            ExploitationRatio = 1,
            EliteRatio = 0,
            ExploitationEliteCount = 1,
            ExploitationSource = EvolutionExploitationSource.GlobalTopK,
            TopInspirationCount = 0,
            DiverseInspirationCount = 0,
            IncludeIslandBest = false
        }
    };
}

internal sealed class PopulationRecordingSelection : IPopulationAwareEvolutionSelectionPolicy<TestGenome>
{
    private readonly int _throwOnCall;
    private readonly UniformEvolutionSelectionPolicy<TestGenome> _inner = new();
    private int _current;

    public PopulationRecordingSelection(int throwOnCall) => _throwOnCall = throwOnCall;

    public List<int> Supplied { get; } = new();

    public List<int> SeenAtSelect { get; } = new();

    public string Id => "population-recording";

    public string VersionHash => "population-recording-v1";

    public void UsePopulation(IReadOnlyList<IEvolutionArchive<TestGenome>> islands)
    {
        _current = islands.Count;
        Supplied.Add(islands.Count);
    }

    public EvolutionSelection<TestGenome>? Select(IEvolutionArchive<TestGenome> archive, StableRandom random, int inspirationCount)
    {
        SeenAtSelect.Add(_current);
        if (SeenAtSelect.Count == _throwOnCall) throw new InvalidOperationException("selection failed");
        return _inner.Select(archive, random, inspirationCount);
    }
}

internal sealed class RecordingIslandParentVariation : IVariationOperator<TestGenome>
{
    private readonly List<(int Island, int Parent)> _draws = new();

    public string Id => "recording-island-parent";

    public string VersionHash => "recording-island-parent-v1";

    public IReadOnlyList<(int Island, int Parent)> Draws
    {
        get { lock (_draws) return _draws.ToArray(); }
    }

    public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int parent = context.Parent.Candidate.CanonicalGenome.Genome.Value;
        lock (_draws) _draws.Add((context.Island, parent));
        return new ValueTask<TestGenome>(new TestGenome(parent + 1));
    }
}

internal sealed class RecordingParentVariation : IVariationOperator<TestGenome>
{
    private readonly List<int> _parents = new();

    public string Id => "recording-parent";

    public string VersionHash => "recording-parent-v1";

    public IReadOnlyList<int> Parents
    {
        get { lock (_parents) return _parents.ToArray(); }
    }

    public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int parent = context.Parent.Candidate.CanonicalGenome.Genome.Value;
        lock (_parents) _parents.Add(parent);
        return new ValueTask<TestGenome>(new TestGenome(parent + 1));
    }
}
