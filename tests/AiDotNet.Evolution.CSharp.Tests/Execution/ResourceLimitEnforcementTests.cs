using System.Diagnostics;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Execution;

/// <summary>
/// V1-61 (#174): a candidate past its memory or CPU-time limit is terminated and reported as a resource failure,
/// on Windows through the job object and on Linux through the engine's process-tree measurement. Each limit has a
/// control run under it, so a sandbox that reported every run as a violation would fail too.
/// </summary>
public sealed class ResourceLimitEnforcementTests
{
    private static readonly string Python = Environment.GetEnvironmentVariable("EVOLUTION_PYTHON")
        ?? (OperatingSystem.IsWindows() ? "python" : "python3");

    // Allocates and touches 10 MB at a time, so the memory is resident, not merely reserved.
    private static string Allocate(int megabytes) =>
        "chunks = []\n" +
        "for _ in range(" + (megabytes / 10).ToString(System.Globalization.CultureInfo.InvariantCulture) + "):\n" +
        "    chunks.append(b'\\x01' * (10 * 1024 * 1024))\n" +
        "print('allocated')\n";

    [Fact]
    public async Task A_candidate_past_its_memory_limit_is_terminated_and_reported()
    {
        var clock = Stopwatch.StartNew();
        ProgramExecuteResponse over = await Run(Allocate(1024), limits => limits.MemoryLimitMb = 128);
        Assert.True(over.ErrorCode == ProgramExecuteErrorCode.MemoryLimitExceeded,
            $"expected MemoryLimitExceeded, got {over.ErrorCode} after {clock.Elapsed.TotalSeconds:F1} s: {over.Error}");
        Assert.False(over.Success);
        Assert.DoesNotContain("allocated", over.StdOut, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "the wall-clock limit ended it, not the memory limit");

        ProgramExecuteResponse under = await Run(Allocate(40), limits => limits.MemoryLimitMb = 256);
        Assert.True(under.Success, under.Error);
        Assert.Contains("allocated", under.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_memory_limit_covers_the_whole_process_tree()
    {
        // Two children of 80 MB each stay under a 128 MB per-process limit alone but not together.
        string source =
            "import subprocess, sys\n" +
            "child = " + Quote(Allocate(80) + "import time\ntime.sleep(30)\n") + "\n" +
            "procs = [subprocess.Popen([sys.executable, '-c', child]) for _ in range(2)]\n" +
            "for p in procs:\n    p.wait()\n" +
            "print('both finished')\n";
        ProgramExecuteResponse over = await Run(source, limits => limits.MemoryLimitMb = 128);
        Assert.Equal(ProgramExecuteErrorCode.MemoryLimitExceeded, over.ErrorCode);
        // Both children together need about 180 MB, so at most one can finish allocating. Which process dies differs
        // by OS: Linux terminates the whole tree, while a Windows job refuses the child's commit and the parent may
        // still print before the tree is killed. So assert on the children, not on the parent's last line.
        int allocated = over.StdOut.Split(new[] { "allocated" }, StringSplitOptions.None).Length - 1;
        Assert.True(allocated < 2, $"both children allocated 80 MB under a 128 MB tree limit: {over.StdOut}");
    }

    [Fact]
    public async Task A_candidate_past_its_cpu_time_limit_is_terminated_and_reported()
    {
        var clock = Stopwatch.StartNew();
        ProgramExecuteResponse over = await Run("while True:\n    pass\n",
            limits => limits.CpuTimeLimitSeconds = 1, CpuCaseWallLimitSeconds);
        // The engine's own account (exit code, which limit it saw) makes a runner-specific failure diagnosable.
        Assert.True(over.ErrorCode == ProgramExecuteErrorCode.CpuTimeLimitExceeded,
            $"expected CpuTimeLimitExceeded, got {over.ErrorCode} after {clock.Elapsed.TotalSeconds:F1} s: {over.Error}");
        Assert.False(over.Success);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(CpuCaseWallLimitSeconds),
            "the wall-clock limit ended it, not the CPU-time limit");

        ProgramExecuteResponse under = await Run(
            "import time\nend = time.time() + 0.2\nwhile time.time() < end:\n    pass\nprint('done')\n",
            limits => limits.CpuTimeLimitSeconds = 5, CpuCaseWallLimitSeconds);
        Assert.True(under.Success, under.Error);
        Assert.Contains("done", under.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waiting_does_not_count_against_the_cpu_time_limit()
    {
        ProgramExecuteResponse sleeper = await Run(
            "import time\ntime.sleep(3)\nprint('rested')\n",
            limits => limits.CpuTimeLimitSeconds = 1, CpuCaseWallLimitSeconds);
        Assert.True(sleeper.Success, sleeper.Error);
        Assert.Contains("rested", sleeper.StdOut, StringComparison.Ordinal);
    }

    // These tests need a real interpreter. Without one every run is ExecutionFailed, which reads like a sandbox
    // defect; fail with the actual cause instead. Not a skip: CI provides Python, and a skip would hide lost coverage.
    private static readonly Lazy<string?> MissingInterpreter = new(() =>
    {
        try
        {
            using Process? probe = Process.Start(new ProcessStartInfo(Python, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (probe is null) return $"'{Python}' could not be started.";
            return probe.WaitForExit(30_000) && probe.ExitCode == 0 ? null : $"'{Python} --version' did not succeed.";
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return $"'{Python}' was not found ({exception.Message}).";
        }
    });

    /// <summary>
    /// The control arm for classifying the address-space backstop's kill as a memory violation: an
    /// ordinary failure must still be reported as one.
    /// </summary>
    /// <remarks>
    /// The backstop denies a mapping outright, which leaves no signal but the child's own report, so
    /// the engine reads standard error for an allocation failure. This pins the two ways that could
    /// go wrong: an unrelated crash must not become a memory violation, and neither must a candidate
    /// that merely prints the word - only a report on standard error counts, and only on a non-zero
    /// exit.
    /// </remarks>
    [Fact]
    public async Task An_ordinary_failure_is_not_reported_as_a_memory_violation()
    {
        ProgramExecuteResponse crashed = await Run(
            "raise ValueError('not about memory')\n", limits => limits.MemoryLimitMb = 256);
        Assert.False(crashed.Success);
        Assert.Equal(ProgramExecuteErrorCode.ExecutionFailed, crashed.ErrorCode);

        // The word on standard output, on a run that then fails for another reason.
        ProgramExecuteResponse printed = await Run(
            "print('MemoryError')\nraise ValueError('still not about memory')\n",
            limits => limits.MemoryLimitMb = 256);
        Assert.False(printed.Success);
        Assert.Equal(ProgramExecuteErrorCode.ExecutionFailed, printed.ErrorCode);

        // And a successful run that prints it is untouched.
        ProgramExecuteResponse fine = await Run("print('MemoryError')\n", limits => limits.MemoryLimitMb = 256);
        Assert.True(fine.Success, fine.Error);
        Assert.Null(fine.ErrorCode);
    }

    /// <summary>The wall limit the CPU-time cases run under, so only the limit under test can fire.</summary>
    /// <remarks>
    /// A busy shared runner gives a candidate a small CPU share, so one second of *CPU* can take far
    /// longer than that in wall time. At the 20 s default the wall limit sometimes won that race and
    /// the test failed reporting the wrong error code - the assertion was right and the setup was
    /// wrong. Raising the net for those cases removes the second limit without touching the first:
    /// the CPU-limit error code is still asserted exactly, and the run is still bounded.
    /// </remarks>
    private const int CpuCaseWallLimitSeconds = 120;

    private static async Task<ProgramExecuteResponse> Run(string source, Action<ProgramSandboxLimitOptions> configure,
        int wallLimitSeconds = 20)
    {
        if (MissingInterpreter.Value is { } missing)
            Assert.Fail("These tests need Python on PATH or in EVOLUTION_PYTHON: " + missing);
        var options = new ProgramSandboxOptions { AllowUnsandboxedExecution = true };
        options.SetInterpreter(ProgramLanguage.Python, new ProgramInterpreterSpecification(Python, "{source}"));
        options.Limits.TimeLimitSeconds = wallLimitSeconds;
        configure(options.Limits);
        using var engine = new ProcessProgramExecutionEngine(options);
        return await engine.ExecuteAsync(new ProgramExecuteRequest { Language = ProgramLanguage.Python, SourceCode = source });
    }

    private static string Quote(string text) =>
        "'" + text.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n") + "'";
}
