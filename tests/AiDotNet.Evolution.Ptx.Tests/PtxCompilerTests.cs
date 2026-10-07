using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.Ptx.Tests;

public sealed class PtxCompilerTests
{
    private static (PtxProgramCompiler Compiler, FakeWorkerTransport Worker) Compiler()
    {
        var worker = new FakeWorkerTransport();
        return (new PtxProgramCompiler(Axpy.Contract(), worker), worker);
    }

    [Fact]
    public void A_valid_kernel_compiles_and_reports_its_resources_and_launch()
    {
        var (compiler, worker) = Compiler();
        PtxCompilationResult result = compiler.Compile(Axpy.Source);
        Assert.True(result.Succeeded, result.ToFeedback());
        Assert.Equal(12, result.Resources?.Registers);
        Assert.Equal(256, result.Launch.BlockX);
        Assert.Equal("sm_75", "sm_" + result.Device?.SmVersion);
        PtxWorkerKernel sent = Assert.Single(Assert.Single(worker.Requests).Kernels);
        Assert.Equal(255, sent.MaxRegisters);
        Assert.Equal("axpy", sent.EntryPoint);
        Assert.Equal(128, compiler.Compile(Axpy.Block128).Launch.BlockX);
    }

    [Theory]
    [InlineData(".version 6.4", "", "PTX002")]
    [InlineData(".target sm_75", ".target sm_80", "PTX003")]
    [InlineData(".address_size 64", ".address_size 32", "PTX004")]
    [InlineData(".visible .entry axpy(", ".visible .entry other(", "PTX005")]
    [InlineData("    .param .s32 n\n", "    .param .s32 n,\n    .param .s32 extra\n", "PTX006")]
    [InlineData("    .param .u64 out,", "    .param .u32 out,", "PTX007")]
    public void Static_contract_violations_are_diagnosed_without_a_gpu(string original, string replacement, string code)
    {
        var (compiler, worker) = Compiler();
        PtxCompilationResult result = compiler.Compile(Axpy.Source.Replace(original, replacement, StringComparison.Ordinal));
        Assert.False(result.Succeeded);
        CompilationDiagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Code == code);
        Assert.Equal(CompilationDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("aidotnet-ptx", diagnostic.Tool);
        Assert.Empty(worker.Requests);
        Assert.Contains(code, result.ToFeedback(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("// aidotnet-launch: block=2048,1,1\n")]
    [InlineData("// aidotnet-launch: block=abc\n")]
    public void A_bad_launch_header_is_refused(string header)
    {
        var (compiler, _) = Compiler();
        PtxCompilationResult result = compiler.Compile(header + Axpy.Source);
        Assert.Contains(result.Diagnostics, d => d.Code == "PTX008" && d.Line == 1);
    }

    [Fact]
    public void Jit_errors_become_line_numbered_diagnostics_and_bounded_repair_feedback()
    {
        var (compiler, worker) = Compiler();
        worker.Compile = _ => new PtxWorkerCompiled
        {
            Loaded = false,
            ErrorCode = "CUDA_ERROR_INVALID_PTX",
            ErrorLog = "ptxas application ptx input, line 38; error   : Unknown symbol '%f9'\nptxas application ptx input, line 38; error   : Arguments mismatch for instruction 'fma'\nptxas fatal   : Ptx assembly aborted due to errors\n",
            InfoLog = "ptxas info    : 0 bytes gmem\nptxas application ptx input, line 12; warning : Unused register\n"
        };
        PtxCompilationResult result = compiler.Compile(Axpy.Source);
        Assert.False(result.Succeeded);
        Assert.False(result.IsInfrastructureFailure);
        Assert.Null(result.Resources);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == "PTXAS-ERROR" && d.Line == 38 && d.Tool == "ptxas"));
        Assert.Single(result.Diagnostics, d => d.Code == "PTXAS-FATAL" && d.Line is null);
        Assert.Single(result.Diagnostics, d => d.Severity == CompilationDiagnosticSeverity.Warning && d.Line == 12);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == CompilationDiagnosticSeverity.Info);
        Assert.Contains("PTXAS-ERROR at line 38: Unknown symbol '%f9'", result.ToFeedback(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_load_failure_without_a_log_still_reports_the_driver_error()
    {
        var (compiler, worker) = Compiler();
        worker.Compile = _ => new PtxWorkerCompiled { Loaded = false, ErrorCode = "CUDA_ERROR_NOT_FOUND" };
        Assert.Contains(compiler.Compile(Axpy.Source).Diagnostics, d => d.Code == "PTX101" && d.Message.Contains("CUDA_ERROR_NOT_FOUND", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(300, 0, 0, 1024, "PTX111")]
    [InlineData(32, 70_000, 0, 1024, "PTX112")]
    [InlineData(32, 0, 64, 1024, "PTX113")]
    [InlineData(32, 0, 0, 128, "PTX114")]
    public void Kernels_past_the_target_limits_are_rejected(int registers, int shared, int local, int maxThreads, string code)
    {
        var (compiler, worker) = Compiler();
        worker.Compile = _ => new PtxWorkerCompiled { Loaded = true, Registers = registers, StaticSharedBytes = shared, LocalBytes = local, MaxThreadsPerBlock = maxThreads };
        PtxCompilationResult result = compiler.Compile(Axpy.Source);
        Assert.False(result.Succeeded);
        Assert.Single(result.Diagnostics, d => d.Code == code);
        Assert.NotNull(result.Resources);
    }

    [Fact]
    public void A_different_device_architecture_is_an_environment_failure()
    {
        var (compiler, worker) = Compiler();
        worker.ComputeMajor = 8;
        worker.ComputeMinor = 6;
        PtxCompilationResult result = compiler.Compile(Axpy.Source);
        Assert.False(result.Succeeded);
        Assert.True(result.IsInfrastructureFailure);
        Assert.Contains(result.Diagnostics, d => d.Code == "PTX110");
    }

    [Theory]
    [InlineData("Unavailable", "ok", true)]
    [InlineData("Completed", "cuda-unavailable", true)]
    [InlineData("TimedOut", "ok", false)]
    [InlineData("Crashed", "ok", false)]
    public void Worker_failures_are_diagnosed_and_classified(string status, string workerStatus, bool infrastructure)
    {
        var (compiler, worker) = Compiler();
        worker.ExchangeStatus = Enum.Parse<PtxWorkerExchangeStatus>(status);
        worker.Status = workerStatus;
        PtxCompilationResult result = compiler.Compile(Axpy.Source);
        Assert.False(result.Succeeded);
        Assert.Equal(infrastructure, result.IsInfrastructureFailure);
        Assert.Contains(result.Diagnostics, d => d.Code == "PTX100");
    }

    [Fact]
    public void As_a_program_compiler_it_catalogs_body_lines_applies_line_patches_and_builds_ptx_artifacts()
    {
        var (compiler, _) = Compiler();
        var parent = new ProgramSnapshot(new Dictionary<string, string> { [PtxProgramCompiler.FileName] = Axpy.Source });
        IReadOnlyList<EditTarget> catalog = compiler.Catalog(parent);
        Assert.All(catalog, t => Assert.Equal(PtxProgramCompiler.FileName, t.File));
        Assert.Contains(catalog, t => t.Kind == "declaration");
        Assert.Contains(catalog, t => t.Kind == "label");
        // The signature's .param declarations sit outside the body and are never targets; ld.param instructions are body lines.
        Assert.DoesNotContain(catalog, t => Axpy.Source.Substring(t.Start, t.Length).TrimStart().StartsWith(".param", StringComparison.Ordinal));
        EditTarget fma = catalog.Single(t => Axpy.Source.Substring(t.Start, t.Length) == Axpy.Fma);
        ProgramSnapshot patched = compiler.Apply(parent, new PatchPlan(parent.Fingerprint, "Use mul+add.",
            new[] { new SourceEdit(fma, "    mul.rn.f32 %f4, %f1, %f2;\n    add.rn.f32 %f4, %f4, %f3;") }));
        Assert.Contains("mul.rn.f32", patched.Files[PtxProgramCompiler.FileName], StringComparison.Ordinal);
        ProgramBuild build = compiler.Build(patched);
        Assert.NotNull(build.Artifact);
        Assert.Equal(compiler.Contract.Fingerprint, build.Artifact.ApiFingerprint);
        Assert.Equal(patched.Files[PtxProgramCompiler.FileName], System.Text.Encoding.UTF8.GetString(build.Artifact.GetImage()));
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, new PatchPlan(parent.Fingerprint, "Rename the entry.",
            new[] { new SourceEdit(fma, ".visible .entry other()") })));
        Assert.Throws<ArgumentException>(() => compiler.Apply(parent, new PatchPlan("stale", "x", new[] { new SourceEdit(fma, "ret;") })));
        Assert.Throws<ArgumentException>(() => compiler.Catalog(new ProgramSnapshot(new Dictionary<string, string> { ["other.ptx"] = Axpy.Source })));
    }
}