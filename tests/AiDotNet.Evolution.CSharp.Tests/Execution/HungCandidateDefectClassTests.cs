using System.Diagnostics;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Execution;

/// <summary>V1-83 (#185), defect class D6: OpenEvolve 0.3.2 leaves a timed-out candidate's processes running.</summary>
public sealed class HungCandidateDefectClassTests
{
    private static readonly string Python = Environment.GetEnvironmentVariable("EVOLUTION_PYTHON")
        ?? (OperatingSystem.IsWindows() ? "python" : "python3");

    [Fact]
    public async Task D6_a_hung_candidate_and_the_process_it_detached_are_gone_within_the_timeout_plus_one_second()
    {
        string directory = Path.Combine(Path.GetTempPath(), "d6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string pidFile = Path.Combine(directory, "grandchild.pid").Replace("\\", "\\\\");
        // The candidate detaches a grandchild (a new process group on Windows, a new session elsewhere) that would sleep
        // for a minute, records its id, then hangs itself.
        string source = "import subprocess, sys, time\n" +
                        "flags = 0x00000008 | 0x00000200 if sys.platform == 'win32' else 0\n" +
                        "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], creationflags=flags,\n" +
                        "                         start_new_session=sys.platform != 'win32')\n" +
                        "with open('" + pidFile + "', 'w') as f:\n    f.write(str(child.pid))\n" +
                        "time.sleep(60)\n";
        var options = new ProgramSandboxOptions();
        options.SetInterpreter(ProgramLanguage.Python, new ProgramInterpreterSpecification(Python, "{source}"));
        options.Limits.TimeLimitSeconds = 3;
        try
        {
            using var engine = new ProcessProgramExecutionEngine(options);
            var clock = Stopwatch.StartNew();
            ProgramExecuteResponse response = await engine.ExecuteAsync(new ProgramExecuteRequest { Language = ProgramLanguage.Python, SourceCode = source });
            Assert.Equal(ProgramExecuteErrorCode.TimeoutOrCanceled, response.ErrorCode);

            string pidPath = Path.Combine(directory, "grandchild.pid");
            Assert.True(File.Exists(pidPath), "the candidate never started its grandchild, so this test proved nothing");
            int grandchild = int.Parse(File.ReadAllText(pidPath).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            TimeSpan deadline = TimeSpan.FromSeconds(options.Limits.TimeLimitSeconds + 1);
            while (IsAlive(grandchild) && clock.Elapsed < deadline) await Task.Delay(50);
            bool survived = IsAlive(grandchild);
            if (survived) Process.GetProcessById(grandchild).Kill();
            Assert.False(survived, $"the detached grandchild was still running {clock.Elapsed.TotalSeconds:F1} s after the run started");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
    }
}
