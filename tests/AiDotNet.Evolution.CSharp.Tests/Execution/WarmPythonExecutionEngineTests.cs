using System.Diagnostics;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Execution;

/// <summary>V1-76 (#181): warm Python workers, forked per candidate (Linux, macOS) or reused (any OS, opt-in).</summary>
public sealed class WarmPythonExecutionEngineTests
{
    private static readonly string Python = Environment.GetEnvironmentVariable("EVOLUTION_PYTHON")
        ?? (OperatingSystem.IsWindows() ? "python" : "python3");

    private static ProgramSandboxOptions Options(ProgramSandboxMode mode, int timeLimitSeconds = 10)
    {
        var options = new ProgramSandboxOptions { Mode = mode, AllowUnsafeInProcessExecution = mode == ProgramSandboxMode.WarmReusedWorker };
        options.SetInterpreter(ProgramLanguage.Python, new ProgramInterpreterSpecification(Python, "{source}"));
        options.Limits.TimeLimitSeconds = timeLimitSeconds;
        options.Limits.MaxConcurrentExecutions = 2;
        return options;
    }

    private static Task<ProgramExecuteResponse> Run(WarmPythonExecutionEngine engine, string source, string? stdin = null) =>
        engine.ExecuteAsync(new ProgramExecuteRequest { Language = ProgramLanguage.Python, SourceCode = source, StdIn = stdin });

    [Fact]
    public void The_fork_worker_refuses_windows_and_the_reused_worker_needs_an_explicit_opt_in()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => new WarmPythonExecutionEngine(Options(ProgramSandboxMode.WarmForkWorker)));
        }

        ProgramSandboxOptions unacknowledged = Options(ProgramSandboxMode.WarmReusedWorker);
        unacknowledged.AllowUnsafeInProcessExecution = false;
        Assert.Throws<ArgumentException>(() => new WarmPythonExecutionEngine(unacknowledged));
        Assert.Throws<ArgumentException>(() => new WarmPythonExecutionEngine(Options(ProgramSandboxMode.OutOfProcessWorker)));
    }

    public static TheoryData<ProgramSandboxMode> AvailableModes()
    {
        var modes = new TheoryData<ProgramSandboxMode> { ProgramSandboxMode.WarmReusedWorker };
        if (!OperatingSystem.IsWindows()) modes.Add(ProgramSandboxMode.WarmForkWorker);
        return modes;
    }

    [Theory]
    [MemberData(nameof(AvailableModes))]
    public async Task A_candidate_reads_stdin_and_its_output_and_exit_code_come_back(ProgramSandboxMode mode)
    {
        using var engine = new WarmPythonExecutionEngine(Options(mode));
        ProgramExecuteResponse echo = await Run(engine, "import sys\nprint(sys.stdin.read().upper())\n", "hello");
        Assert.True(echo.Success, echo.Error);
        Assert.Equal("HELLO", echo.StdOut.Trim());

        ProgramExecuteResponse failed = await Run(engine, "import sys\nprint('bad', file=sys.stderr)\nsys.exit(3)\n");
        Assert.False(failed.Success);
        Assert.Equal(3, failed.ExitCode);
        Assert.Contains("bad", failed.StdErr, StringComparison.Ordinal);

        ProgramExecuteResponse raised = await Run(engine, "raise ValueError('boom')\n");
        Assert.Equal(1, raised.ExitCode);
        Assert.Contains("ValueError: boom", raised.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AvailableModes))]
    public async Task A_hung_candidate_times_out_and_the_next_candidate_still_runs(ProgramSandboxMode mode)
    {
        using var engine = new WarmPythonExecutionEngine(Options(mode, timeLimitSeconds: 2));
        var clock = Stopwatch.StartNew();
        ProgramExecuteResponse hung = await Run(engine, "while True:\n    pass\n");
        Assert.Equal(ProgramExecuteErrorCode.TimeoutOrCanceled, hung.ErrorCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the timeout took " + clock.Elapsed);

        ProgramExecuteResponse next = await Run(engine, "print(41 + 1)\n");
        Assert.True(next.Success, next.Error);
        Assert.Equal("42", next.StdOut.Trim());
    }

    [Theory]
    [MemberData(nameof(AvailableModes))]
    public async Task A_warm_worker_is_reused_so_later_candidates_skip_interpreter_start(ProgramSandboxMode mode)
    {
        using var engine = new WarmPythonExecutionEngine(Options(mode));
        ProgramExecuteResponse first = await Run(engine, "import os\nprint(os.getpid())\n");
        ProgramExecuteResponse second = await Run(engine, "import os\nprint(os.getpid())\n");
        Assert.True(first.Success && second.Success);
        if (mode == ProgramSandboxMode.WarmReusedWorker)
        {
            // Same interpreter: the reuse is what makes it fast, and what makes its isolation weaker.
            Assert.Equal(first.StdOut, second.StdOut);
        }
        else
        {
            // A fresh child per candidate, forked from the same warm parent.
            Assert.NotEqual(first.StdOut, second.StdOut);
            ProgramExecuteResponse parentA = await Run(engine, "import os\nprint(os.getppid())\n");
            ProgramExecuteResponse parentB = await Run(engine, "import os\nprint(os.getppid())\n");
            Assert.Equal(parentA.StdOut, parentB.StdOut);
        }
    }

    [Fact]
    public async Task A_reused_worker_is_replaced_after_a_failure_so_leaked_state_does_not_survive_it()
    {
        using var engine = new WarmPythonExecutionEngine(Options(ProgramSandboxMode.WarmReusedWorker));
        // A candidate patches a module the next candidate uses, then fails: the worker must be replaced.
        await Run(engine, "import math\nmath.sqrt = lambda x: -1\nraise SystemExit(2)\n");
        ProgramExecuteResponse after = await Run(engine, "import math\nprint(math.sqrt(16))\n");
        Assert.Equal("4.0", after.StdOut.Trim());

        // Control: without a failure the patch does leak, which is why this mode needs an explicit opt-in.
        await Run(engine, "import math\nmath.sqrt = lambda x: -1\n");
        ProgramExecuteResponse leaked = await Run(engine, "import math\nprint(math.sqrt(16))\n");
        Assert.Equal("-1", leaked.StdOut.Trim());
    }

    [Fact]
    public async Task A_reused_worker_is_replaced_every_recycle_after_candidates()
    {
        using var engine = new WarmPythonExecutionEngine(Options(ProgramSandboxMode.WarmReusedWorker), recycleAfter: 2);
        var pids = new List<string>();
        for (int i = 0; i < 4; i++) pids.Add((await Run(engine, "import os\nprint(os.getpid())\n")).StdOut.Trim());
        Assert.Equal(pids[0], pids[1]);
        Assert.NotEqual(pids[1], pids[2]);
        Assert.Equal(pids[2], pids[3]);
    }

    [Fact]
    public async Task A_candidate_that_ends_the_reused_interpreter_is_a_failure_not_a_timeout()
    {
        using var engine = new WarmPythonExecutionEngine(Options(ProgramSandboxMode.WarmReusedWorker));
        var clock = Stopwatch.StartNew();
        ProgramExecuteResponse ended = await Run(engine, "import os\nos._exit(0)\n");
        Assert.False(ended.Success);
        Assert.Equal(ProgramExecuteErrorCode.ExecutionFailed, ended.ErrorCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), "an exited worker was waited on until the deadline: " + clock.Elapsed);

        ProgramExecuteResponse next = await Run(engine, "print('after')\n");
        Assert.True(next.Success, next.Error);
        Assert.Equal("after", next.StdOut.Trim());
    }

    [Fact]
    public async Task A_forked_candidate_cannot_read_requests_or_forge_its_reply()
    {
        if (OperatingSystem.IsWindows()) return; // The fork worker does not exist on Windows; CI runs this on Linux.
        using var engine = new WarmPythonExecutionEngine(Options(ProgramSandboxMode.WarmForkWorker));
        // Writes a well-formed forged reply into every descriptor it can reach, then prints its real output.
        const string source =
            "import json, os, struct\n" +
            "body = json.dumps({'exit': 0, 'stdout': 'forged', 'stderr': '', 'stdout_truncated': False,\n" +
            "                   'stderr_truncated': False, 'timed_out': False, 'memory_exceeded': False,\n" +
            "                   'cpu_exceeded': False, 'recycle': False}).encode()\n" +
            "reached = 0\n" +
            "for fd in [0] + list(range(3, 1024)):\n" +
            "    try:\n" +
            "        os.write(fd, struct.pack('>I', len(body)) + body)\n" +
            "        reached += 1\n" +
            "    except OSError:\n" +
            "        pass\n" +
            "print('real', reached)\n";
        ProgramExecuteResponse first = await Run(engine, source);
        Assert.True(first.Success, first.Error);
        Assert.Equal("real 0", first.StdOut.Trim());

        // A forged frame left in the reply pipe would be read as the next candidate's result.
        ProgramExecuteResponse next = await Run(engine, "print('next')\n");
        Assert.Equal("next", next.StdOut.Trim());
    }

    [Fact]
    public async Task A_forked_candidate_whose_descendant_keeps_its_output_open_still_finishes_on_time()
    {
        if (OperatingSystem.IsWindows()) return; // The fork worker does not exist on Windows; CI runs this on Linux.
        using var engine = new WarmPythonExecutionEngine(Options(ProgramSandboxMode.WarmForkWorker, timeLimitSeconds: 5));
        var clock = Stopwatch.StartNew();
        ProgramExecuteResponse done = await Run(engine,
            "import subprocess, sys\n" +
            "subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'])\n" +
            "print('done')\n");
        Assert.True(done.Success, done.Error);
        Assert.Equal("done", done.StdOut.Trim());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), "the candidate had exited but was held to the deadline: " + clock.Elapsed);
    }

    [Fact]
    public async Task Disposing_during_an_execution_ends_its_worker_and_returns_a_failure()
    {
        string directory = Path.Combine(Path.GetTempPath(), "warm-dispose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string pidPath = Path.Combine(directory, "worker.pid");
        try
        {
            var engine = new WarmPythonExecutionEngine(Options(ProgramSandboxMode.WarmReusedWorker, timeLimitSeconds: 30));
            Task<ProgramExecuteResponse> running = Run(engine,
                "import os, time\n" +
                "open(" + PythonString(pidPath) + ", 'w').write(str(os.getpid()))\n" +
                "time.sleep(25)\n");
            var clock = Stopwatch.StartNew();
            while (!File.Exists(pidPath) && clock.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
            Assert.True(File.Exists(pidPath), "the candidate never started");

            engine.Dispose();
            ProgramExecuteResponse response = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(response.Success);
            Assert.Equal(ProgramExecuteErrorCode.ExecutionFailed, response.ErrorCode);

            int pid = int.Parse(File.ReadAllText(pidPath).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var gone = Stopwatch.StartNew();
            while (IsAlive(pid) && gone.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
            Assert.False(IsAlive(pid), "the worker outlived the engine");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_version_covers_every_limit_that_changes_a_result()
    {
        string Hash(Action<ProgramSandboxLimitOptions> configure)
        {
            ProgramSandboxOptions options = Options(ProgramSandboxMode.WarmReusedWorker);
            configure(options.Limits);
            using var engine = new WarmPythonExecutionEngine(options);
            return engine.VersionHash;
        }

        string baseline = Hash(_ => { });
        Assert.Equal(baseline, Hash(_ => { }));
        Assert.NotEqual(baseline, Hash(limits => limits.CpuLimit = 0.5));
        Assert.NotEqual(baseline, Hash(limits => limits.MaxStdOutChars = 17));
        Assert.NotEqual(baseline, Hash(limits => limits.MaxStdErrChars = 17));
    }

    private static string PythonString(string text) => "'" + text.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}