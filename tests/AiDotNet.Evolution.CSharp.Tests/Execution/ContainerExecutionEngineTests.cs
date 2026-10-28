using System.Diagnostics;
using System.Text.Json;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Execution;

/// <summary>The container engine isolates candidates; the unsandboxed engines refuse to start without an opt-in.</summary>
/// <remarks>One class, so the Docker tests run one after another and never compete for the runner's memory.</remarks>
public sealed class ContainerExecutionEngineTests
{
    private static ProgramSandboxOptions Sandbox(int timeLimitSeconds = 20, int memoryLimitMb = 256)
    {
        var options = new ProgramSandboxOptions { Mode = ProgramSandboxMode.Container };
        options.Limits.TimeLimitSeconds = timeLimitSeconds;
        options.Limits.MemoryLimitMb = memoryLimitMb;
        options.Limits.MaxConcurrentExecutions = 1;
        return options;
    }

    private static ContainerExecutionOptions Container(ContainerLifecycle lifecycle = ContainerLifecycle.Warm,
        ContainerPythonLaunch launch = ContainerPythonLaunch.ForkSupervisor) => new()
        {
            Image = DockerTestEnvironment.Image,
            Lifecycle = lifecycle,
            PythonLaunch = launch
        };

    private static Task<ProgramExecuteResponse> Run(IProgramExecutionEngine engine, string source, string? stdin = null) =>
        engine.ExecuteAsync(new ProgramExecuteRequest { Language = ProgramLanguage.Python, SourceCode = source, StdIn = stdin });

    [Fact]
    public void Docker_is_present_wherever_the_ci_lane_requires_it()
    {
        // The Linux lane sets the variable, so a runner without Docker fails here by name instead of skipping green.
        if (!DockerTestEnvironment.Required) return;
        Assert.True(DockerTestEnvironment.UnavailableReason is null,
            "AIDOTNET_EVOLUTION_REQUIRE_DOCKER=1 but " + DockerTestEnvironment.UnavailableReason);
    }

    [Fact]
    public void Every_unsandboxed_engine_refuses_without_the_explicit_opt_in()
    {
        ArgumentException process = Assert.Throws<ArgumentException>(() => new ProcessProgramExecutionEngine(new ProgramSandboxOptions()));
        Assert.Contains("AllowUnsandboxedExecution", process.Message, StringComparison.Ordinal);
        Assert.Contains("ContainerExecutionEngine", process.Message, StringComparison.Ordinal);

        foreach (ProgramSandboxMode warm in new[] { ProgramSandboxMode.WarmForkWorker, ProgramSandboxMode.WarmReusedWorker })
        {
            var options = new ProgramSandboxOptions { Mode = warm, AllowUnsafeInProcessExecution = true };
            ArgumentException refused = Assert.Throws<ArgumentException>(() => new WarmPythonExecutionEngine(options));
            Assert.Contains("AllowUnsandboxedExecution", refused.Message, StringComparison.Ordinal);
        }

        // The opt-in is what was missing: with it, the same default options start an engine.
        using var accepted = new ProcessProgramExecutionEngine(new ProgramSandboxOptions { AllowUnsandboxedExecution = true });
        Assert.False(new ProgramSandboxOptions().AllowUnsandboxedExecution);
        Assert.True(new ProgramSandboxOptions { AllowUnsandboxedExecution = true }.Clone().AllowUnsandboxedExecution);
    }

    [Fact]
    public void The_container_engine_refuses_other_modes_and_weakening_options_before_touching_docker()
    {
        var process = new ProgramSandboxOptions { AllowUnsandboxedExecution = true };
        Assert.Throws<ArgumentException>(() => new ContainerExecutionEngine(process, new ContainerExecutionOptions { Image = "python:3.12-slim" }));
        Assert.Throws<ArgumentException>(() => new ContainerExecutionEngine(Sandbox(), new ContainerExecutionOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerExecutionOptions { Image = "x", UserId = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerExecutionOptions { Image = "x", GroupId = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new ContainerExecutionOptions { Image = "x", SeccompProfilePath = "unconfined" }.Validate());
        Assert.Throws<ArgumentException>(() => new ContainerExecutionOptions { Image = "--privileged" }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerExecutionEngine(Sandbox(memoryLimitMb: 8), Container()));
    }

    [DockerFact]
    public void Every_container_starts_with_the_hardening_flags()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container());
        string arguments = string.Join(" ", engine.BuildRunArguments("probe"));
        foreach (string flag in new[]
                 {
                     "--network none", "--read-only", "--cap-drop ALL", "--security-opt no-new-privileges=true", "--ipc none",
                     "--pids-limit 64", "--memory 256m --memory-swap 256m", "--cpus 1", "--user 65534:65534",
                     "--tmpfs /work:rw,nosuid,nodev,noexec,size=64m,uid=65534,gid=65534,mode=0700"
                 })
        {
            Assert.Contains(flag, arguments, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("--privileged", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--volume", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--mount", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("unconfined", arguments, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", engine.ImageId, StringComparison.Ordinal);
    }

    [DockerTheory]
    [InlineData(ContainerPythonLaunch.ForkSupervisor)]
    [InlineData(ContainerPythonLaunch.FreshInterpreter)]
    public async Task A_candidate_reads_stdin_and_its_output_and_exit_code_come_back(ContainerPythonLaunch launch)
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container(launch: launch));
        ProgramExecuteResponse echo = await Run(engine, "import sys\nprint(sys.stdin.read().upper())\n", "hello");
        Assert.True(echo.Success, echo.Error + " " + echo.StdErr);
        Assert.Equal("HELLO", echo.StdOut.Trim());

        ProgramExecuteResponse failed = await Run(engine, "import sys\nprint('bad', file=sys.stderr)\nsys.exit(3)\n");
        Assert.False(failed.Success);
        Assert.Equal(3, failed.ExitCode);
        Assert.Equal(ProgramExecuteErrorCode.ExecutionFailed, failed.ErrorCode);
        Assert.Contains("bad", failed.StdErr, StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task A_candidate_cannot_reach_the_network()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container());
        const string Probe = "import os, socket\n" +
                             "print('interfaces', sorted(os.listdir('/sys/class/net')))\n" +
                             "try:\n" +
                             "    socket.create_connection(('1.1.1.1', 53), timeout=3)\n" +
                             "    print('tcp connected')\n" +
                             "except OSError:\n" +
                             "    print('tcp blocked')\n" +
                             "try:\n" +
                             "    socket.getaddrinfo('github.com', 443)\n" +
                             "    print('dns resolved')\n" +
                             "except OSError:\n" +
                             "    print('dns blocked')\n";
        ProgramExecuteResponse response = await Run(engine, Probe);
        Assert.True(response.Success, response.Error + " " + response.StdErr);
        Assert.Contains("interfaces ['lo']", response.StdOut, StringComparison.Ordinal);
        Assert.Contains("tcp blocked", response.StdOut, StringComparison.Ordinal);
        Assert.Contains("dns blocked", response.StdOut, StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task A_candidate_cannot_write_outside_its_workdir()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container());
        const string Probe = "import json\n" +
                             "result = {}\n" +
                             "for path in ['/probe', '/etc/probe', '/tmp/probe', '/var/tmp/probe', '/usr/probe', '/root/probe',\n" +
                             "             '/home/probe', '/dev/shm/probe', '/run/probe', '/work/probe']:\n" +
                             "    try:\n" +
                             "        with open(path, 'w') as f:\n" +
                             "            f.write('x')\n" +
                             "        result[path] = 'written'\n" +
                             "    except OSError:\n" +
                             "        result[path] = 'refused'\n" +
                             "print(json.dumps(result))\n";
        ProgramExecuteResponse response = await Run(engine, Probe);
        Assert.True(response.Success, response.Error + " " + response.StdErr);
        Dictionary<string, string> result = JsonSerializer.Deserialize<Dictionary<string, string>>(response.StdOut.Trim())
            ?? throw new InvalidOperationException("no result: " + response.StdOut);
        Assert.Equal("written", result["/work/probe"]);
        Assert.All(result.Where(entry => entry.Key != "/work/probe"), entry => Assert.Equal("refused", entry.Value));
    }

    [DockerFact]
    public async Task A_candidate_is_killed_at_its_memory_cap_and_the_next_one_still_runs()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(timeLimitSeconds: 30, memoryLimitMb: 128), Container());
        // Grows to 1 GB at most, so a missing cap fails the test with "survived" instead of exhausting the runner.
        const string Hog = "chunks = []\n" +
                           "for _ in range(128):\n" +
                           "    chunks.append(b'x' * (8 << 20))\n" +
                           "print('survived', len(chunks) * 8, 'MB')\n";
        ProgramExecuteResponse hog = await Run(engine, Hog);
        Assert.False(hog.Success, "the candidate " + hog.StdOut);
        Assert.Equal(ProgramExecuteErrorCode.MemoryLimitExceeded, hog.ErrorCode);
        Assert.DoesNotContain("survived", hog.StdOut, StringComparison.Ordinal);

        ProgramExecuteResponse next = await Run(engine, "print(41 + 1)\n");
        Assert.True(next.Success, next.Error + " " + next.StdErr);
        Assert.Equal("42", next.StdOut.Trim());
    }

    [DockerFact]
    public async Task A_warm_container_gives_every_candidate_a_clean_workdir_and_no_leftover_process()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container(ContainerLifecycle.Warm));
        // Leaves a file, a directory it made unreadable, and a background process that would recreate the file.
        const string Litter = "import os, socket, time\n" +
                              "open('/work/left-behind', 'w').write('x')\n" +
                              "os.makedirs('/work/locked/inner')\n" +
                              "open('/work/locked/inner/file', 'w').write('x')\n" +
                              "os.chmod('/work/locked', 0)\n" +
                              "if os.fork() == 0:\n" +
                              "    os.setsid()\n" +
                              "    time.sleep(5)\n" +
                              "    open('/work/from-orphan', 'w').write('x')\n" +
                              "    os._exit(0)\n" +
                              "print(socket.gethostname())\n";
        const string Inspect = "import json, os, socket, time\n" +
                               "time.sleep(6)\n" +
                               "print(json.dumps({'host': socket.gethostname(), 'files': sorted(os.listdir('/work')),\n" +
                               "                  'pids': sorted(int(p) for p in os.listdir('/proc') if p.isdigit()), 'self': os.getpid()}))\n";
        ProgramExecuteResponse first = await Run(engine, Litter);
        Assert.True(first.Success, first.Error + " " + first.StdErr);
        ProgramExecuteResponse second = await Run(engine, Inspect);
        Assert.True(second.Success, second.Error + " " + second.StdErr);

        using JsonDocument seen = JsonDocument.Parse(second.StdOut.Trim());
        // The same container ran both, so the clean workdir was cleaned, not merely new.
        Assert.Equal(first.StdOut.Trim(), seen.RootElement.GetProperty("host").GetString());
        Assert.Equal(new[] { "candidate.py" }, seen.RootElement.GetProperty("files").EnumerateArray().Select(file => file.GetString()).ToArray());
        int self = seen.RootElement.GetProperty("self").GetInt32();
        Assert.Equal(new[] { 1, self }, seen.RootElement.GetProperty("pids").EnumerateArray().Select(pid => pid.GetInt32()).ToArray());
    }

    [DockerFact]
    public async Task A_hung_candidate_times_out_and_the_container_keeps_serving()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(timeLimitSeconds: 2), Container());
        var clock = Stopwatch.StartNew();
        ProgramExecuteResponse hung = await Run(engine, "while True:\n    pass\n");
        Assert.Equal(ProgramExecuteErrorCode.TimeoutOrCanceled, hung.ErrorCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "the timeout took " + clock.Elapsed);

        ProgramExecuteResponse next = await Run(engine, "print(41 + 1)\n");
        Assert.True(next.Success, next.Error + " " + next.StdErr);
        Assert.Equal("42", next.StdOut.Trim());
    }

    [DockerFact]
    public async Task A_cold_engine_uses_a_fresh_container_per_candidate_and_leaves_none_behind()
    {
        string first, second;
        using (var engine = new ContainerExecutionEngine(Sandbox(), Container(ContainerLifecycle.Cold)))
        {
            const string Host = "import socket\nprint(socket.gethostname())\n";
            ProgramExecuteResponse one = await Run(engine, Host);
            ProgramExecuteResponse two = await Run(engine, Host);
            Assert.True(one.Success && two.Success, one.Error + " " + two.Error);
            first = one.StdOut.Trim();
            second = two.StdOut.Trim();
            Assert.Equal(0, engine.LiveContainerCount);
        }

        Assert.NotEqual(first, second);
        // No container this suite started is left on the daemon (the class runs its tests one at a time).
        (int ExitCode, string StdOut)? listed = DockerTestEnvironment.Docker(
            "ps", "--all", "--quiet", "--filter", "label=org.aidotnet.evolution.container");
        Assert.NotNull(listed);
        Assert.Equal(string.Empty, listed.Value.StdOut.Trim());
    }
}
