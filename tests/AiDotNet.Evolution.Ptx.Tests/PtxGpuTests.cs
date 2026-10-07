using Xunit;

namespace AiDotNet.Evolution.Ptx.Tests;

/// <summary>End to end on a real sm_75 device. Skipped wherever the isolated worker finds no such device (CI included).</summary>
public sealed class PtxGpuTests
{
    private static PtxProgramCompiler Compiler(TimeSpan? timeout = null) =>
        new(Axpy.Contract(timingN: 1 << 20), new PtxIsolationOptions { Timeout = timeout ?? TimeSpan.FromSeconds(60) });

    [CudaFact]
    public void The_driver_jit_loads_a_real_kernel_and_reports_ptxas_errors_by_line()
    {
        PtxProgramCompiler compiler = Compiler();
        PtxCompilationResult good = compiler.Compile(Axpy.Source);
        Assert.True(good.Succeeded, good.ToFeedback());
        Assert.InRange(good.Resources?.Registers ?? 0, 1, 255);
        Assert.Equal(0, good.Resources?.LocalBytes);
        PtxCompilationResult bad = compiler.Compile(Axpy.Source.Replace(Axpy.Fma, "    fma.rn.f32 %f4, %f1, %f2, %f9;", StringComparison.Ordinal));
        Assert.False(bad.Succeeded);
        Assert.False(bad.IsInfrastructureFailure);
        int line = Axpy.Source.Split('\n').ToList().IndexOf(Axpy.Fma) + 1;
        Assert.Contains(bad.Diagnostics, d => d.Code == "PTXAS-ERROR" && d.Line == line);
    }

    [CudaFact]
    public void Real_kernels_are_judged_against_the_reference_with_guards_and_sentinels()
    {
        var evaluator = new PtxCorrectnessEvaluator(Compiler(), Axpy.Reference, null);
        Assert.Equal(PtxCorrectnessVerdict.Passed, evaluator.Evaluate(Axpy.Source).Verdict);
        Assert.Equal(PtxCorrectnessVerdict.Mismatch, evaluator.Evaluate(Axpy.Wrong).Verdict);
        Assert.Equal(PtxCorrectnessVerdict.OutOfBoundsWrite, evaluator.Evaluate(Axpy.Unguarded).Verdict);
        Assert.Equal(PtxCorrectnessVerdict.Passed, new PtxCorrectnessEvaluator(Compiler(), null, Axpy.Source).Evaluate(Axpy.Block128).Verdict);
    }

    [CudaFact]
    public void A_spinning_kernel_is_killed_by_the_watchdog()
    {
        var evaluator = new PtxCorrectnessEvaluator(Compiler(TimeSpan.FromSeconds(10)), Axpy.Reference, null);
        PtxCorrectnessReport report = evaluator.Evaluate(Axpy.Hanging);
        Assert.Equal(PtxCorrectnessVerdict.LaunchFailed, report.Verdict);
    }

    [CudaFact]
    public void Real_paired_timing_produces_tight_evidence_and_a_loadable_artifact()
    {
        PtxProgramCompiler compiler = Compiler();
        var timing = new PtxTimingEvaluator(compiler);
        PtxTimingReport report = timing.Measure(Axpy.Block128, Axpy.Source);
        Assert.True(report.Completed, report.Message);
        PtxPairedTimingEvidence evidence = report.Evidence ?? throw new InvalidOperationException(report.Message);
        Assert.InRange(evidence.MedianSpeedup, 0.5, 2.0);
        Assert.True(evidence.CandidateTiming.Iqr < evidence.CandidateTiming.Median, "Timing should be stable on an idle device.");
        PtxCorrectnessReport correctness = new PtxCorrectnessEvaluator(compiler, Axpy.Reference, null).Evaluate(Axpy.Block128);
        PtxKernelArtifact artifact = PtxKernelArtifact.Create(compiler, correctness, report, Axpy.Source, timing.Identity);
        Assert.Equal(artifact.ArtifactId, PtxKernelArtifact.FromBytes(artifact.ToBytes()).ArtifactId);
        Assert.Equal(75, artifact.SmVersion);
    }
}