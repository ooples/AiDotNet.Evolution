using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        // The mounts share the docker command line with the supervisor script, under Windows' 32,767-character limit.
        var mounts = new ContainerExecutionOptions { Image = "x" };
        string source = Path.GetPathRoot(Path.GetTempPath()) + new string('d', 200);
        for (int index = 0; index < 15; index++) mounts.ReadOnlyMounts["/opt/m" + index] = source;
        mounts.Validate();
        for (int index = 15; index < 20; index++) mounts.ReadOnlyMounts["/opt/m" + index] = source;
        ArgumentException tooLong = Assert.Throws<ArgumentException>(() => mounts.Validate());
        Assert.Equal(nameof(ContainerExecutionOptions.ReadOnlyMounts), tooLong.ParamName);
    }

    [Fact]
    public void A_token_canceled_before_the_call_is_a_failed_execution_not_an_exception()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.False(engine.TryExecute(ProgramLanguage.Python, "print(1)\n", string.Empty, out string output, out string? error,
            canceled.Token));
        Assert.Equal(string.Empty, output);
        Assert.Equal("Execution was canceled.", error);
    }

    // The supervisor's memory flag alone decides a memory failure: exit 0 under an OOM event is still a failure, and
    // exit 125 without the flag (a candidate that chose 125) is an ordinary failed execution.
    [Fact]
    public void A_memory_event_fails_the_execution_and_exit_code_125_alone_is_not_one()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container());
        ProgramExecuteResponse oom = engine.FromReply(JsonNode.Parse("""{"exit": 0, "stdout": "42", "memory_exceeded": true}""")!,
            ProgramLanguage.Python);
        Assert.False(oom.Success);
        Assert.Equal(ProgramExecuteErrorCode.MemoryLimitExceeded, oom.ErrorCode);

        ProgramExecuteResponse chosen = engine.FromReply(JsonNode.Parse("""{"exit": 125, "memory_exceeded": false}""")!,
            ProgramLanguage.JavaScript);
        Assert.False(chosen.Success);
        Assert.Equal(125, chosen.ExitCode);
        Assert.Equal(ProgramExecuteErrorCode.ExecutionFailed, chosen.ErrorCode);
    }

    [Fact]
    public void Every_container_knob_is_range_checked_and_survives_a_clone()
    {
        Assert.Throws<ArgumentException>(() => new ContainerExecutionOptions { Image = "x", DockerExecutable = " " }.Validate());
        Assert.Throws<ArgumentException>(() => new ContainerExecutionOptions { Image = "x", SupervisorPython = "python3 -S" }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerExecutionOptions { Image = "x", RecycleAfter = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContainerExecutionOptions { Image = "x", RecycleAfter = ContainerExecutionOptions.MaxRecycleAfter + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerExecutionOptions { Image = "x", PidsLimit = 7 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerExecutionOptions { Image = "x", WorkspaceSizeMb = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerExecutionOptions { Image = "x", StartupTimeoutSeconds = 0 }.Validate());
        string hostDirectory = Path.GetFullPath(Path.GetTempPath());
        foreach (string target in new[] { "relative", "/", "/work", "/work/x", "/proc", "/sys/fs", "/dev", "/a/../work", "/a//b", "/a/", "/a b", "/a,b" })
        {
            var mounted = new ContainerExecutionOptions { Image = "x" };
            mounted.ReadOnlyMounts[target] = hostDirectory;
            Assert.Throws<ArgumentException>(() => mounted.Validate());
        }

        foreach (string source in new[] { "", "relative/dir", hostDirectory + ",readonly=false", hostDirectory + "\"x" })
        {
            var mounted = new ContainerExecutionOptions { Image = "x" };
            mounted.ReadOnlyMounts["/opt/evaluator"] = source;
            Assert.Throws<ArgumentException>(() => mounted.Validate());
        }

        var copy = new ContainerExecutionOptions
        {
            Image = "x",
            DockerExecutable = "/usr/bin/docker",
            RecycleAfter = 7,
            PidsLimit = 32,
            WorkspaceSizeMb = 16,
            SupervisorPython = "/usr/local/bin/python3",
            StartupTimeoutSeconds = 5
        };
        copy.ReadOnlyMounts["/opt/evaluator"] = hostDirectory;
        copy = copy.Clone();
        copy.Validate();
        Assert.Equal(hostDirectory, Assert.Single(copy.ReadOnlyMounts, pair => pair.Key == "/opt/evaluator").Value);
        Assert.Equal("/usr/bin/docker", copy.DockerExecutable);
        Assert.Equal(7, copy.RecycleAfter);
        Assert.Equal(32, copy.PidsLimit);
        Assert.Equal(16, copy.WorkspaceSizeMb);
        Assert.Equal("/usr/local/bin/python3", copy.SupervisorPython);
        Assert.Equal(5, copy.StartupTimeoutSeconds);
    }

    [DockerFact]
    public void Every_container_starts_with_the_hardening_flags()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container());
        string arguments = string.Join(" ", engine.BuildRunArguments("probe"));
        ContainerExecutionOptions mounting = Container();
        string hostDirectory = Path.GetFullPath(Path.GetTempPath()).TrimEnd('/');
        mounting.ReadOnlyMounts["/opt/evaluator"] = hostDirectory;
        using (var mounted = new ContainerExecutionEngine(Sandbox(), mounting))
        {
            Assert.Contains("--mount type=bind,source=" + hostDirectory + ",target=/opt/evaluator,readonly",
                string.Join(" ", mounted.BuildRunArguments("probe")), StringComparison.Ordinal);
            // What a candidate can read is part of what a result means.
            Assert.NotEqual(engine.VersionHash, mounted.VersionHash);
        }

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
    public async Task A_declared_host_directory_is_readable_and_cannot_be_written()
    {
        string host = Path.Combine(Path.GetTempPath(), "aidotnet-container-mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(host);
        try
        {
            File.WriteAllText(Path.Combine(host, "helper.txt"), "from the host");
            // The container runs as nobody, so the directory must be readable by others whatever the runner's umask is.
            if (!OperatingSystem.IsWindows())
            {
                // World-writable, so the read-only mount is the only thing that refuses the writes below.
                File.SetUnixFileMode(host, (UnixFileMode)0b111_111_111);
                File.SetUnixFileMode(Path.Combine(host, "helper.txt"), (UnixFileMode)0b110_110_110);
            }

            ContainerExecutionOptions container = Container();
            container.ReadOnlyMounts["/opt/evaluator"] = host;
            using var engine = new ContainerExecutionEngine(Sandbox(), container);
            const string Probe = "import json, os\n" +
                                 "result = {'read': open('/opt/evaluator/helper.txt').read()}\n" +
                                 "for path in ['/opt/evaluator/helper.txt', '/opt/evaluator/new.txt']:\n" +
                                 "    try:\n" +
                                 "        with open(path, 'a') as f:\n" +
                                 "            f.write('x')\n" +
                                 "        result[path] = 'written'\n" +
                                 "    except OSError:\n" +
                                 "        result[path] = 'refused'\n" +
                                 "print(json.dumps(result))\n";
            ProgramExecuteResponse response = await Run(engine, Probe);
            Assert.True(response.Success, response.Error + " " + response.StdErr);
            Dictionary<string, string> result = JsonSerializer.Deserialize<Dictionary<string, string>>(response.StdOut.Trim())
                ?? throw new InvalidOperationException("no result: " + response.StdOut);
            Assert.Equal("from the host", result["read"]);
            Assert.Equal("refused", result["/opt/evaluator/helper.txt"]);
            Assert.Equal("refused", result["/opt/evaluator/new.txt"]);
            Assert.Equal("from the host", File.ReadAllText(Path.Combine(host, "helper.txt")));
            Assert.False(File.Exists(Path.Combine(host, "new.txt")));
        }
        finally
        {
            Directory.Delete(host, recursive: true);
        }
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
                               "                  'pids': sorted(int(p) for p in os.listdir('/proc') if p.isdigit()), 'self': os.getpid(),\n" +
                               "                  'zygote': os.getppid()}))\n";
        ProgramExecuteResponse first = await Run(engine, Litter);
        Assert.True(first.Success, first.Error + " " + first.StdErr);
        ProgramExecuteResponse second = await Run(engine, Inspect);
        Assert.True(second.Success, second.Error + " " + second.StdErr);

        using JsonDocument seen = JsonDocument.Parse(second.StdOut.Trim());
        // The same container ran both, so the clean workdir was cleaned, not merely new.
        Assert.Equal(first.StdOut.Trim(), seen.RootElement.GetProperty("host").GetString());
        Assert.Equal(new[] { "candidate.py" }, seen.RootElement.GetProperty("files").EnumerateArray().Select(file => file.GetString()).ToArray());
        int self = seen.RootElement.GetProperty("self").GetInt32();
        int zygote = seen.RootElement.GetProperty("zygote").GetInt32();
        // The supervisor, the zygote that forked this candidate, and the candidate; nothing the first one started.
        Assert.Equal(new[] { 1, zygote, self }.OrderBy(pid => pid).ToArray(),
            seen.RootElement.GetProperty("pids").EnumerateArray().Select(pid => pid.GetInt32()).ToArray());
    }

    [DockerFact]
    public async Task A_warm_candidate_finds_nothing_of_an_earlier_candidate_in_its_memory()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container(ContainerLifecycle.Warm));
        // The marker is spelled out at run time, so neither source holds it; the scan's own needle is the one copy.
        const string Marker = "chr(72) + 'IDDEN-TEST-INPUT-' + chr(55) + 'f3a91'";
        ProgramExecuteResponse first = await Run(engine, "import sys\nprint(len(sys.stdin.read()))\n",
            string.Concat(Enumerable.Repeat("HIDDEN-TEST-INPUT-7f3a91 ", 2700)));
        Assert.True(first.Success, first.Error + " " + first.StdErr);
        string scan = "import ctypes, re\n" +
                      "needle = (" + Marker + ").encode()\n" +
                      "hits = 0\n" +
                      "for line in open('/proc/self/maps').read().splitlines():\n" +
                      "    m = re.match(r'([0-9a-f]+)-([0-9a-f]+) rw-p 00000000 00:00 0\\s*(\\[heap\\])?\\s*$', line)\n" +
                      "    if m:\n" +
                      "        lo, hi = int(m.group(1), 16), int(m.group(2), 16)\n" +
                      "        hits += ctypes.string_at(lo, hi - lo).count(needle)\n" +
                      "print(hits)\n";
        ProgramExecuteResponse second = await Run(engine, scan);
        Assert.True(second.Success, second.Error + " " + second.StdErr);
        // A candidate forked from the supervisor inherits its heap, which held the first request: hundreds of copies.
        Assert.Equal("1", second.StdOut.Trim());
    }

    [DockerFact]
    public async Task A_candidate_that_kills_stops_or_limits_its_zygote_does_not_cost_the_warm_container()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container(ContainerLifecycle.Warm));
        const string Host = "import socket\nprint(socket.gethostname())\n";
        ProgramExecuteResponse before = await Run(engine, Host);
        Assert.True(before.Success, before.Error + " " + before.StdErr);
        string[] attacks =
        {
            "import os, signal\nos.kill(os.getppid(), signal.SIGKILL)\n",
            "import os, signal\nos.kill(os.getppid(), signal.SIGSTOP)\n",
            "import os, resource\nresource.prlimit(os.getppid(), resource.RLIMIT_AS, (50 << 20, 50 << 20))\n"
        };
        foreach (string attack in attacks)
        {
            var clock = Stopwatch.StartNew();
            await Run(engine, attack);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), attack + " took " + clock.Elapsed);
            // Allocates past the limit the last attack set, so a zygote that kept it fails here.
            ProgramExecuteResponse next = await Run(engine, "x = bytearray(100 << 20)\nimport socket\nprint(socket.gethostname())\n");
            Assert.True(next.Success, attack + ": " + next.Error + " " + next.StdErr);
            // The same container: the supervisor recovered by itself and reported clean, so it was not replaced.
            Assert.Equal(before.StdOut.Trim(), next.StdOut.Trim());
        }
    }

    [DockerTheory]
    [InlineData("import resource\nresource.prlimit(1, resource.RLIMIT_NOFILE, (64, 64))\n")]
    [InlineData("import os\nos.sched_setaffinity(1, {0})\n")]
    public async Task A_candidate_that_changes_the_supervisors_limits_or_affinity_costs_it_the_container(string attack)
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container(ContainerLifecycle.Warm));
        const string Host = "import socket\nprint(socket.gethostname())\n";
        ProgramExecuteResponse before = await Run(engine, Host);
        Assert.True(before.Success, before.Error + " " + before.StdErr);
        // Same user as the supervisor, so the kernel allows it; a zygote started afterwards would inherit the change.
        ProgramExecuteResponse tamper = await Run(engine, attack);
        Assert.True(tamper.Success, tamper.Error + " " + tamper.StdErr);
        ProgramExecuteResponse after = await Run(engine, Host);
        Assert.True(after.Success, after.Error + " " + after.StdErr);
        Assert.NotEqual(before.StdOut.Trim(), after.StdOut.Trim());
    }
    [DockerFact]
    public async Task A_hung_candidate_times_out_and_the_container_keeps_serving()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(timeLimitSeconds: 2), Container());
        var clock = Stopwatch.StartNew();
        // Sleeps rather than spins: a spinning loop spends its derived CPU-time budget (also 2 s) at the same instant and
        // is reported, correctly, as CpuTimeLimitExceeded. This one can only be stopped by the wall clock.
        ProgramExecuteResponse hung = await Run(engine, "import time\nwhile True:\n    time.sleep(1)\n");
        Assert.Equal(ProgramExecuteErrorCode.TimeoutOrCanceled, hung.ErrorCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "the timeout took " + clock.Elapsed);

        ProgramExecuteResponse next = await Run(engine, "print(41 + 1)\n");
        Assert.True(next.Success, next.Error + " " + next.StdErr);
        Assert.Equal("42", next.StdOut.Trim());
    }

    // 125 is the forked-Python MemoryError sentinel; an exec'd interpreter that exits 125 did not run out of memory.
    [DockerFact]
    public async Task An_execd_candidate_that_exits_125_is_not_reported_as_out_of_memory()
    {
        using var engine = new ContainerExecutionEngine(Sandbox(), Container(launch: ContainerPythonLaunch.FreshInterpreter));
        ProgramExecuteResponse response = await Run(engine, "import sys\nsys.exit(125)\n");
        Assert.False(response.Success);
        Assert.Equal(125, response.ExitCode);
        Assert.Equal(ProgramExecuteErrorCode.ExecutionFailed, response.ErrorCode);
    }

    // The cap counts UTF-16 units, as the process engine does: ten emoji are twenty units, so a cap of 10 keeps five
    // and never splits a surrogate pair.
    [DockerFact]
    public async Task The_output_cap_counts_utf16_units_and_never_splits_a_surrogate_pair()
    {
        ProgramSandboxOptions sandbox = Sandbox();
        sandbox.Limits.MaxStdOutChars = 10;
        using var engine = new ContainerExecutionEngine(sandbox, Container());
        ProgramExecuteResponse response = await Run(engine, "print('\\U0001F600' * 10, end='')\n");
        Assert.True(response.StdOutTruncated);
        Assert.Equal(string.Concat(Enumerable.Repeat("\U0001F600", 5)), response.StdOut);
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
        Assert.Equal(0, listed.Value.ExitCode);
        Assert.Equal(string.Empty, listed.Value.StdOut.Trim());
    }
}
