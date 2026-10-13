using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.Ptx.Tests;

public sealed class PtxFitnessTests
{
    private static (PtxKernelFitnessEvaluator Evaluator, FakeWorkerTransport Worker) Evaluator()
    {
        var worker = new FakeWorkerTransport();
        var compiler = new PtxProgramCompiler(Axpy.Contract(), worker);
        var fitness = new PtxKernelFitnessEvaluator(new PtxCorrectnessEvaluator(compiler, Axpy.Reference, null),
            new PtxTimingEvaluator(compiler, new PtxTimingOptions { MachineLockName = null }), Axpy.Source);
        return (fitness, worker);
    }

    private static EvolutionEvaluationContext Context() => new(1, 7UL, 1UL, 1);

    [Fact]
    public async Task A_correct_faster_kernel_scores_its_speedup_with_resource_descriptors()
    {
        var (fitness, worker) = Evaluator();
        EvolutionTaskResult result = await fitness.EvaluateAsync(new ProgramGenome(Axpy.Block128), Context());
        Assert.Equal(EvolutionEvaluationStatus.Completed, result.Status);
        Assert.InRange(result.Quality ?? 0, 1.2, 1.3);
        Assert.Equal(12, result.Descriptors["registers"]);
        Assert.Equal(1, result.Descriptors["qualifies"]);
        Assert.Equal(0, result.Descriptors["max_abs_error"]);
        Assert.Contains(worker.Requests, r => r.Operation == PtxWorkerOperation.Time);
        Assert.StartsWith("ptx-kernel-fitness", fitness.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_kernel_fails_without_being_timed()
    {
        var (fitness, worker) = Evaluator();
        EvolutionTaskResult result = await fitness.EvaluateAsync(
            new ProgramGenome(Axpy.Source + "\n" + FakeWorkerTransport.FaultMarker + SimulatedFault.WrongValue + "\n"), Context());
        Assert.Equal(EvolutionEvaluationStatus.Failed, result.Status);
        Assert.Equal("ptx_mismatch", Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain(worker.Requests, r => r.Operation == PtxWorkerOperation.Time);
    }

    [Fact]
    public async Task A_missing_device_stops_the_run_instead_of_failing_every_candidate()
    {
        var (fitness, worker) = Evaluator();
        worker.Status = "no-device";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fitness.EvaluateAsync(new ProgramGenome(Axpy.Source), Context()).AsTask());
    }
}