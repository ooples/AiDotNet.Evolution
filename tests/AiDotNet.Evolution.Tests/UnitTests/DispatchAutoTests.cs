using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>V1-20: Dispatch=Auto resolves once, from declared latency, to Pipeline or Batch.</summary>
public sealed class DispatchAutoTests
{
    private sealed class LatencyVariation : IVariationOperator<TestGenome>, IEvolutionLatencyProfile
    {
        private readonly IncrementVariation _inner = new();
        public LatencyVariation(bool latencyBound) => IsLatencyBound = latencyBound;
        public bool IsLatencyBound { get; }
        public string Id => _inner.Id;
        public string VersionHash => _inner.VersionHash;
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            _inner.ProposeAsync(context, cancellationToken);
    }

    private static EvolutionEngineOptions Options(EvolutionDispatchMode dispatch) => new()
    {
        RunId = "dispatch-auto",
        Seed = 9,
        Dispatch = dispatch,
        MaxProposals = 24,
        MaxEvaluationAttempts = 24,
        MaxGenerations = 24,
        MaxDegreeOfParallelism = 2,
        IslandCount = 1,
        CheckpointInterval = 0,
        MigrationInterval = 0
    };

    private static EvolutionEngine<TestGenome> Engine(IVariationOperator<TestGenome> variation, EvolutionDispatchMode dispatch) =>
        new(new SyntheticEvolutionTask(), variation,
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 100) }), Options(dispatch));

    private static Task<EvolutionRunResult<TestGenome>> Run(EvolutionEngine<TestGenome> engine) =>
        engine.RunAsync(new[] { new TestGenome(1), new TestGenome(2) });

    [Theory]
    [InlineData(false, EvolutionDispatchMode.Batch)]
    [InlineData(true, EvolutionDispatchMode.Pipeline)]
    public async Task Auto_behaves_exactly_like_the_mode_it_resolves_to(bool latencyBound, EvolutionDispatchMode expected)
    {
        var auto = Engine(new LatencyVariation(latencyBound), EvolutionDispatchMode.Auto);
        var explicitMode = Engine(new LatencyVariation(latencyBound), expected);

        Assert.Equal(explicitMode.CompatibilityHash, auto.CompatibilityHash); // resume sees the resolved mode
        EvolutionRunResult<TestGenome> autoResult = await Run(auto);
        EvolutionRunResult<TestGenome> explicitResult = await Run(explicitMode);
        Assert.Equal(explicitResult.StateHash, autoResult.StateHash);
        Assert.Equal(expected == EvolutionDispatchMode.Pipeline, auto.PipelineReport is not null);
    }

    [Fact]
    public void Undeclared_components_resolve_to_batch_and_a_metered_wrapper_forwards_the_declaration()
    {
        Assert.Equal(Engine(new IncrementVariation(), EvolutionDispatchMode.Batch).CompatibilityHash,
            Engine(new IncrementVariation(), EvolutionDispatchMode.Auto).CompatibilityHash);

        var ledger = new EvolutionResourceLedger("forwarding", EvolutionResources.Of("cost_units", 10));
        var metered = new ResourceMeteredEvolutionTask<TestGenome>(new DeclaredTask(), ledger, new[] { 1m });
        Assert.False(new ResourceMeteredEvolutionTask<TestGenome>(new SyntheticEvolutionTask(), ledger, new[] { 1m }).IsLatencyBound);
        Assert.True(metered.IsLatencyBound);
    }

    private sealed class DeclaredTask : IEvolutionTask<TestGenome>, IEvolutionLatencyProfile
    {
        private readonly SyntheticEvolutionTask _inner = new();
        public bool IsLatencyBound => true;
        public string Id => _inner.Id;
        public string VersionHash => _inner.VersionHash;
        public string EvaluatorVersionHash => _inner.EvaluatorVersionHash;
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            _inner.CanonicalizeAsync(genome, cancellationToken);
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default) => _inner.EvaluateAsync(candidate, context, cancellationToken);
    }
}
