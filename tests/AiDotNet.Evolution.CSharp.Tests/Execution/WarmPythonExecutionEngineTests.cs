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
}
