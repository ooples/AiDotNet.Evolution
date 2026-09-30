#if NET8_0_OR_GREATER
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>V1-26: aidotnet-evolve run / resume against a loopback OpenAI-compatible model and a real Python evaluator.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class CliRunTests
{
    // The evaluator scores X; the fake model proposes X = 1, 2, 3, ... so progress is observable and exact.
    private const string Evaluator = """
        import json, re, sys

        def evaluate(source):
            match = re.search(r"^X = (\d+)$", source, re.MULTILINE)
            return {"quality": float(match.group(1)) if match else 0.0}

        print(json.dumps(evaluate(sys.stdin.read())))
        """;

    internal static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        TextWriter originalOut = Console.Out, originalError = Console.Error;
        Console.SetOut(output);
        Console.SetError(error);
        try { return (AiDotNet.Evolution.Cli.Program.Main(args), output.ToString(), error.ToString()); }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
    }

    private static string WriteRun(string directory, string endpoint, int maxEvaluations, string output = "out", string extra = "",
        int timeoutSeconds = 20)
    {
        string path = Path.Combine(directory, "run.json");
        File.WriteAllText(path, $$"""
            {
              "schema": "aidotnet-evolve-run-v1",
              "runId": "cli-run",
              "initialProgram": "initial.py",
              "evaluator": "evaluator.py",
              "model": { "endpoint": "{{endpoint}}", "name": "fake-model", "timeoutSeconds": {{timeoutSeconds}}, "maxRetries": 0 },
              "budget": { "maxEvaluations": {{maxEvaluations}}, "seed": 7, "evaluationTimeLimitSeconds": 30 },
              "output": "{{output}}"{{extra}}
            }
            """);
        return path;
    }

    [Fact]
    public void Run_then_resume_continues_the_same_run_and_never_overwrites()
    {
        using var directory = new TemporaryDirectory();
        using var model = new FakeChatModel();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);

        string runFile = WriteRun(directory.Path, model.Endpoint, maxEvaluations: 3);
        var (code, output, error) = Run("run", runFile);
        Assert.True(code == 0, error);
        double firstBest;
        using (JsonDocument first = JsonDocument.Parse(output))
        {
            Assert.False(first.RootElement.GetProperty("Resumed").GetBoolean());
            Assert.Equal(3, first.RootElement.GetProperty("CompletedEvaluations").GetInt64());
            firstBest = first.RootElement.GetProperty("BestQuality").GetDouble();
            Assert.Equal(2, first.RootElement.GetProperty("ModelUsage").GetProperty("ChatCalls").GetInt64()); // the seed needs no model call
            Assert.Equal(20, first.RootElement.GetProperty("ModelUsage").GetProperty("InputTokens").GetInt64());
        }
        Assert.Equal(2d, firstBest); // X = 0 seed, then the model's X = 1 and X = 2
        string outDir = Path.Combine(directory.Path, "out");
        Assert.Equal("X = 2", File.ReadAllText(Path.Combine(outDir, "best.py")).TrimEnd()); // the program extracted from the fence
        Assert.True(File.Exists(Path.Combine(outDir, "trace-000.jsonl")));

        Assert.Equal(2, Run("run", runFile).Code); // the checkpoint already exists: a run never overwrites

        WriteRun(directory.Path, model.Endpoint, maxEvaluations: 6);
        (code, output, error) = Run("resume", runFile);
        Assert.True(code == 0, error);
        using (JsonDocument resumed = JsonDocument.Parse(output))
        {
            Assert.True(resumed.RootElement.GetProperty("Resumed").GetBoolean());
            Assert.Equal(6, resumed.RootElement.GetProperty("CompletedEvaluations").GetInt64()); // counters continue, not restart
            Assert.True(resumed.RootElement.GetProperty("BestQuality").GetDouble() > firstBest);
        }
        Assert.Equal(5, model.Calls); // resume re-evaluated nothing: 2 + 3 new proposals
        Assert.True(File.Exists(Path.Combine(outDir, "trace-001.jsonl")));

        var (inspectCode, inspect, _) = Run("inspect", Path.Combine(outDir, "trace-001.jsonl"));
        Assert.Equal(0, inspectCode);
        using (JsonDocument summary = JsonDocument.Parse(inspect))
            Assert.NotEqual(JsonValueKind.Null, summary.RootElement.GetProperty("Best").ValueKind);
    }

    [Fact]
    public void Preflight_scores_the_seed_exactly_as_run_would_and_never_calls_the_model()
    {
        using var directory = new TemporaryDirectory();
        using var model = new FakeChatModel();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 4\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        string runFile = WriteRun(directory.Path, model.Endpoint, maxEvaluations: 2);

        var (code, output, error) = Run("preflight", runFile);
        Assert.True(code == 0, error);
        using (JsonDocument report = JsonDocument.Parse(output))
        {
            Assert.True(report.RootElement.GetProperty("Passed").GetBoolean());
            Assert.Equal("Completed", report.RootElement.GetProperty("SeedStatus").GetString());
            Assert.Equal(4d, report.RootElement.GetProperty("SeedQuality").GetDouble()); // the evaluator really ran
            Assert.Equal("run", report.RootElement.GetProperty("Next").GetString());
        }
        Assert.Equal(0, model.Calls);
        string outDir = Path.Combine(directory.Path, "out");
        Assert.Empty(Directory.GetFileSystemEntries(outDir).Select(Path.GetFileName)); // the write probe is removed; nothing is started

        Assert.Equal(0, Run("run", runFile).Code);
        (code, output, error) = Run("preflight", runFile);
        Assert.True(code == 0, error);
        using (JsonDocument report = JsonDocument.Parse(output))
            Assert.Equal("resume", report.RootElement.GetProperty("Next").GetString()); // a fresh run would now refuse
    }

    [Fact]
    public void Preflight_fails_a_seed_the_evaluator_rejects_before_any_model_call()
    {
        using var directory = new TemporaryDirectory();
        using var model = new FakeChatModel();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), "import sys\n\ndef evaluate(source):\n    raise ValueError(\"seed rejected\")\n\nevaluate(sys.stdin.read())\n");
        string runFile = WriteRun(directory.Path, model.Endpoint, maxEvaluations: 2);

        var (code, output, error) = Run("preflight", runFile);
        Assert.True(code == AiDotNet.Evolution.Cli.RunCommand.PreflightFailedExitCode, code + ": " + error);
        using (JsonDocument report = JsonDocument.Parse(output))
        {
            Assert.False(report.RootElement.GetProperty("Passed").GetBoolean());
            Assert.NotEqual("Completed", report.RootElement.GetProperty("SeedStatus").GetString());
        }
        Assert.Equal(0, model.Calls);
    }
    [Fact]
    public void Export_includes_the_winner_source_only_when_its_identity_is_the_winning_genome()
    {
        using var directory = new TemporaryDirectory();
        using var model = new FakeChatModel();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        string runFile = WriteRun(directory.Path, model.Endpoint, maxEvaluations: 3);
        Assert.Equal(0, Run("run", runFile).Code);
        string outDir = Path.Combine(directory.Path, "out");
        string trace = Path.Combine(outDir, "trace-000.jsonl");
        string best = Path.Combine(outDir, "best.py");

        string tampered = Path.Combine(directory.Path, "tampered.py");
        File.WriteAllText(tampered, File.ReadAllText(best) + "# edited\n");
        string refusedDir = Path.Combine(directory.Path, "refused");
        var (refusedCode, _, refusedError) = Run("export", trace, refusedDir, "--include-source", tampered);
        Assert.Equal(2, refusedCode);
        Assert.Contains("is not the winner's program", refusedError);
        Assert.False(Directory.Exists(refusedDir)); // nothing is written under the winner's evidence

        string exportDir = Path.Combine(directory.Path, "export");
        var (code, _, error) = Run("export", trace, exportDir, "--include-source", best);
        Assert.True(code == 0, error);
        Assert.Equal(File.ReadAllText(best), File.ReadAllText(Path.Combine(exportDir, "winner.py"))); // byte-exact
        using (JsonDocument winner = JsonDocument.Parse(File.ReadAllText(Path.Combine(exportDir, "winner.json"))))
        {
            JsonElement source = winner.RootElement.GetProperty("Source");
            Assert.Equal(winner.RootElement.GetProperty("GenomeId").GetString(), source.GetProperty("BoundTo").GetString());
            Assert.Equal("winner.py", source.GetProperty("File").GetString());
            Assert.Equal(64, source.GetProperty("Sha256").GetString()?.Length);
        }
        Assert.Equal(2, Run("export", trace, exportDir, "--include-source", best).Code); // exports never overwrite
        var (verifyCode, verified, verifyError) = Run("inspect-export", exportDir);
        Assert.True(verifyCode == 0, verifyError);
        using (JsonDocument check = JsonDocument.Parse(verified))
            Assert.True(check.RootElement.GetProperty("Valid").GetBoolean());
        File.AppendAllText(Path.Combine(exportDir, "winner.py"), "# changed in transit\n");
        var (tamperedCode, _, tamperedError) = Run("inspect-export", exportDir);
        Assert.Equal(2, tamperedCode);
        Assert.Contains("SHA-256", tamperedError);
    }
    [Fact]
    public void Export_refuses_a_winner_source_that_contains_a_configured_credential()
    {
        using var directory = new TemporaryDirectory();
        using var model = new FakeChatModel();
        const string variable = "AIDOTNET_EVOLVE_TEST_API_KEY";
        const string secret = "sk-test-0123456789abcdef";
        // The seed is the only evaluation, so the winner carries the key -- as a program an echoing model wrote would.
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n# " + secret + "\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        Assert.Equal(0, Run("run", WriteRun(directory.Path, model.Endpoint, maxEvaluations: 1)).Code);
        string outDir = Path.Combine(directory.Path, "out");
        string trace = Path.Combine(outDir, "trace-000.jsonl");

        Environment.SetEnvironmentVariable(variable, secret);
        try
        {
            string exportDir = Path.Combine(directory.Path, "export");
            var (code, output, error) = Run("export", trace, exportDir, "--include-source", Path.Combine(outDir, "best.py"));
            Assert.Equal(2, code);
            Assert.Contains(variable, error);                       // the variable is named...
            Assert.DoesNotContain(secret, error + output);          // ...its value never is
            Assert.False(Directory.Exists(exportDir));              // and nothing is written

            Assert.Equal(0, Run("export", trace, exportDir).Code);  // without the source the evidence carries no key
            Assert.Null(AiDotNet.Evolution.Cli.Program.FindCredential("X = 0"));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }
    [Fact]
    public async Task Inspect_reads_a_run_that_is_still_in_progress()
    {
        using var directory = new TemporaryDirectory();
        using var model = new FakeChatModel();
        using var reached = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        // Hold the second model call: by then the seed and the first proposal are scored and checkpointed.
        model.OnCall = call => { if (call == 2) { reached.Set(); release.Wait(TimeSpan.FromSeconds(60)); } };
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        string runFile = WriteRun(directory.Path, model.Endpoint, maxEvaluations: 3);

        using var interrupt = new AiDotNet.Evolution.Cli.RunInterrupt();
        var running = Task.Run(() => AiDotNet.Evolution.Cli.RunCommand.Execute(runFile, false, new StringWriter(), new StringWriter(), interrupt));
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(60)), "the run never reached its second model call");
            var (code, output, error) = Run("inspect", Path.Combine(directory.Path, "out", "trace-000.jsonl"));
            Assert.True(code == 0, error); // the writer shares the file, so a live trace is readable
            using JsonDocument summary = JsonDocument.Parse(output);
            Assert.True(summary.RootElement.GetProperty("Running").GetBoolean()); // reported as in progress
            Assert.NotEqual(JsonValueKind.Null, summary.RootElement.GetProperty("Best").ValueKind);
        }
        finally
        {
            release.Set();
            // Awaited before the directory is disposed, so a failed assertion above is not masked by a locked trace.
            Assert.Equal(0, await running);
        }
        var (_, after, _) = Run("inspect", Path.Combine(directory.Path, "out", "trace-000.jsonl"));
        using (JsonDocument finished = JsonDocument.Parse(after))
            Assert.False(finished.RootElement.GetProperty("Running").GetBoolean()); // the marker goes with the run
    }
    [Fact]
    public async Task Watch_keeps_a_self_refreshing_report_current_until_the_run_ends()
    {
        using var directory = new TemporaryDirectory();
        using var model = new FakeChatModel();
        using var reached = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        model.OnCall = call => { if (call == 2) { reached.Set(); release.Wait(TimeSpan.FromSeconds(60)); } };
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        string runFile = WriteRun(directory.Path, model.Endpoint, maxEvaluations: 3);
        string trace = Path.Combine(directory.Path, "out", "trace-000.jsonl");
        string page = Path.Combine(directory.Path, "live.html");

        using var interrupt = new AiDotNet.Evolution.Cli.RunInterrupt();
        var running = Task.Run(() => AiDotNet.Evolution.Cli.RunCommand.Execute(runFile, false, new StringWriter(), new StringWriter(), interrupt));
        Task<int> watching = Task.FromResult(0);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(60)), "the run never reached its second model call");
            watching = Task.Run(() => AiDotNet.Evolution.Cli.Program.Watch(trace, page, 1, CancellationToken.None));
            for (int i = 0; i < 200 && !File.Exists(page); i++) await Task.Delay(50);
            string live = await File.ReadAllTextAsync(page);
            Assert.Contains("http-equiv=\"refresh\"", live);
            Assert.Contains("<strong>live</strong>", live);
            Assert.DoesNotContain("<script", live);
            Assert.DoesNotContain("http://", live.Replace("http-equiv", ""));
        }
        finally
        {
            release.Set();
            Assert.Equal(0, await running);
        }
        // Once the run's marker is gone, watch renders a final page without the refresh and exits.
        Assert.Equal(0, await watching.WaitAsync(TimeSpan.FromSeconds(60)));
        string final = await File.ReadAllTextAsync(page);
        Assert.DoesNotContain("http-equiv=\"refresh\"", final);
        Assert.Contains("3 evaluations", final);
    }
    [Theory]
    [InlineData(1, 0)]                                      // one press: stop at the batch boundary and report
    [InlineData(2, AiDotNet.Evolution.Cli.RunCommand.AbortedExitCode)] // two presses: abort, checkpoint still written
    public void An_interrupted_run_is_resumable_to_its_full_budget(int presses, int expectedCode)
    {
        using var directory = new TemporaryDirectory();
        using var model = new FakeChatModel();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        string runFile = WriteRun(directory.Path, model.Endpoint, maxEvaluations: 8);

        using (var interrupt = new AiDotNet.Evolution.Cli.RunInterrupt())
        {
            model.OnCall = n => { if (n == 2) for (int i = 0; i < presses; i++) interrupt.Press(); };
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(expectedCode, AiDotNet.Evolution.Cli.RunCommand.Execute(runFile, false, output, error, interrupt));
            if (presses == 1)
            {
                using JsonDocument stopped = JsonDocument.Parse(output.ToString());
                Assert.Equal("Canceled", stopped.RootElement.GetProperty("StopReason").GetString());
                Assert.InRange(stopped.RootElement.GetProperty("CompletedEvaluations").GetInt64(), 1, 7);
            }
            else
            {
                Assert.Contains("aidotnet-evolve resume", error.ToString());
            }
        }
        model.OnCall = null;

        var (code, resumed, stderr) = Run("resume", runFile);
        Assert.True(code == 0, stderr);
        using JsonDocument done = JsonDocument.Parse(resumed);
        Assert.Equal(8, done.RootElement.GetProperty("CompletedEvaluations").GetInt64());
        // The model proposes X = its call number, so the best is always the last call.
        Assert.Equal((double)model.Calls, done.RootElement.GetProperty("BestQuality").GetDouble());
        if (presses == 1) Assert.Equal(7, model.Calls); // a graceful stop commits its batch: no model call is wasted
        else Assert.True(model.Calls >= 7, "an abort rolls back its in-flight batch, whose proposals are made again");
    }

    [Fact]
    public void A_warm_start_seeds_a_new_run_with_the_earlier_runs_programs_and_reports_their_cost_separately()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        using var model = new FakeChatModel();
        foreach (string directory in new[] { first.Path, second.Path })
            File.WriteAllText(Path.Combine(directory, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(first.Path, "evaluator.py"), Evaluator);
        // The new run's evaluator differs: seeds are evaluated afresh, so an evaluator change is allowed.
        File.WriteAllText(Path.Combine(second.Path, "evaluator.py"), "# revised evaluator\n" + Evaluator);

        var (code, _, error) = Run("run", WriteRun(first.Path, model.Endpoint, maxEvaluations: 4));
        Assert.True(code == 0, error);
        string repertoire = Path.Combine(first.Path, "out", "repertoire-000.json");
        Assert.True(File.Exists(repertoire));

        string warm = WriteRun(second.Path, model.Endpoint, maxEvaluations: 2,
            extra: ",\n  \"warmStart\": \"" + repertoire.Replace('\\', '/') + "\"");
        (code, string output, error) = Run("run", warm);
        Assert.True(code == 0, error);
        using JsonDocument summary = JsonDocument.Parse(output);
        JsonElement warmStart = summary.RootElement.GetProperty("WarmStart");
        Assert.Equal(1, warmStart.GetProperty("Accepted").GetInt32()); // one length cell: the earlier best, X = 3
        Assert.Equal(0, warmStart.GetProperty("Rejected").GetInt32());
        Assert.Equal(4d, warmStart.GetProperty("PriorCostUnits").GetDouble()); // the earlier run's attempts, not re-charged here
        Assert.Equal("evaluation-attempts-v1", warmStart.GetProperty("CostUnit").GetString());
        Assert.Equal(3d, summary.RootElement.GetProperty("BestQuality").GetDouble()); // reached with no model call at all
        Assert.Equal(0, summary.RootElement.GetProperty("ModelUsage").GetProperty("ChatCalls").GetInt64());
        Assert.Equal(3, model.Calls); // only the first run's proposals
    }

    [Fact]
    public void A_tampered_warm_start_is_refused_before_any_work()
    {
        using var first = new TemporaryDirectory();
        using var model = new FakeChatModel();
        File.WriteAllText(Path.Combine(first.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(first.Path, "evaluator.py"), Evaluator);
        Assert.Equal(0, Run("run", WriteRun(first.Path, model.Endpoint, maxEvaluations: 2)).Code);
        string repertoire = Path.Combine(first.Path, "out", "repertoire-000.json");
        string tampered = Path.Combine(first.Path, "tampered.json");
        // Inside the checksummed payload (escaped JSON): the provenance's source run id.
        string quote = "\\u" + "0022"; // how the envelope escapes a quote inside its payload string
        string runId = quote + "SourceRunId" + quote + ":" + quote + "cli-ru";
        File.WriteAllText(tampered, File.ReadAllText(repertoire).Replace(runId + "n", runId + "x"));
        Assert.NotEqual(File.ReadAllText(repertoire), File.ReadAllText(tampered));
        int callsBefore = model.Calls;

        var (code, _, error) = Run("run", WriteRun(first.Path, model.Endpoint, maxEvaluations: 2, output: "out-2",
            extra: ",\n  \"warmStart\": \"tampered.json\""));
        Assert.Equal(2, code);
        Assert.StartsWith("error:", error);
        Assert.Equal(callsBefore, model.Calls);
        Assert.False(Directory.Exists(Path.Combine(first.Path, "out-2", "checkpoints")) &&
            Directory.EnumerateFileSystemEntries(Path.Combine(first.Path, "out-2", "checkpoints")).Any());
    }

    [Fact]
    public void A_model_endpoint_that_never_answers_is_an_error_not_a_result()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        var (code, output, error) = Run("run", WriteRun(directory.Path, $"http://127.0.0.1:{port}/v1", 3));
        Assert.Equal(AiDotNet.Evolution.Cli.RunCommand.ModelUnavailableExitCode, code);
        Assert.Contains("model calls failed", error);
        using JsonDocument summary = JsonDocument.Parse(output); // the summary is still reported
        Assert.Equal(summary.RootElement.GetProperty("ModelUsage").GetProperty("ChatCalls").GetInt64(),
            summary.RootElement.GetProperty("ModelUsage").GetProperty("ProviderErrors").GetInt64());
    }

    [Fact]
    public void A_model_endpoint_that_accepts_but_never_replies_is_an_error_not_a_result()
    {
        // HttpClient reports its own timeout as a cancellation; the run was not canceled, so it must count as a failed call.
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var (code, _, error) = Run("run", WriteRun(directory.Path, $"http://127.0.0.1:{port}/v1", 2, timeoutSeconds: 1));
            Assert.Equal(AiDotNet.Evolution.Cli.RunCommand.ModelUnavailableExitCode, code);
            Assert.Contains("model calls failed", error);
        }
        finally { silent.Stop(); }
    }

    [Fact]
    public void A_run_refuses_an_output_directory_another_live_process_is_using()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        string output = Path.Combine(directory.Path, "out");
        Directory.CreateDirectory(output);
        // Another run holds the directory's lock; a second run or resume would write the same checkpoints.
        var (code, _, error) = (0, "", "");
        using (new FileStream(Path.Combine(output, "running.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            (code, _, error) = Run("run", WriteRun(directory.Path, "http://127.0.0.1:9/v1", 2));
        Assert.Equal(2, code);
        Assert.Contains("in use by process", error);
        Assert.False(File.Exists(Path.Combine(output, "trace-000.jsonl")), "nothing may be written into a directory in use");
    }
    [Fact]
    public void A_stale_marker_without_a_held_lock_does_not_block_a_run()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        string output = Path.Combine(directory.Path, "out");
        Directory.CreateDirectory(output);
        // Left by a run that crashed: its lock died with it, and even a reused process id must not block the directory.
        File.WriteAllText(Path.Combine(output, "running.json"),
            $$"""{"RunId":"cli-run","ProcessId":{{Environment.ProcessId}},"Trace":"trace-000.jsonl","StartedUtc":"2026-01-01T00:00:00+00:00"}""");
        File.WriteAllText(Path.Combine(output, "running.lock"), string.Empty);
        var (code, _, error) = Run("run", WriteRun(directory.Path, "http://127.0.0.1:9/v1", 1));
        Assert.DoesNotContain("in use", error);
        Assert.Equal(0, code);
    }

    [Fact]
    public void An_interrupt_before_the_engine_starts_is_an_abort_not_a_crash()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        string runFile = WriteRun(directory.Path, "http://127.0.0.1:9/v1", 2);
        using var interrupt = new AiDotNet.Evolution.Cli.RunInterrupt();
        interrupt.Press(); // nothing is attached yet, so this cancels the token the setup calls observe
        var error = new StringWriter();
        int code = AiDotNet.Evolution.Cli.RunCommand.Execute(runFile, false, new StringWriter(), error, interrupt);
        Assert.Equal(AiDotNet.Evolution.Cli.RunCommand.AbortedExitCode, code);
        Assert.Contains("aborted", error.ToString());
    }

    [Theory]
    [InlineData("\"model\": null")]
    [InlineData("\"budget\": null")]
    public void A_null_model_or_budget_is_an_input_error_not_a_crash(string replacement)
    {
        using var directory = new TemporaryDirectory();
        string runFile = WriteRun(directory.Path, "http://127.0.0.1:9/v1", 2);
        string text = File.ReadAllText(runFile);
        string key = replacement.Split(':')[0];
        int start = text.IndexOf(key, StringComparison.Ordinal), end = text.IndexOf('}', start) + 1;
        File.WriteAllText(runFile, text[..start] + replacement + text[end..]);
        var (code, _, error) = Run("run", runFile);
        Assert.Equal(2, code);
        Assert.Contains("required", error);
    }

    [Fact]
    public void Preflight_fails_when_the_api_key_variable_is_unset()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        string runFile = WriteRun(directory.Path, "http://127.0.0.1:9/v1", 2);
        string variable = "AIDOTNET_EVOLVE_TEST_UNSET_" + Guid.NewGuid().ToString("N");
        File.WriteAllText(runFile, File.ReadAllText(runFile).Replace("\"name\": \"fake-model\",", $"\"name\": \"fake-model\", \"apiKeyEnvironmentVariable\": \"{variable}\","));
        var (code, _, error) = Run("preflight", runFile);
        Assert.Equal(2, code);
        Assert.Contains(variable, error);
    }

    [Theory]
    [InlineData("""{"Quality": 1.0}""")]
    [InlineData("""{"GenomeId": "g", "Quality": "high"}""")]
    [InlineData("""{"GenomeId": "g", "Quality": 1.0, "Source": {"File": "best.py", "Sha256": "x", "Language": "999", "BoundTo": "g"}}""")]
    [InlineData("""{"GenomeId": "g", "Quality": 1.0, "Source": {"File": "best.py"}}""")]
    public void Inspect_export_refuses_a_malformed_winner_with_exit_code_2(string winner)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "winner.json"), winner);
        File.WriteAllText(Path.Combine(directory.Path, "best.py"), "X = 1\n");
        var (code, _, error) = Run("inspect-export", directory.Path);
        Assert.Equal(2, code);
        Assert.StartsWith("error:", error.Trim());
    }
    [Theory]
    [InlineData("\"provider\": \"OpenAiCompatible\", \"name\": \"m\"", "model.endpoint")]
    [InlineData("\"provider\": \"Manual\", \"name\": \"m\"", "model.manualQueue")]
    [InlineData("\"provider\": \"ClaudeCode\", \"name\": \"m\", \"maxBudgetUsd\": 0", "model.maxBudgetUsd")]
    [InlineData("\"provider\": \"Ollama\", \"name\": \"m\"", "")]
    [InlineData("\"endpoint\": \"http://127.0.0.1:9/v1\", \"name\": \"m\", \"topP\": 1.5", "model.topP")]
    public void Each_provider_refuses_the_settings_it_cannot_run_with(string model, string mentioned)
    {
        using var directory = new TemporaryDirectory();
        string runFile = WriteRun(directory.Path, "http://127.0.0.1:9/v1", 2);
        string text = File.ReadAllText(runFile);
        int start = text.IndexOf("\"model\"", StringComparison.Ordinal), end = text.IndexOf('}', start) + 1;
        File.WriteAllText(runFile, text[..start] + "\"model\": { " + model + " }" + text[end..]);
        var (code, _, error) = Run("run", runFile);
        Assert.Equal(2, code);
        Assert.Contains(mentioned, error);
    }

    [Fact]
    public async Task The_manual_provider_runs_a_search_answered_by_a_person()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        string runFile = WriteRun(directory.Path, "http://127.0.0.1:9/v1", 2);
        string text = File.ReadAllText(runFile);
        int start = text.IndexOf("\"model\"", StringComparison.Ordinal), end = text.IndexOf('}', start) + 1;
        File.WriteAllText(runFile, text[..start] +
            "\"model\": { \"provider\": \"Manual\", \"name\": \"person\", \"manualQueue\": \"queue\", \"manualTimeoutSeconds\": 60 }" + text[end..]);
        string queue = Path.Combine(directory.Path, "queue");
        Task answering = Task.Run(async () =>
        {
            string prompt = Path.Combine(queue, "000001.prompt.json");
            for (int i = 0; i < 1200 && !File.Exists(prompt); i++) await Task.Delay(50);
            string staged = Path.Combine(queue, "answer.tmp");
            await File.WriteAllTextAsync(staged, "```python\nX = 5\n```\n");
            File.Move(staged, Path.Combine(queue, "000001.response.txt"));
        });
        var (code, output, error) = Run("run", runFile);
        await answering;
        Assert.True(code == 0, error);
        using JsonDocument summary = JsonDocument.Parse(output);
        Assert.Equal(2, summary.RootElement.GetProperty("CompletedEvaluations").GetInt64());
        Assert.Equal(1, summary.RootElement.GetProperty("ModelUsage").GetProperty("ChatCalls").GetInt64());
    }

    [Fact]
    public void Resume_without_a_checkpoint_and_malformed_run_files_fail_with_exit_code_2()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "initial.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), Evaluator);
        const string loopback = "http://127.0.0.1:9/v1";

        var (code, _, error) = Run("resume", WriteRun(directory.Path, loopback, 3));
        Assert.Equal(2, code);
        Assert.Contains("Nothing to resume", error);

        (code, _, error) = Run("run", WriteRun(directory.Path, loopback, 3, extra: ",\n  \"budgt\": {}"));
        Assert.Equal(2, code); // an unknown field is refused, not silently ignored

        (code, _, _) = Run("run", WriteRun(directory.Path, loopback, 3, extra: ",\n  \"direction\": 1"));
        Assert.Equal(2, code); // enums are names, never integers

        (code, _, error) = Run("run", WriteRun(directory.Path, "http://example.com/v1", 3));
        Assert.Equal(2, code);
        Assert.Contains("https", error); // an API key is never sent in clear text off-machine

        (code, _, _) = Run("run", WriteRun(directory.Path, loopback, 0));
        Assert.Equal(2, code);

        File.WriteAllText(Path.Combine(directory.Path, "run.json"), "{ \"schema\": \"aidotnet-evolve-run-v0\" }");
        Assert.Equal(2, Run("run", Path.Combine(directory.Path, "run.json")).Code);
    }

    /// <summary>A loopback OpenAI-compatible /v1/chat/completions endpoint proposing X = 1, 2, 3, ... in a python fence.</summary>
    internal sealed class FakeChatModel : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private int _calls;

        public FakeChatModel()
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
            Endpoint = $"http://localhost:{port}/v1";
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _loop = Task.Run(Serve);
        }

        public string Endpoint { get; }
        public int Calls => Volatile.Read(ref _calls);
        /// <summary>Runs with the call number before the response is sent.</summary>
        public Action<int>? OnCall { get; set; }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                try { await Respond(context); }
                // An aborted client (the second-interrupt test cancels mid-request) must not end the serve loop.
                catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
            }
        }

        private async Task Respond(HttpListenerContext context)
        {
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            using (JsonDocument request = JsonDocument.Parse(await reader.ReadToEndAsync()))
            {
                bool valid = context.Request.Url?.AbsolutePath == "/v1/chat/completions" &&
                    request.RootElement.GetProperty("model").GetString() == "fake-model" &&
                    request.RootElement.GetProperty("messages").GetArrayLength() > 0;
                int n = valid ? Interlocked.Increment(ref _calls) : 0;
                if (valid) OnCall?.Invoke(n);
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    model = "fake-model",
                    choices = new[] { new { message = new { role = "assistant", content = $"```python\nX = {n}\n```" } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 4 }
                });
                context.Response.StatusCode = valid ? 200 : 400;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }
}
#endif
