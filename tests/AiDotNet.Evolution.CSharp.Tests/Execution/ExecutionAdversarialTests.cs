using System.Text;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Execution;

public sealed class ExecutionAdversarialTests
{
    [Fact]
    public async Task IncompleteReaderNeverCertifiesAnEmptyOrMatchingPrefix()
    {
        var reader = new BoundedOutputReader(64);
        Assert.True(reader.Snapshot().Truncated);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var stream = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("ok")));
        await reader.PumpAsync(stream, canceled.Token);
        Assert.True(reader.Snapshot().Truncated);
        using var complete = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("ok")));
        await reader.PumpAsync(complete, CancellationToken.None);
        Assert.Equal(("ok", false), reader.Snapshot());
    }

    [Fact]
    public async Task SynchronousAdapterRefusesTruncatedMatchingOutput()
    {
        var options = ProgramSandboxTestEnvironment.Options(ProgramSandboxTestEnvironment.EchoSourceTemplate);
        options.Limits.MaxStdOutChars = 2;
        using var engine = new ProcessProgramExecutionEngine(options);
        var result = await Task.Run(() => engine.TryExecute(ProgramLanguage.Python, "okay", "", out _, out _));
        Assert.False(result);
    }

    [Fact]
    public async Task QueuedRequestsOwnInputAndCompileModeAndDisposeDoesNotDestroyTheirPermit()
    {
        var options = ProgramSandboxTestEnvironment.Options(
            ProgramSandboxTestEnvironment.SleepTemplate(1), ProgramSandboxTestEnvironment.EchoStdInTemplate);
        options.Limits.MaxConcurrentExecutions = 1;
        options.Limits.TimeLimitSeconds = 15;
        options.Limits.MaxStdInChars = 16;
        using var engine = new ProcessProgramExecutionEngine(options);
        Task<ProgramExecuteResponse> first = engine.ExecuteAsync(new() { Language = ProgramLanguage.Python, SourceCode = "ignored" });
        var request = new ProgramExecuteRequest { Language = ProgramLanguage.Python, SourceCode = "ignored", StdIn = "original\n", CompileOnly = true };
        Task<ProgramExecuteResponse> second = engine.ExecuteAsync(request);
        Assert.Equal(1, engine.QueuedExecutionCount);
        request.StdIn = new string('x', 100);
        request.CompileOnly = false;
        engine.Dispose();
        ProgramExecuteResponse[] responses = await Task.WhenAll(first, second);
        Assert.All(responses, response => Assert.True(response.Success, response.Error));
        Assert.Contains("original", responses[1].StdOut);
        Assert.True(responses[1].CompilationAttempted);
        Assert.Equal(0, engine.ActiveExecutionCount);
        Assert.Equal(0, engine.QueuedExecutionCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.ExecuteAsync(request));
    }

    [Theory]
    [InlineData(ProgramLanguage.Generic)]
    [InlineData((ProgramLanguage)999)]
    public async Task InvalidAllowListCannotSilentlyBecomeUnrestricted(ProgramLanguage allowed)
    {
        using var engine = new ProcessProgramExecutionEngine(new());
        var response = await engine.ExecuteAsync(new()
        {
            Language = ProgramLanguage.Python,
            SourceCode = "print(1)",
            AllowedLanguages = new() { allowed }
        });
        Assert.Equal(ProgramExecuteErrorCode.InvalidRequest, response.ErrorCode);
        Assert.Equal(0, engine.ActiveExecutionCount);
    }

    [Fact]
    public void PlaceholderTextInsideAPathIsNotExpandedAgain()
    {
        Assert.Equal("'/tmp/{workspace}/program.py' '/tmp/base'", ProgramInterpreterSpecification.Expand(
            "{source} {workspace}", "/tmp/{workspace}/program.py", "/tmp/base", false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ContradictorySuccessEvidenceCannotPass(int flaw)
    {
        var engine = new ScriptedProgramExecutionEngine(_ => new()
        {
            Success = true,
            Language = flaw == 0 ? ProgramLanguage.JavaScript : ProgramLanguage.Python,
            ExitCode = flaw == 1 ? 7 : 0,
            ErrorCode = flaw == 2 ? ProgramExecuteErrorCode.ExecutionFailed : null,
            CompilationAttempted = flaw == 3,
            StdOut = flaw == 4 ? null! : "ok"
        });
        var evaluator = new SandboxedProgramFitnessEvaluator(engine, new[] { new ProgramInputOutputExample { ExpectedOutput = "ok" } });
        var result = await evaluator.EvaluateAsync(new("print(1)", ProgramLanguage.Python), new(0, 1, 1, 1));
        Assert.Equal(0, result.Quality);
        Assert.Equal("program_sandbox_invalid_response", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, result.CostUnits);
    }

    [Fact]
    public async Task CallerCancellationReportedByResponseIsNotScoredAsCandidateFailure()
    {
        using var canceled = new CancellationTokenSource();
        var engine = new ScriptedProgramExecutionEngine(_ =>
        {
            canceled.Cancel();
            return new() { Success = false, Language = ProgramLanguage.Python, ExitCode = -1, ErrorCode = ProgramExecuteErrorCode.TimeoutOrCanceled };
        });
        var evaluator = new SandboxedProgramFitnessEvaluator(engine, new[] { new ProgramInputOutputExample { ExpectedOutput = "ok" } });
        var result = await evaluator.EvaluateAsync(new("print(1)", ProgramLanguage.Python), new(0, 1, 1, 1), canceled.Token);
        Assert.Equal(EvolutionEvaluationStatus.Canceled, result.Status);
        Assert.Equal(1, result.CostUnits);
    }

    [Fact]
    public void RunnerConfigurationAndProvisionedRuntimeInvalidateFitnessIdentity()
    {
        var options = new ProgramSandboxOptions { RuntimeVersion = "fixture-v1" };
        using var first = new ProcessProgramExecutionEngine(options);
        using var same = new ProcessProgramExecutionEngine(options);
        Assert.Equal(first.VersionHash, same.VersionHash);
        options.Limits.MaxStdOutChars++;
        using var second = new ProcessProgramExecutionEngine(options);
        options.RuntimeVersion = "image-sha256-changed";
        using var third = new ProcessProgramExecutionEngine(options);
        var cases = new[] { new ProgramInputOutputExample { ExpectedOutput = "ok" } };
        Assert.NotEqual(new SandboxedProgramFitnessEvaluator(first, cases).VersionHash, new SandboxedProgramFitnessEvaluator(second, cases).VersionHash);
        Assert.NotEqual(new InputOutputProgramFitnessEvaluator(second, cases).VersionHash, new InputOutputProgramFitnessEvaluator(third, cases).VersionHash);
    }

    [Fact]
    public void UnversionedRuntimeCannotReuseAnotherEnginesCacheIdentity()
    {
        using var first = new ProcessProgramExecutionEngine(new());
        using var second = new ProcessProgramExecutionEngine(new());
        Assert.NotEqual(first.VersionHash, second.VersionHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineIdentityMutationIsNotConvertedToAReusableFailureScore(bool asynchronous)
    {
        var engine = new ChangingEngine();
        var examples = new[] { new ProgramInputOutputExample() };
        IProgramFitnessEvaluator evaluator = asynchronous
            ? new SandboxedProgramFitnessEvaluator(engine, examples)
            : new InputOutputProgramFitnessEvaluator(engine, examples);
        await Assert.ThrowsAsync<InvalidOperationException>(() => evaluator.EvaluateAsync(
            new("print(1)", ProgramLanguage.Python), new(0, 1, 1, 1)).AsTask());
    }

    private sealed class ChangingEngine : IProgramExecutionEngine
    {
        public string Id => "changing-runner";
        public string VersionHash { get; private set; } = "v1";
        public Task<ProgramExecuteResponse> ExecuteAsync(ProgramExecuteRequest request, CancellationToken cancellationToken = default)
        {
            VersionHash = "v2";
            throw new IOException("runtime replaced");
        }
        public bool TryExecute(ProgramLanguage language, string sourceCode, string input,
            out string output, out string? errorMessage, CancellationToken cancellationToken = default)
        {
            VersionHash = "v2";
            throw new IOException("runtime replaced");
        }
    }
}
