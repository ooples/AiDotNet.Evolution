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

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        TextWriter originalOut = Console.Out, originalError = Console.Error;
        Console.SetOut(output);
        Console.SetError(error);
        try { return (AiDotNet.Evolution.Cli.Program.Main(args), output.ToString(), error.ToString()); }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
    }

    private static string WriteRun(string directory, string endpoint, int maxEvaluations, string output = "out", string extra = "")
    {
        string path = Path.Combine(directory, "run.json");
        File.WriteAllText(path, $$"""
            {
              "schema": "aidotnet-evolve-run-v1",
              "runId": "cli-run",
              "initialProgram": "initial.py",
              "evaluator": "evaluator.py",
              "model": { "endpoint": "{{endpoint}}", "name": "fake-model", "timeoutSeconds": 20 },
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
    private sealed class FakeChatModel : IDisposable
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
