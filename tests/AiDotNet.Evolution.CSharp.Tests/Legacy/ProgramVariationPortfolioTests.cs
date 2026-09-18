using System.Globalization;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests;

public sealed class ProgramVariationPortfolioTests
{
    private const string Units = "all-instrumented-stages-v1";
    private static EvolutionOperatorRewardPolicy Policy(EvolutionOperatorCostBasis basis = EvolutionOperatorCostBasis.ProposalAndEvaluation) =>
        new(EvolutionOperatorRewardKind.ParentImprovement, basis, Units);
    private static EvolutionResourceLedger Resources() => new("portfolio-test", EvolutionResources.Of("cost_units", 1000));
    private static ProgramVariationPortfolio Create(EvolutionResourceLedger ledger, string version = "v1") =>
        new(new[] { "mutation", "crossover", "restart", "local-refinement", "consumer-llm" }.Select(name =>
            new MeteredProgramVariationOperator(new Backend(name, version), ledger, EvolutionResources.Of("cost_units", 5), Units)), Policy(), 0.2);

    [Fact]
    public async Task Five_consumer_strategies_keep_credit_usage_and_pending_checkpoint_state()
    {
        var ledger = Resources(); var portfolio = Create(ledger);
        for (long generation = 1; generation <= 5; generation++) await portfolio.ProposeAsync(Context(generation: generation));
        var restoredLedger = Resources(); restoredLedger.RestoreState(ledger.CaptureState());
        var restored = Create(restoredLedger); restored.RestoreState(portfolio.CaptureState());
        Assert.Equal(portfolio.CaptureState(), restored.CaptureState());
        foreach (var subject in new[] { portfolio, restored })
            for (long generation = 5; generation >= 1; generation--) subject.Observe(Evaluation(generation), EvolutionArchiveInsertionResult.Replaced);
        Assert.Equal(portfolio.CaptureState(), restored.CaptureState());
        Assert.Equal(5, portfolio.GetUsage().Proposals);
        Assert.Equal(1, portfolio.GetUsage().ChatCalls);
        Assert.Equal(2, portfolio.GetUsage().InputTokens);
        Assert.Equal(0, portfolio.CreditNotificationFailures);
        Assert.All(portfolio.Statistics, arm => Assert.Equal(1, arm.Outcomes));
        Assert.NotNull(portfolio.LastCredit);
        for (long generation = 6; generation <= 20; generation++)
        {
            var first = await portfolio.ProposeAsync(Context(generation: generation));
            var second = await restored.ProposeAsync(Context(generation: generation));
            Assert.Equal(first.Id, second.Id);
            portfolio.Observe(Evaluation(generation), EvolutionArchiveInsertionResult.Replaced);
            restored.Observe(Evaluation(generation), EvolutionArchiveInsertionResult.Replaced);
        }
        Assert.Equal(portfolio.CaptureState(), restored.CaptureState());
        Assert.Equal(ledger.CaptureState(), restoredLedger.CaptureState());
        Assert.Throws<InvalidDataException>(() => Create(Resources(), "changed").RestoreState(portfolio.CaptureState()));
    }

    [Fact]
    public void Missing_costs_units_duplicate_children_and_evaluation_only_credit_are_rejected()
    {
        var backend = new MeteredProgramVariationOperator(new Backend("a", "v1"), Resources(), EvolutionResources.Of("cost_units", 5), Units);
        Assert.Throws<ArgumentException>(() => new ProgramVariationPortfolio(new[] { backend, backend }, Policy()));
        Assert.Throws<ArgumentException>(() => new ProgramVariationPortfolio(new[] { backend }, Policy(EvolutionOperatorCostBasis.Evaluation)));
        Assert.Throws<ArgumentException>(() => new ProgramVariationPortfolio(new[] { backend },
            new(EvolutionOperatorRewardKind.ParentImprovement, EvolutionOperatorCostBasis.ProposalAndEvaluation, "wrong")));
        Assert.Throws<ArgumentNullException>(() => new ProgramVariationPortfolio(null!, Policy()));
        Assert.Throws<ArgumentNullException>(() => new ProgramVariationPortfolio(new[] { backend }, null!));
        Assert.Throws<ArgumentException>(() => new ProgramVariationPortfolio(Array.Empty<IProgramVariationOperator>(), Policy()));
        Assert.Throws<ArgumentException>(() => new ProgramVariationPortfolio(new IProgramVariationOperator[] { new Unmetered() }, Policy()));
        var other = new MeteredProgramVariationOperator(new Backend("b", "v1"), Resources(), EvolutionResources.Of("cost_units", 5), Units);
        Assert.Throws<ArgumentException>(() => new ProgramVariationPortfolio(new[] { backend, other }, Policy()));
    }

    [Fact]
    public void Standalone_entry_point_rejects_an_independent_or_missing_evaluation_ledger()
    {
        var ledger = Resources();
        var evaluator = new DelegateProgramFitnessEvaluator((_, _, _) => new(EvolutionTaskResult.Completed(
            1, new Dictionary<string, double> { ["x"] = 0.5 }, costUnits: 1)), versionHash: "test-v1");
        EvolutionEngine<ProgramGenome> CreateEngine(EvolutionResourceLedger selected, string units = Units, bool resume = false) => ProgramEvolution.CreateEngine(
            Create(ledger), evaluator, evaluator, new ProgramEvolutionResourceOptions(selected, 2, units), new EvolutionEngineOptions { Resume = resume, EnableEvaluationCache = false },
            _ => new MapElitesArchive<ProgramGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 1, 1) }));
        Assert.Throws<ArgumentNullException>(() => CreateEngine(null!));
        Assert.Throws<ArgumentException>(() => CreateEngine(Resources()));
        Assert.Throws<ArgumentException>(() => CreateEngine(ledger, "different-units"));
        Assert.Throws<ArgumentException>(() => CreateEngine(ledger, resume: true));
        Assert.NotNull(CreateEngine(ledger));
    }

    private static EvolutionEvaluation Evaluation(long generation) => new(generation, "g" + generation,
        EvolutionEvaluationStatus.Completed, 1, EvolutionOptimizationDirection.Maximize,
        new Dictionary<string, double> { ["x"] = 0.5 }, Array.Empty<double>(), Array.Empty<double>(),
        new EvolutionEvaluationCost(TimeSpan.Zero, 1, 2), new EvolutionLineage(null, null, "adaptive-variation", null, generation, 0, 7UL),
        EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");

    [Fact]
    public async Task Standalone_engine_rejects_wrong_programs_before_fitness_and_charges_both_paths()
    {
        int fitnessCalls = 0;
        var ledger = Resources();
        var initial = Parent();
        var correctness = new DelegateProgramFitnessEvaluator((g, _, _) => new(EvolutionTaskResult.Completed(
            g.Id == initial.Id ? 1 : 0, new Dictionary<string, double> { ["x"] = 0.5 }, costUnits: 1)), versionHash: "correct-v1");
        var fitness = new DelegateProgramFitnessEvaluator((_, _, _) =>
        {
            fitnessCalls++;
            return new(EvolutionTaskResult.Completed(0.5, new Dictionary<string, double> { ["x"] = 0.5 }, costUnits: 1));
        }, versionHash: "fitness-v1");
        var archive = new MapElitesArchive<ProgramGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 1, 1) });
        var engine = ProgramEvolution.CreateEngine(Create(ledger), correctness, fitness,
            new ProgramEvolutionResourceOptions(ledger, 2, Units),
            new EvolutionEngineOptions { MaxEvaluationAttempts = 3, MaxProposals = 5, ProposalBatchSize = 1, EnableEvaluationCache = false },
            _ => archive);
        await engine.RunAsync(new[] { initial });
        Assert.Equal(1, fitnessCalls);
        Assert.Equal(initial.Id, archive.Best!.Candidate.CanonicalGenome.Genome.Id);
        Assert.Equal(10, ledger.Snapshot().Spent["cost_units"]);
    }

    private static ProgramGenome Parent(string source = "public static class C { public static int F() => 1; }") => new(source, ProgramLanguage.CSharp);

    private static EvolutionVariationContext<ProgramGenome> Context(long generation)
    {
        var parent = Parent();
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL);
        var candidate = new EvolutionCandidate<ProgramGenome>(0, new(parent, parent.Id), lineage);
        var evaluation = new EvolutionEvaluation(0, parent.Id, EvolutionEvaluationStatus.Completed, 0.5,
            EvolutionOptimizationDirection.Maximize, new Dictionary<string, double> { ["x"] = 0.5 },
            Array.Empty<double>(), Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage,
            EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task-v1", "evaluator-v1", "config-v1");
        return new(new EvolutionArchiveEntry<ProgramGenome>(new EvolutionCellKey(new[] { 0 }), candidate, evaluation),
            Array.Empty<EvolutionArchiveEntry<ProgramGenome>>(), new StableRandom(1234UL, 7UL), generation, 0);
    }

    private sealed class Unmetered : IProgramVariationOperator
    {
        public string Id => "unmetered";
        public string VersionHash => "v1";
        public ProgramEvolutionLlmUsage GetUsage() => new();
        public ValueTask<ProgramGenome> ProposeAsync(EvolutionVariationContext<ProgramGenome> context, CancellationToken cancellationToken = default) => new(Parent());
    }

    private sealed class Backend(string name, string version) : ICostedProgramProposalSource
    {
        private long _proposals, _outcomes;
        public string Id => name;
        public string VersionHash => version;
        public ValueTask<EvolutionResourceResult<ProgramGenome>> ProposeAsync(EvolutionVariationContext<ProgramGenome> context, CancellationToken cancellationToken = default)
        {
            _proposals++;
            return new(new EvolutionResourceResult<ProgramGenome>(Parent("public static class C { public static int F() { return " +
                context.Random.NextInt(100).ToString(CultureInfo.InvariantCulture) + "; } }"), EvolutionResources.Of("cost_units", 3)));
        }
        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult) => _outcomes++;
        public ProgramEvolutionLlmUsage GetUsage() => new(_proposals, name == "consumer-llm" ? _proposals : 0,
            inputTokens: name == "consumer-llm" ? 2 * _proposals : 0);
        public string CaptureState() => _proposals.ToString(CultureInfo.InvariantCulture) + ":" + _outcomes.ToString(CultureInfo.InvariantCulture);
        public void RestoreState(string state)
        {
            var parts = state.Split(':');
            _proposals = long.Parse(parts[0], CultureInfo.InvariantCulture); _outcomes = long.Parse(parts[1], CultureInfo.InvariantCulture);
        }
    }
}
