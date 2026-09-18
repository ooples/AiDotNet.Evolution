using AiDotNet.Evolution.Programs;
namespace AiDotNet.Evolution.CSharp.Tests.Execution;

internal sealed class FakeExecutionOutcome
{
    private FakeExecutionOutcome(bool succeeded, string output, string? errorMessage)
    {
        Succeeded = succeeded;
        Output = output;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }
    public string Output { get; }
    public string? ErrorMessage { get; }

    public static FakeExecutionOutcome Success(string output) => new(true, output, null);
    public static FakeExecutionOutcome Failure(string errorMessage) => new(false, string.Empty, errorMessage);
}

internal sealed class FakeProgramExecutionEngine : IProgramExecutionEngine
{
    public string Id => "FakeProgramExecutionEngine";
    public string VersionHash => "scripted-test-v1";
    private readonly Func<string, string, FakeExecutionOutcome> _handler;
    private int _calls;

    public FakeProgramExecutionEngine(Func<string, string, FakeExecutionOutcome> handler) => _handler = handler;

    public int Calls => _calls;
    public ProgramLanguage? LastLanguage { get; private set; }
    public string? LastStdIn { get; private set; }
    public bool LastCompileOnly { get; private set; }
    public int PeakConcurrency { get; private set; }

    private int _active;
    private readonly object _gate = new();

    public bool TryExecute(
        ProgramLanguage language,
        string sourceCode,
        string input,
        out string output,
        out string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        _calls++;
        LastLanguage = language;
        LastStdIn = input;
        cancellationToken.ThrowIfCancellationRequested();
        FakeExecutionOutcome outcome = _handler(sourceCode, input);
        output = outcome.Output;
        errorMessage = outcome.ErrorMessage;
        return outcome.Succeeded;
    }

    public Task<ProgramExecuteResponse> ExecuteAsync(
        ProgramExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _calls++;
            _active++;
            if (_active > PeakConcurrency) PeakConcurrency = _active;
        }

        try
        {
            LastLanguage = request.Language;
            LastStdIn = request.StdIn;
            LastCompileOnly = request.CompileOnly;
            FakeExecutionOutcome outcome = _handler(request.SourceCode, request.StdIn ?? string.Empty);

            return Task.FromResult(new ProgramExecuteResponse
            {
                Success = outcome.Succeeded,
                Language = request.Language,
                ExitCode = outcome.Succeeded ? 0 : 1,
                StdOut = outcome.Output,
                StdErr = outcome.ErrorMessage ?? string.Empty,
                Error = outcome.ErrorMessage,
                ErrorCode = outcome.Succeeded ? null : ProgramExecuteErrorCode.ExecutionFailed
            });
        }
        finally
        {
            lock (_gate) _active--;
        }
    }
}

internal sealed class ThrowingProgramExecutionEngine : IProgramExecutionEngine
{
    public string Id => "ThrowingProgramExecutionEngine";
    public string VersionHash => "scripted-test-v1";
    public bool TryExecute(
        ProgramLanguage language,
        string sourceCode,
        string input,
        out string output,
        out string? errorMessage,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("sandbox unavailable");

    public Task<ProgramExecuteResponse> ExecuteAsync(
        ProgramExecuteRequest request,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("sandbox unavailable");
}

internal sealed class ScriptedProgramExecutionEngine : IProgramExecutionEngine
{
    public string Id => "ScriptedProgramExecutionEngine";
    public string VersionHash => "scripted-test-v1";
    private readonly Func<ProgramExecuteRequest, ProgramExecuteResponse> _handler;

    public ScriptedProgramExecutionEngine(Func<ProgramExecuteRequest, ProgramExecuteResponse> handler) =>
        _handler = handler;

    public int Calls { get; private set; }

    public bool TryExecute(
        ProgramLanguage language,
        string sourceCode,
        string input,
        out string output,
        out string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        ProgramExecuteResponse response = ExecuteAsync(
            new ProgramExecuteRequest { Language = language, SourceCode = sourceCode, StdIn = input },
            cancellationToken).GetAwaiter().GetResult();
        output = response.StdOut;
        errorMessage = response.Error;
        return response.Success;
    }

    public Task<ProgramExecuteResponse> ExecuteAsync(
        ProgramExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(_handler(request));
    }
}
