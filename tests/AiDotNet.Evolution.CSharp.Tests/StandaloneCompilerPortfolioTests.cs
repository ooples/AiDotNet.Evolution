using AiDotNet.Evolution.Programs;
using Xunit;
using static AiDotNet.Evolution.CSharp.Tests.CompilerTestSupport;

namespace AiDotNet.Evolution.CSharp.Tests;

public sealed class StandaloneCompilerPortfolioTests
{
    [Fact]
    public async Task Standalone_engine_commits_compiler_arm_outcomes_and_evaluation_costs_once()
    {
        var ledger = Ledger();
        var options = Options();
        var resources = new ProgramEvolutionResourceOptions(ledger, 2, options.CostUnitVersionHash);
        var arm = CSharpProgramVariation.Create(new ScriptedClient(), Program(), options, resources);
        var portfolio = new ProgramVariationPortfolio(new[] { arm }, new EvolutionOperatorRewardPolicy(
            EvolutionOperatorRewardKind.ParentImprovement, EvolutionOperatorCostBasis.ProposalAndEvaluation, resources.CostUnitVersionHash));
        var gate = new DelegateProgramFitnessEvaluator((_, _, _) => new(EvolutionTaskResult.Completed(1,
            new Dictionary<string, double>(), costUnits: 1)), versionHash: "independent-test-gate-v1");
        var fitness = new DelegateProgramFitnessEvaluator((genome, _, _) => new(EvolutionTaskResult.Completed(
            genome.Source.Contains("return 2", StringComparison.Ordinal) ? 2 : 1,
            new Dictionary<string, double> { ["x"] = .5 }, costUnits: 1)), versionHash: "test-fitness-v1");
        var engine = ProgramEvolution.CreateEngine(portfolio, gate, fitness, resources,
            new EvolutionEngineOptions
            {
                EnableEvaluationCache = false,
                MaxProposals = 2,
                MaxEvaluationAttempts = 2,
                MaxGenerations = 1,
                ProposalBatchSize = 1,
                MaxDegreeOfParallelism = 1
            },
            _ => new MapElitesArchive<ProgramGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 1, 1) }));
        await engine.RunAsync(new[] { Parent() });
        Assert.Equal(1, portfolio.GetUsage().ChatCalls);
        Assert.Equal(1, Assert.Single(portfolio.Statistics).Outcomes);
        Assert.Equal(5.382m, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(2, ledger.Snapshot().Receipts.Count(receipt => receipt.Stage == EvolutionResourceStage.Evaluation));
        Assert.Throws<InvalidOperationException>(() => portfolio.GetProposalCost(1));
    }

    [Fact]
    public async Task Public_factory_compiles_and_repairs_with_exact_shared_portfolio_costs()
    {
        var ledger = Ledger();
        var options = Options();
        var resources = new ProgramEvolutionResourceOptions(ledger, 1, options.CostUnitVersionHash);
        var client = new ScriptedClient { Handler = (call, messages) => ScriptedClient.Response(Reply(messages, call == 1 ? "MISSING" : "2")) };
        var arm = CSharpProgramVariation.Create(client, Program(), options, resources);
        var portfolio = new ProgramVariationPortfolio(new[] { arm }, new EvolutionOperatorRewardPolicy(
            EvolutionOperatorRewardKind.ParentImprovement, EvolutionOperatorCostBasis.ProposalAndEvaluation, resources.CostUnitVersionHash));
        var child = await portfolio.ProposeAsync(Context());
        Assert.Contains("return 2", child.Source);
        Assert.Equal(2, portfolio.GetUsage().ChatCalls);
        Assert.Equal(1, portfolio.GetUsage().Retries);
        Assert.Equal(2.654m, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(2.554m, portfolio.GetProposalCost(1).Charged["cost_units"]);
        Assert.Same(ledger, portfolio.Ledger);
        Assert.DoesNotContain(typeof(CSharpProgramVariation).Assembly.GetReferencedAssemblies(), name => name.Name == "AiDotNet");
    }

    [Fact]
    public void Invalid_factory_configuration_fails_before_setup_or_model_dispatch()
    {
        var ledger = Ledger();
        var options = Options();
        var client = new ScriptedClient();
        Assert.Throws<ArgumentException>(() => CSharpProgramVariation.Create(client, Program(), options,
            new ProgramEvolutionResourceOptions(ledger, 1, "wrong")));
        var program = Program();
        program.Language = ProgramLanguage.Python;
        Assert.Throws<ArgumentException>(() => CSharpProgramVariation.Create(client, program, options,
            new ProgramEvolutionResourceOptions(ledger, 1, options.CostUnitVersionHash)));
        Assert.Equal(0, ledger.Snapshot().Admitted);
        Assert.Empty(client.Conversations);
        Assert.False(Directory.Exists(options.AuditDirectory));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void Negative_usage_cannot_reduce_charges(long input, long output) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompilerChatUsage(input, output));
}
