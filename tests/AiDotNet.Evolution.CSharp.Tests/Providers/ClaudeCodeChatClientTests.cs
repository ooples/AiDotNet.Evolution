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
    private ClaudeCodeChatClient Client(bool isolate, int failFirst = 0)
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
            print(json.dumps({"type": "result", "subtype": "success", "is_error": False, "result": "print('hi')", "usage": {"input_tokens": 12, "output_tokens": 5}}))
            """);
        Environment.SetEnvironmentVariable("STUB_FAIL_FIRST", failFirst.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new ClaudeCodeChatClient(new ClaudeCodeChatClientOptions
        {
            Executable = Python,
            ExecutablePrefixArguments = { script, Path.Combine(_directory, "record.json"), Path.Combine(_directory, "count.txt") },
            Model = "sonnet",
            MaxBudgetUsd = 0.5m,
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
}
