using Xunit;

namespace AiDotNet.Evolution.Ptx.Tests;

public sealed class PtxCorrectnessTests
{
    private static string Simulate(SimulatedFault fault) => Axpy.Source + "\n" + FakeWorkerTransport.FaultMarker + fault + "\n";

    private static (PtxCorrectnessEvaluator Evaluator, FakeWorkerTransport Worker) Evaluator(bool cpuReference = true)
    {
        var worker = new FakeWorkerTransport();
        var compiler = new PtxProgramCompiler(Axpy.Contract(), worker);
        return (new PtxCorrectnessEvaluator(compiler, cpuReference ? Axpy.Reference : null, cpuReference ? null : Axpy.Source), worker);
    }

    [Fact]
    public void A_correct_kernel_passes_every_fixed_and_fuzzed_shape_against_the_cpu_reference()
    {
        var (evaluator, worker) = Evaluator();
        PtxCorrectnessReport report = evaluator.Evaluate(Axpy.Source);
        Assert.True(report.Passed, report.Message);
        Assert.Equal(Axpy.Contract().GetValidationCases().Count, report.Cases.Count);
        Assert.All(report.Cases, c => Assert.Equal(0, c.Mismatches));
        Assert.Equal(0, report.MaxAbsoluteError);
        Assert.Equal("axpy-cpu-v1", report.ReferenceIdentity);
        Assert.True(report.Cases[0].Shape.Symbols["N"] <= report.Cases[^1].Shape.Symbols["N"], "Smallest shapes run first.");
        PtxWorkerRequest validate = worker.Requests.Single(r => r.Operation == PtxWorkerOperation.Validate);
        Assert.Single(validate.Kernels);
        Assert.All(validate.Cases, c => Assert.Equal(c.Buffers[0].Elements, c.Buffers[2].Elements));
    }

    [Theory]
    [InlineData(SimulatedFault.WrongValue, PtxCorrectnessVerdict.Mismatch, "out[0]")]
    [InlineData(SimulatedFault.SkipLastElement, PtxCorrectnessVerdict.Mismatch, "candidate NaN")]
    [InlineData(SimulatedFault.WriteOutOfBounds, PtxCorrectnessVerdict.OutOfBoundsWrite, "outside buffer(s) out")]
    [InlineData(SimulatedFault.LaunchFault, PtxCorrectnessVerdict.LaunchFailed, "CUDA_ERROR_ILLEGAL_ADDRESS")]
    public void A_wrong_kernel_is_rejected_before_it_could_be_timed(SimulatedFault fault, PtxCorrectnessVerdict verdict, string evidence)
    {
        var (evaluator, worker) = Evaluator();
        PtxCorrectnessReport report = evaluator.Evaluate(Simulate(fault));
        Assert.False(report.Passed);
        Assert.Equal(verdict, report.Verdict);
        Assert.Contains(evidence, report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(worker.Requests, r => r.Operation == PtxWorkerOperation.Time);
    }

    [Fact]
    public void Unwritten_outputs_are_caught_even_when_the_sentinel_happens_to_match_the_reference()
    {
        // The second run starts from zero bytes, so the first run's NaN sentinel is not the only witness: the skipped
        // element is compared on both runs and fails on whichever sentinel differs from the reference.
        var (evaluator, _) = Evaluator();
        PtxCorrectnessReport report = evaluator.Evaluate(Simulate(SimulatedFault.SkipLastElement));
        PtxCaseOutcome failed = report.Cases[^1];
        Assert.False(failed.Passed);
        Assert.True(failed.Mismatches >= 1);
        Assert.Equal(2 * failed.Shape.Symbols["N"], failed.Compared);
    }

    [Fact]
    public void Against_the_incumbent_the_candidate_runs_beside_it_in_the_same_worker()
    {
        var (evaluator, worker) = Evaluator(cpuReference: false);
        Assert.True(evaluator.Evaluate(Axpy.Block128).Passed);
        PtxWorkerRequest validate = worker.Requests.Last(r => r.Operation == PtxWorkerOperation.Validate);
        Assert.Equal(2, validate.Kernels.Count);
        Assert.All(validate.Cases, c => Assert.Equal(new uint[] { 128, 256 }, c.Launches.Select(l => l.BlockX)));
        Assert.Equal(PtxCorrectnessVerdict.Mismatch, evaluator.Evaluate(Simulate(SimulatedFault.WrongValue)).Verdict);
        Assert.StartsWith("incumbent:", evaluator.ReferenceIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_failures_and_missing_workers_are_distinct_verdicts()
    {
        var (evaluator, worker) = Evaluator();
        Assert.Equal(PtxCorrectnessVerdict.CompileFailed, evaluator.Evaluate(Axpy.Source.Replace(".version 6.4", "", StringComparison.Ordinal)).Verdict);
        worker.Status = "cuda-unavailable";
        PtxCorrectnessReport unavailable = evaluator.Evaluate(Axpy.Source);
        Assert.Equal(PtxCorrectnessVerdict.Unavailable, unavailable.Verdict);
        Assert.Empty(unavailable.Cases);
    }

    [Fact]
    public void A_hung_worker_is_a_launch_failure_not_a_pass()
    {
        var (evaluator, worker) = Evaluator();
        Assert.True(evaluator.Evaluate(Axpy.Source).Passed);
        worker.ExchangeStatus = PtxWorkerExchangeStatus.TimedOut;
        PtxCorrectnessReport report = evaluator.Evaluate(Axpy.Source);
        Assert.Equal(PtxCorrectnessVerdict.LaunchFailed, report.Verdict);
    }

    [Fact]
    public void A_reference_that_throws_is_reported_rather_than_passing_the_candidate()
    {
        var worker = new FakeWorkerTransport();
        var broken = PtxKernelReference.Create("broken-v1", call => call.Output<double>("out"));
        var evaluator = new PtxCorrectnessEvaluator(new PtxProgramCompiler(Axpy.Contract(), worker), broken, null);
        PtxCorrectnessReport report = evaluator.Evaluate(Axpy.Source);
        Assert.Equal(PtxCorrectnessVerdict.ReferenceFailed, report.Verdict);
        Assert.Throws<ArgumentException>(() => new PtxCorrectnessEvaluator(new PtxProgramCompiler(Axpy.Contract(), worker), null, null));
    }
}