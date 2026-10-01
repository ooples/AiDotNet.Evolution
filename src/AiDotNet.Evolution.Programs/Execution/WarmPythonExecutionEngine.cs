using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDotNet.Evolution.Programs;

/// <summary>
/// Runs Python candidates through pre-started interpreters, so an evaluation does not pay for starting one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProgramSandboxMode.WarmForkWorker"/> keeps isolation per candidate: the warm interpreter forks a fresh
/// child for each one, applies the memory and CPU-time limits to that child, runs it in its own process group and
/// working directory, and kills the group at the wall-clock limit. Fork exists only on Linux and macOS.
/// </para>
/// <para>
/// <see cref="ProgramSandboxMode.WarmReusedWorker"/> runs candidates one after another in the same interpreter, each
/// in a fresh namespace, and works on any OS. It is faster than a fresh process but weaker: a candidate can leave
/// state behind (patched modules, background threads, open files) that affects the next one until the worker is
/// replaced. The worker is replaced after a wall-clock timeout, after any candidate that exits non-zero or runs out
/// of memory, and after every <see cref="RecycleAfter"/> candidates. Use it only for candidates you would run in one
/// process anyway; it requires <see cref="ProgramSandboxOptions.AllowUnsafeInProcessExecution"/>.
/// </para>
/// <para>
/// Neither mode restricts filesystem, network or system calls; like the process engine, run hostile candidates inside
/// a container or VM you provision. Only Python is supported, and compile-only requests are refused.
/// </para>
/// </remarks>
public sealed class WarmPythonExecutionEngine : IProgramExecutionEngine, IProgramExecutionTelemetrySource, IDisposable
{
    private const int MemoryExitCode = 125;
    private const string ScriptResource = "AiDotNet.Evolution.Programs.Execution.warm_python_worker.py";
    private static readonly TimeSpan ReplyMargin = TimeSpan.FromSeconds(5);

    private readonly ProgramSandboxOptions _options;
    private readonly ProgramSandboxLimitOptions _limits;
    private readonly string _python;
    private readonly string _scriptPath;
    private readonly string _workspace;
    private readonly bool _fork;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<Worker> _idle = new();
    // Workers running a candidate, so Dispose can end them too rather than leave their processes behind.
    private readonly ConcurrentDictionary<Worker, byte> _busy = new();
    private int _queued;
    private int _active;
    private volatile bool _disposed;

    /// <summary>Creates the engine. Workers start on first use.</summary>
    /// <param name="options">The sandbox options. <see cref="ProgramSandboxOptions.Mode"/> must be
    /// <see cref="ProgramSandboxMode.WarmForkWorker"/> or <see cref="ProgramSandboxMode.WarmReusedWorker"/>, and a
    /// Python interpreter must be configured.</param>
    /// <param name="recycleAfter">Candidates a reused worker runs before it is replaced, 1 to 100,000.</param>
    /// <exception cref="ArgumentException">The mode is not a warm mode, no Python interpreter is configured, or the reused
    /// mode was chosen without <see cref="ProgramSandboxOptions.AllowUnsafeInProcessExecution"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">The fork mode was chosen on Windows.</exception>
    public WarmPythonExecutionEngine(ProgramSandboxOptions options, int recycleAfter = 100)
    {
        ProgramGuard.NotNull(options);
        _options = options.Clone();
        _options.Validate();
        if (recycleAfter is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(recycleAfter));
        _fork = _options.Mode switch
        {
            ProgramSandboxMode.WarmForkWorker => true,
            ProgramSandboxMode.WarmReusedWorker => false,
            _ => throw new ArgumentException("WarmPythonExecutionEngine runs only the warm sandbox modes.", nameof(options))
        };
        if (_fork && OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The warm fork worker needs fork(), which Windows does not have. Use WarmReusedWorker or the process engine.");
        if (!_fork && !_options.AllowUnsafeInProcessExecution)
            throw new ArgumentException("WarmReusedWorker runs candidates in one shared interpreter. Set AllowUnsafeInProcessExecution to acknowledge that.", nameof(options));
        if (!_options.TryGetInterpreter(ProgramLanguage.Python, out ProgramInterpreterSpecification? interpreter) || interpreter is null)
            throw new ArgumentException("A Python interpreter must be configured.", nameof(options));

        RecycleAfter = recycleAfter;
        _limits = _options.Limits;
        _python = interpreter.Executable;
        _workspace = Path.Combine(_options.WorkingDirectory ?? Path.GetTempPath(), "aidotnet-warm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        _scriptPath = Path.Combine(_workspace, "warm_python_worker.py");
        File.WriteAllText(_scriptPath, ReadScript(), new UTF8Encoding(false));
        _slots = new SemaphoreSlim(_limits.MaxConcurrentExecutions, _limits.MaxConcurrentExecutions);
        VersionHash = EvolutionHash.Combine(new[]
        {
            "warm-python-execution-v1", _options.Mode.ToString(), _python, _options.RuntimeVersion,
            recycleAfter.ToString(CultureInfo.InvariantCulture), _limits.TimeLimitSeconds.ToString(CultureInfo.InvariantCulture),
            _limits.MemoryLimitMb.ToString(CultureInfo.InvariantCulture), _limits.CpuLimit.ToString("R", CultureInfo.InvariantCulture),
            _limits.CpuTimeLimitSeconds?.ToString(CultureInfo.InvariantCulture) ?? "derived-cpu-time",
            _limits.MaxStdOutChars.ToString(CultureInfo.InvariantCulture), _limits.MaxStdErrChars.ToString(CultureInfo.InvariantCulture),
            EvolutionHash.Compute(ReadScript())
        });
    }

    /// <inheritdoc />
    public string Id => "warm-python-execution";

    /// <inheritdoc />
    public string VersionHash { get; }

    /// <summary>Gets how many candidates a reused worker runs before it is replaced.</summary>
    public int RecycleAfter { get; }

    /// <inheritdoc />
    public int QueuedExecutionCount => Volatile.Read(ref _queued);

    // Workers currently tracked as running a candidate; zero whenever no execution is in progress.
    internal int BusyWorkerCount => _busy.Count;

    /// <inheritdoc />
    public int ActiveExecutionCount => Volatile.Read(ref _active);

    /// <inheritdoc />
    public bool TryExecute(ProgramLanguage language, string sourceCode, string input, out string output, out string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        ProgramExecuteResponse response = ExecuteAsync(new ProgramExecuteRequest
        {
            Language = language,
            SourceCode = sourceCode,
            StdIn = input
        }, cancellationToken).GetAwaiter().GetResult();
        output = response.StdOut;
        errorMessage = response.Success ? null : response.Error;
        return response.Success;
    }

    /// <inheritdoc />
    public async Task<ProgramExecuteResponse> ExecuteAsync(ProgramExecuteRequest request, CancellationToken cancellationToken = default)
    {
        ProgramGuard.NotNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.Language is not (ProgramLanguage.Python or ProgramLanguage.Generic))
            return Rejected(ProgramExecuteErrorCode.InvalidRequest, "The warm worker runs Python only.");
        if (request.CompileOnly)
            return Rejected(ProgramExecuteErrorCode.InvalidRequest, "The warm worker does not support compile-only requests.");
        if (string.IsNullOrEmpty(request.SourceCode))
            return Rejected(ProgramExecuteErrorCode.SourceCodeRequired, "SourceCode is required.");
        if (request.SourceCode.Length > _limits.MaxSourceCodeChars)
            return Rejected(ProgramExecuteErrorCode.SourceCodeTooLarge, "The source exceeds the sandbox's size limit.");
        if ((request.StdIn?.Length ?? 0) > _limits.MaxStdInChars)
            return Rejected(ProgramExecuteErrorCode.StdInTooLarge, "Standard input exceeds the sandbox's size limit.");

        Interlocked.Increment(ref _queued);
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Rejected(ProgramExecuteErrorCode.TimeoutOrCanceled, "Execution was canceled while queued.");
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }

        Interlocked.Increment(ref _active);
        Worker? worker = null;
        // The busy entry is removed whatever happens to the worker, and only a worker that finished a whole exchange goes
        // back to the pool: an exception or a cancellation can leave half a frame in its pipe, so it is disposed instead.
        Worker? tracked = null;
        bool reusable = false;
        try
        {
            worker = _idle.TryTake(out Worker? idle) && idle.IsUsable ? idle : await StartWorkerAsync(cancellationToken).ConfigureAwait(false);
            _busy[worker] = 0;
            tracked = worker;
            if (_disposed) return Failed(ProgramExecuteErrorCode.ExecutionFailed, "The engine was disposed before the candidate ran.");
            var frame = new JsonObject
            {
                ["source"] = request.SourceCode,
                ["stdin"] = request.StdIn ?? string.Empty,
                ["time_limit"] = _limits.TimeLimitSeconds,
                ["memory_bytes"] = _limits.GetMemoryLimitBytes(),
                ["cpu_seconds"] = (int)_limits.GetCpuTimeLimit().TotalSeconds,
                ["max_stdout"] = _limits.MaxStdOutChars,
                ["max_stderr"] = _limits.MaxStdErrChars,
                ["fork"] = _fork
            };

            // A fork worker enforces the wall clock itself and replies; a reused worker cannot interrupt the candidate
            // it is running, so the engine stops waiting at the limit and replaces the worker.
            TimeSpan wait = _fork ? _limits.GetTimeLimit() + ReplyMargin : _limits.GetTimeLimit();
            (JsonNode? reply, WorkerReadOutcome outcome) = await worker.ExchangeAsync(frame, wait, cancellationToken).ConfigureAwait(false);
            if (reply is null)
            {
                worker.Dispose();
                worker = null;
                return NoReply(outcome, cancellationToken.IsCancellationRequested);
            }

            worker.Completed++;
            bool recycle = reply["recycle"]?.GetValue<bool>() == true || (!_fork && worker.Completed >= RecycleAfter);
            ProgramExecuteResponse response = FromReply(reply);
            if (recycle) { worker.Dispose(); worker = null; }
            reusable = true;
            return response;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            worker?.Dispose();
            worker = null;
            return Failed(ProgramExecuteErrorCode.ExecutionFailed, "The warm worker failed: " + exception.GetType().Name + ".");
        }
        finally
        {
            if (tracked is not null) _busy.TryRemove(tracked, out _);
            if (worker is not null && !reusable)
            {
                worker.Dispose();
                worker = null;
            }
            if (worker is not null)
            {
                if (_disposed) worker.Dispose();
                else
                {
                    _idle.Add(worker);
                    // Dispose may have drained the pool between the check and the add; do not leave this one behind.
                    if (_disposed) DrainIdle();
                }
            }

            Interlocked.Decrement(ref _active);
            _slots.Release();
        }
    }

    // A missing reply is a timeout only when the deadline passed. A worker that exited closed the pipe at once: the
    // candidate crashed the interpreter or called os._exit, and that is a failure, not a timeout.
    private ProgramExecuteResponse NoReply(WorkerReadOutcome outcome, bool canceled)
    {
        if (canceled) return Failed(ProgramExecuteErrorCode.TimeoutOrCanceled, "Execution was canceled.");
        if (_disposed) return Failed(ProgramExecuteErrorCode.ExecutionFailed, "The engine was disposed during execution.");
        if (outcome == WorkerReadOutcome.WorkerExited)
            return Failed(ProgramExecuteErrorCode.ExecutionFailed, "The warm worker exited during the candidate and was replaced.");
        return _fork
            ? Failed(ProgramExecuteErrorCode.ExecutionFailed, "The warm worker stopped responding and was replaced.")
            : Failed(ProgramExecuteErrorCode.TimeoutOrCanceled,
                $"Execution exceeded the {_limits.TimeLimitSeconds.ToString(CultureInfo.InvariantCulture)} second limit and the worker was replaced.");
    }

    private void DrainIdle()
    {
        while (_idle.TryTake(out Worker? idle)) idle.Dispose();
    }

    private ProgramExecuteResponse FromReply(JsonNode reply)
    {
        int exit = reply["exit"]?.GetValue<int>() ?? -1;
        bool timedOut = reply["timed_out"]?.GetValue<bool>() == true;
        bool memory = reply["memory_exceeded"]?.GetValue<bool>() == true || exit == MemoryExitCode;
        bool cpu = reply["cpu_exceeded"]?.GetValue<bool>() == true;
        bool success = exit == 0 && !timedOut;
        string? error = success ? null
            : timedOut ? $"Execution exceeded the {_limits.TimeLimitSeconds.ToString(CultureInfo.InvariantCulture)} second limit and the process group was terminated."
            : memory ? $"Execution exceeded the {_limits.MemoryLimitMb.ToString(CultureInfo.InvariantCulture)} MB memory limit."
            : cpu ? "Execution exceeded its CPU-time limit."
            : $"Execution failed with exit code {exit.ToString(CultureInfo.InvariantCulture)}.";
        return new ProgramExecuteResponse
        {
            Success = success,
            Language = ProgramLanguage.Python,
            ExitCode = timedOut ? -1 : exit,
            StdOut = reply["stdout"]?.GetValue<string>() ?? string.Empty,
            StdErr = reply["stderr"]?.GetValue<string>() ?? string.Empty,
            StdOutTruncated = reply["stdout_truncated"]?.GetValue<bool>() == true,
            StdErrTruncated = reply["stderr_truncated"]?.GetValue<bool>() == true,
            Error = error,
            ErrorCode = success ? null
                : timedOut ? ProgramExecuteErrorCode.TimeoutOrCanceled
                : memory ? ProgramExecuteErrorCode.MemoryLimitExceeded
                : cpu ? ProgramExecuteErrorCode.CpuTimeLimitExceeded
                : ProgramExecuteErrorCode.ExecutionFailed
        };
    }

    private async Task<Worker> StartWorkerAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workspace
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(_scriptPath);
        // Start from nothing so no key or token the host holds is visible to candidates.
        start.Environment.Clear();
        start.Environment["PATH"] = PinnedPath();
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        if (OperatingSystem.IsWindows())
        {
            start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            start.Environment["TEMP"] = _workspace;
            start.Environment["TMP"] = _workspace;
        }
        else
        {
            start.Environment["HOME"] = _workspace;
            start.Environment["TMPDIR"] = _workspace;
            start.Environment["LANG"] = "C.UTF-8";
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException("The Python interpreter did not start.");
        // No CPU-time limit on the job: a reused worker runs many executions, and job CPU time accumulates across all of
        // them, so a per-execution budget there would end a healthy worker after enough short runs. The per-execution
        // CPU limit is the worker's RLIMIT_CPU on each forked child (cpu_seconds in the request).
        var worker = new Worker(process, _fork ? null : WindowsJobObject.TryCreate(_limits.GetMemoryLimitBytes(), TimeSpan.Zero));
        (JsonNode? ready, _) = await worker.ReadAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (ready?["ready"]?.GetValue<bool>() != true)
        {
            worker.Dispose();
            throw new InvalidOperationException("The warm worker did not become ready.");
        }

        return worker;
    }

    private static string PinnedPath() => OperatingSystem.IsWindows()
        ? string.Join(";", new[] { Environment.GetFolderPath(Environment.SpecialFolder.System), Environment.GetFolderPath(Environment.SpecialFolder.Windows) }
            .Where(path => !string.IsNullOrWhiteSpace(path)))
        : "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    private static string ReadScript()
    {
        using Stream stream = typeof(WarmPythonExecutionEngine).Assembly.GetManifestResourceStream(ScriptResource)
            ?? throw new InvalidOperationException("The warm worker script is missing from the assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static ProgramExecuteResponse Rejected(ProgramExecuteErrorCode code, string error) => new()
    {
        Success = false,
        Language = ProgramLanguage.Python,
        ExitCode = -1,
        Error = error,
        ErrorCode = code
    };

    private static ProgramExecuteResponse Failed(ProgramExecuteErrorCode code, string error) => Rejected(code, error);

    /// <summary>Stops every worker and deletes the engine's working directory.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DrainIdle();
        // Ending a busy worker closes its pipe, so the execution waiting on it returns at once and its finally
        // disposes nothing twice. The semaphore is left undisposed: in-flight executions still release it.
        foreach (Worker busy in _busy.Keys) busy.Dispose();
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // A worker that is still exiting can hold the directory briefly; the temp directory is cleaned up later.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private enum WorkerReadOutcome
    {
        Reply,
        DeadlineOrCanceled,
        WorkerExited
    }

    private sealed class Worker : IDisposable
    {
        private readonly Process _process;
        private readonly WindowsJobObject? _job;
        private readonly Stream _input;
        private readonly Stream _output;
        private int _disposed;

        internal Worker(Process process, WindowsJobObject? job)
        {
            _process = process;
            _job = job;
            if (job is not null) job.TryAssign(process.Handle);
            _input = process.StandardInput.BaseStream;
            _output = process.StandardOutput.BaseStream;
            // Drain stderr so a chatty interpreter can never block on a full pipe.
            _ = Task.Run(async () =>
            {
                try { await process.StandardError.BaseStream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            });
        }

        internal int Completed { get; set; }

        internal bool IsUsable => !_process.HasExited;

        internal async Task<(JsonNode? Reply, WorkerReadOutcome Outcome)> ExchangeAsync(JsonObject request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            byte[] body = Encoding.UTF8.GetBytes(request.ToJsonString());
            byte[] header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
            await _input.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _input.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
            return await ReadAsync(timeout, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<(JsonNode? Reply, WorkerReadOutcome Outcome)> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                byte[] header = new byte[4];
                await _output.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
                if (length is < 2 or > 64 * 1024 * 1024) throw new InvalidDataException("The warm worker sent an invalid frame.");
                byte[] body = new byte[length];
                await _output.ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);
                return (JsonNode.Parse(body), WorkerReadOutcome.Reply);
            }
            catch (OperationCanceledException)
            {
                return (null, WorkerReadOutcome.DeadlineOrCanceled);
            }
            catch (EndOfStreamException)
            {
                return (null, WorkerReadOutcome.WorkerExited);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already exited, or cannot be signalled; the job (Windows) or the closed pipe ends it.
            }

            _job?.Dispose();
            _process.Dispose();
        }
    }
}
