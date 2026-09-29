using System.Diagnostics;
using System.Globalization;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Execution;

/// <summary>
/// #205 review: a candidate that double-forks (fork, setsid, fork, parent exits) detaches a grandchild from its
/// process tree. On Linux the sandbox guardian is a child subreaper, so that grandchild stays in the tree the engine
/// measures and kills. Windows contains the same case through the job object; <c>os.fork</c> is POSIX-only.
/// </summary>
public sealed class DetachedDescendantContainmentTests
{
    private static readonly string Python = Environment.GetEnvironmentVariable("EVOLUTION_PYTHON") ?? "python3";

    // Detaches a grandchild that records its pid, runs `payload`, then sleeps; the candidate waits for the pid file.
    private static string DoubleFork(string pidFile, string payload, double candidateSeconds) =>
        "import os, sys, time\n" +
        "pid = os.fork()\n" +
        "if pid == 0:\n" +
        "    os.setsid()\n" +
        "    if os.fork() == 0:\n" +
        "        open(" + Quote(pidFile) + ", 'w').write(str(os.getpid()))\n" +
        payload +
        "        time.sleep(60)\n" +
        "        os._exit(0)\n" +
        "    os._exit(0)\n" +
        "os.waitpid(pid, 0)\n" +
        "for _ in range(200):\n" +
        "    if os.path.exists(" + Quote(pidFile) + "):\n" +
        "        break\n" +
        "    time.sleep(0.05)\n" +
        "print('detached')\n" +
        "time.sleep(" + candidateSeconds.ToString(CultureInfo.InvariantCulture) + ")\n";

    [Fact]
    public async Task A_double_forked_descendant_is_gone_when_the_run_ends()
    {
        if (!OperatingSystem.IsLinux()) return; // POSIX fork; Windows is covered by the job object.
        string directory = Directory.CreateTempSubdirectory("detached-").FullName;
        string pidFile = Path.Combine(directory, "grandchild.pid");
        try
        {
            ProgramExecuteResponse response = await Run(DoubleFork(pidFile, string.Empty, 0), limits => limits.MemoryLimitMb = 512);
            Assert.True(response.Success, response.Error);
            Assert.Contains("detached", response.StdOut, StringComparison.Ordinal);
            int grandchild = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);
            // Gone once the run has returned: no grace period is needed, but allow the kernel a moment to reap.
            var clock = Stopwatch.StartNew();
            while (IsRunning(grandchild) && clock.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(50);
            Assert.False(IsRunning(grandchild), $"the detached grandchild {grandchild} outlived its run");
        }
        finally
        {
            KillIfRunning(pidFile);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_double_forked_descendant_counts_against_the_memory_limit()
    {
        if (!OperatingSystem.IsLinux()) return; // POSIX fork; Windows is covered by the job object.
        string directory = Directory.CreateTempSubdirectory("detached-").FullName;
        string pidFile = Path.Combine(directory, "grandchild.pid");
        try
        {
            // The grandchild touches 160 MB and holds it while the candidate itself stays small and waits; under a 128 MB
            // limit the run must end as a memory violation, well before the candidate's own 15 s wait. 160 MB, not more:
            // the shell's ulimit -v backstop (twice the limit) also binds the grandchild, and an allocation that ran into it
            // would kill the grandchild between two polls and leave nothing over the limit to see.
            const string allocate =
                "        chunks = []\n" +
                "        for _ in range(16):\n" +
                "            chunks.append(b'\\x01' * (10 * 1024 * 1024))\n";
            var clock = Stopwatch.StartNew();
            ProgramExecuteResponse response = await Run(DoubleFork(pidFile, allocate, 15), limits => limits.MemoryLimitMb = 128);
            Assert.True(response.ErrorCode == ProgramExecuteErrorCode.MemoryLimitExceeded,
                $"expected MemoryLimitExceeded, got {response.ErrorCode} after {clock.Elapsed.TotalSeconds:F1} s: {response.Error}");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(12), "the memory limit did not end it: " + clock.Elapsed);
        }
        finally
        {
            KillIfRunning(pidFile);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<ProgramExecuteResponse> Run(string source, Action<ProgramSandboxLimitOptions> configure)
    {
        var options = new ProgramSandboxOptions();
        options.SetInterpreter(ProgramLanguage.Python, new ProgramInterpreterSpecification(Python, "{source}"));
        options.Limits.TimeLimitSeconds = 30;
        configure(options.Limits);
        using var engine = new ProcessProgramExecutionEngine(options);
        return await engine.ExecuteAsync(new ProgramExecuteRequest { Language = ProgramLanguage.Python, SourceCode = source });
    }

    // A zombie has exited; only a process that can still run counts.
    private static bool IsRunning(int pid)
    {
        string stat = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat";
        if (!File.Exists(stat)) return false;
        try
        {
            string text = File.ReadAllText(stat);
            string[] fields = text.Substring(text.LastIndexOf(')') + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 0 && fields[0] != "Z" && fields[0] != "X";
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void KillIfRunning(string pidFile)
    {
        if (!File.Exists(pidFile)) return;
        if (!int.TryParse(File.ReadAllText(pidFile).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) return;
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static string Quote(string text) => "'" + text.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}