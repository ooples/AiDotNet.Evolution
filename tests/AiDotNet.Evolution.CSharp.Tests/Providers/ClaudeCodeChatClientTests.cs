using System.Text.Json;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Providers;

/// <summary>V1-51: the Claude Code CLI provider, driven by a stub CLI so no login or network is needed.</summary>
public sealed class ClaudeCodeChatClientTests : IDisposable
{
    private static readonly string Python = Environment.GetEnvironmentVariable("EVOLUTION_PYTHON")
        ?? (OperatingSystem.IsWindows() ? "python" : "python3");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "claude-stub-" + Guid.NewGuid().ToString("N"));

    public ClaudeCodeChatClientTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    // Records its arguments, standard input and whether inherited credentials leaked, then answers like `claude -p --output-format json`.
    private ClaudeCodeChatClient Client(bool isolate, int failFirst = 0, decimal? totalBudgetUsd = null, decimal? reportedCostUsd = null)
    {
        string script = Path.Combine(_directory, "claude_stub.py");
        File.WriteAllText(script, """
            import json, os, sys
            record, counter = sys.argv[1], sys.argv[2]
            args = sys.argv[3:]
            calls = int(open(counter).read()) if os.path.exists(counter) else 0
            open(counter, "w").write(str(calls + 1))
            stdin = sys.stdin.read()
            json.dump({"args": args, "stdin": stdin, "leaked": "ANTHROPIC_API_KEY" in os.environ, "cwd_empty": sorted(os.listdir(".")) in ([], ["no-mcp.json"])}, open(record, "w"))
            if calls < int(os.environ.get("STUB_FAIL_FIRST", "0")):
                sys.exit(3)
            reply = {"type": "result", "subtype": "success", "is_error": False, "result": "print('hi')", "usage": {"input_tokens": 12, "output_tokens": 5}}
            if os.environ.get("STUB_COST"):
                reply["total_cost_usd"] = float(os.environ["STUB_COST"])
            print(json.dumps(reply))
            """);
        Environment.SetEnvironmentVariable("STUB_FAIL_FIRST", failFirst.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("STUB_COST", reportedCostUsd?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new ClaudeCodeChatClient(new ClaudeCodeChatClientOptions
        {
            Executable = Python,
            ExecutablePrefixArguments = { script, Path.Combine(_directory, "record.json"), Path.Combine(_directory, "count.txt") },
            Model = "sonnet",
            MaxBudgetUsd = 0.5m,
            TotalBudgetUsd = totalBudgetUsd,
            Isolate = isolate,
            MaxRetries = 2,
            RetryDelay = TimeSpan.Zero,
            Timeout = TimeSpan.FromSeconds(60)
        });
    }

    private JsonElement Record() => JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "record.json"))).RootElement;

    [Fact]
    public async Task The_whole_conversation_goes_on_stdin_and_the_system_prompt_as_a_flag()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "must-not-leak");
        try
        {
            ProgramChatResponse response = await Client(isolate: true).GetResponseAsync(new[]
            {
                ProgramChatMessage.System("be terse"), ProgramChatMessage.User("first"),
                ProgramChatMessage.Assistant("draft"), ProgramChatMessage.User("again")
            });
            Assert.Equal("print('hi')", response.Text);
            Assert.Equal(17, response.Usage?.TotalTokens);
            JsonElement record = Record();
            string[] args = record.GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? "").ToArray();
            Assert.Equal("be terse", args[Array.IndexOf(args, "--system-prompt") + 1]);
            Assert.Equal("0.5", args[Array.IndexOf(args, "--max-budget-usd") + 1]);
            Assert.Contains("--strict-mcp-config", args);
            // Every non-system turn, assistant included, which OpenEvolve's provider drops.
            string stdin = record.GetProperty("stdin").GetString() ?? "";
            Assert.Contains("draft", stdin);
            Assert.Contains("again", stdin);
            Assert.False(record.GetProperty("leaked").GetBoolean());
            Assert.True(record.GetProperty("cwd_empty").GetBoolean());
        }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null); }
    }

    [Fact]
    public async Task Without_isolation_the_profile_and_environment_are_left_alone()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "user-choice");
        try
        {
            await Client(isolate: false).GetResponseAsync(new[] { ProgramChatMessage.User("x") });
            JsonElement record = Record();
            Assert.True(record.GetProperty("leaked").GetBoolean());
            Assert.DoesNotContain("--strict-mcp-config", record.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null); }
    }

    [Fact]
    public async Task A_failed_call_is_retried_and_a_persistent_failure_surfaces()
    {
        Assert.Equal("print('hi')", (await Client(isolate: true, failFirst: 2).GetResponseAsync(new[] { ProgramChatMessage.User("x") })).Text);
        Assert.Equal("3", File.ReadAllText(Path.Combine(_directory, "count.txt")));
        File.Delete(Path.Combine(_directory, "count.txt"));
        await Assert.ThrowsAsync<IOException>(() => Client(isolate: true, failFirst: 5).GetResponseAsync(new[] { ProgramChatMessage.User("x") }));
    }

    private string BudgetArgument() =>
        Record().GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? "").SkipWhile(a => a != "--max-budget-usd").Skip(1).First();

    private int CallCount() => int.Parse(File.ReadAllText(Path.Combine(_directory, "count.txt")), System.Globalization.CultureInfo.InvariantCulture);

    // MaxBudgetUsd caps each `claude -p` process, and every attempt and retry is a new process, so a campaign of N calls
    // could spend N times the cap. A total budget is shared by every call: each one is given at most what remains,
    // charged what the CLI reports, and none starts once it is gone.
    [Fact]
    public async Task A_total_budget_shrinks_each_call_to_what_remains_and_stops_before_starting_another()
    {
        var client = Client(isolate: true, totalBudgetUsd: 1.0m, reportedCostUsd: 0.3m);
        var expectedCaps = new[] { "0.5", "0.5", "0.4", "0.1" };
        foreach (string cap in expectedCaps)
        {
            await client.GetResponseAsync(new[] { ProgramChatMessage.User("x") });
            Assert.Equal(cap, BudgetArgument());
        }
        // The CLI may overrun its cap by one turn, and the reported cost is what is charged.
        Assert.Equal(1.2m, client.SpentUsd);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(new[] { ProgramChatMessage.User("x") }));
        Assert.Contains("total budget", refused.Message, StringComparison.Ordinal);
        Assert.Equal(expectedCaps.Length, CallCount());
    }

    [Fact]
    public async Task A_call_that_reports_no_cost_is_charged_its_whole_cap_failed_retries_included()
    {
        // Two failed attempts and a success, none reporting a cost: each is charged its 0.5 cap.
        var client = Client(isolate: true, failFirst: 2, totalBudgetUsd: 1.6m);
        await client.GetResponseAsync(new[] { ProgramChatMessage.User("x") });
        Assert.Equal(3, CallCount());
        Assert.Equal(1.5m, client.SpentUsd);
        // 0.1 remains, so the next call is capped at it; after that nothing is left and no process starts.
        await client.GetResponseAsync(new[] { ProgramChatMessage.User("x") });
        Assert.Equal("0.1", BudgetArgument());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(new[] { ProgramChatMessage.User("x") }));
        Assert.Equal(4, CallCount());
    }

    [Fact]
    public void A_nonpositive_total_budget_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClaudeCodeChatClient(new ClaudeCodeChatClientOptions { TotalBudgetUsd = 0m }));
    }

    [Fact]
    public async Task A_json_response_request_is_carried_as_an_instruction_rather_than_refused()
    {
        // This provider used to throw NotSupportedException for a JSON request, which made the LLM judge
        // unusable with it at the judge's own defaults: LlmFeedbackOptions.RequestJsonResponseFormat is
        // true, so every judgement failed while the evaluation completed on its base quality - the
        // feedback signal was absent rather than the run being refused. The CLI has no response-format
        // flag, so the request is carried into the prompt the model reads instead.
        ProgramChatResponse response = await Client(isolate: true).GetResponseAsync(
            new[] { ProgramChatMessage.System("be terse"), ProgramChatMessage.User("score it") },
            new ProgramChatOptions { ResponseFormat = ProgramChatResponseFormat.Json });

        Assert.Equal("print('hi')", response.Text);
        JsonElement record = Record();
        string[] args = record.GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? "").ToArray();
        string system = args[Array.IndexOf(args, "--system-prompt") + 1];
        Assert.Contains("be terse", system, StringComparison.Ordinal);
        Assert.Contains(ClaudeCodeChatClient.JsonOnlyInstruction, system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_text_response_request_carries_no_json_instruction()
    {
        // The inverted half: if the instruction were appended unconditionally, the test above would pass
        // for a client that ignored the option entirely.
        await Client(isolate: true).GetResponseAsync(
            new[] { ProgramChatMessage.System("be terse"), ProgramChatMessage.User("write it") },
            new ProgramChatOptions { ResponseFormat = ProgramChatResponseFormat.Text });

        string[] args = Record().GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? "").ToArray();
        Assert.DoesNotContain(ClaudeCodeChatClient.JsonOnlyInstruction,
            args[Array.IndexOf(args, "--system-prompt") + 1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_limit_is_reported_as_itself_and_not_retried()
    {
        // Measured against the real CLI: a six-line prompt spent 484 thinking tokens, the CLI returned
        // subtype error_max_turns with no result field at all, and the client's generic
        // InvalidDataException put that through the transient-retry path - so a deterministic limit cost
        // MaxRetries+1 calls, each billed the same, before failing anyway.
        string script = Path.Combine(_directory, "turns_stub.py");
        File.WriteAllText(script, """
            import json, os, sys
            counter = sys.argv[1]
            calls = int(open(counter).read()) if os.path.exists(counter) else 0
            open(counter, "w").write(str(calls + 1))
            sys.stdin.read()
            print(json.dumps({"type": "result", "subtype": "error_max_turns", "is_error": True}))
            """);
        string counter = Path.Combine(_directory, "turns.txt");
        var client = new ClaudeCodeChatClient(new ClaudeCodeChatClientOptions
        {
            Executable = Python,
            ExecutablePrefixArguments = { script, counter },
            Model = "sonnet",
            MaxTurns = 3,
            MaxRetries = 4,
            RetryDelay = TimeSpan.Zero,
            Timeout = TimeSpan.FromSeconds(60)
        });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync(new[] { ProgramChatMessage.User("solve it") }));

        Assert.Contains("--max-turns limit of 3", error.Message, StringComparison.Ordinal);
        Assert.Contains("MaxTurns", error.Message, StringComparison.Ordinal);
        // The point of the change: one call, not five.
        Assert.Equal("1", File.ReadAllText(counter));
    }

    [Fact]
    public async Task The_turn_limit_is_configurable_and_passed_to_the_cli()
    {
        await Client(isolate: true).GetResponseAsync(new[] { ProgramChatMessage.User("hello") });
        string[] args = Record().GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? "").ToArray();

        // Default is above one, because a model spending a turn on reasoning is ordinary rather than an error.
        Assert.Equal("4", args[Array.IndexOf(args, "--max-turns") + 1]);
        Assert.Equal(4, new ClaudeCodeChatClientOptions().MaxTurns);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClaudeCodeChatClient(new ClaudeCodeChatClientOptions { Model = "sonnet", MaxTurns = 0 }));
    }
}
