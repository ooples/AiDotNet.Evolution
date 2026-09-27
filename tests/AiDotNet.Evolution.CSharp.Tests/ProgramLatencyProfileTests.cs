using AiDotNet.Evolution.CSharp.Tests.ModelRuntime;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests;

/// <summary>V1-20: program components declare latency so Dispatch=Auto picks Pipeline for model calls and processes.</summary>
public sealed class ProgramLatencyProfileTests
{
    [Fact]
    public void Model_proposals_are_latency_bound_and_the_task_forwards_its_evaluator()
    {
        Assert.True(new LlmProgramVariationOperator(new FakeChatClient("x")).IsLatencyBound);

        // An in-process delegate evaluator declares nothing, so the task is not latency-bound and Auto resolves to Batch.
        Assert.False(new ProgramEvolutionTask(new DelegateProgramFitnessEvaluator(genome => genome.LineCount)).IsLatencyBound);
    }
}
