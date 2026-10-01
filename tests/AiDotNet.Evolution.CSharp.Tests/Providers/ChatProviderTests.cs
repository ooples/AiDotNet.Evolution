using System.Net;
using System.Text;
using System.Text.Json;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Providers;

/// <summary>V1-51: OpenEvolve model-option parity for OpenAI-compatible endpoints, without network access.</summary>
public sealed class OpenAiCompatibleChatClientTests
{
    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<(string Body, string? Authorization)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((body, request.Headers.Authorization?.ToString()));
            return responses[Math.Min(_index++, responses.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = "resolved-model",
            choices = new[] { new { message = new { role = "assistant", content = text } } },
            usage = new { prompt_tokens = 11, completion_tokens = 7 }
        }), Encoding.UTF8, "application/json")
    };

    private static OpenAiCompatibleChatClientOptions Options(string? key = null) => new()
    {
        Endpoint = new Uri("http://127.0.0.1:9/v1"),
        Model = "m",
        ApiKey = key,
        MaxRetries = 2,
        RetryDelay = TimeSpan.Zero
    };

    private static readonly ProgramChatMessage[] Messages = { ProgramChatMessage.System("sys"), ProgramChatMessage.User("hi") };

    // Answers only when the request is cancelled, like an endpoint that never responds.
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Timeout_ends_a_request_the_endpoint_never_answers()
    {
        OpenAiCompatibleChatClientOptions options = Options();
        options.Timeout = TimeSpan.FromMilliseconds(300);
        options.MaxRetries = 0;
        using var client = new OpenAiCompatibleChatClient(options, new SilentHandler());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetResponseAsync(Messages, new ProgramChatOptions()));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the request outlived its timeout: " + clock.Elapsed);
    }

    [Fact]
    public async Task MaxResponseBytes_refuses_a_larger_body_and_admits_one_within_it()
    {
        string large = new string('a', 4096);
        OpenAiCompatibleChatClientOptions bounded = Options();
        bounded.MaxResponseBytes = 1024;
        bounded.MaxRetries = 0;
        using (var client = new OpenAiCompatibleChatClient(bounded, new FakeHandler(_ => Ok(large))))
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(Messages, new ProgramChatOptions()));

        // Control: the same body is accepted when the bound allows it.
        OpenAiCompatibleChatClientOptions roomy = Options();
        roomy.MaxResponseBytes = 64 * 1024;
        using (var client = new OpenAiCompatibleChatClient(roomy, new FakeHandler(_ => Ok(large))))
            Assert.Equal(large, (await client.GetResponseAsync(Messages, new ProgramChatOptions())).Text);
    }

    [Fact]
    public async Task Every_openevolve_sampling_option_reaches_the_request()
    {
        var handler = new FakeHandler(_ => Ok("answer"));
        using var client = new OpenAiCompatibleChatClient(Options("secret"), handler);
        ProgramChatResponse response = await client.GetResponseAsync(Messages, new ProgramChatOptions
        {
            Temperature = 0.3,
            TopP = 0.9,
            MaxOutputTokens = 128,
            Seed = 42,
            ReasoningEffort = ProgramReasoningEffort.High
        });
        Assert.Equal("answer", response.Text);
        Assert.Equal(18, response.Usage?.TotalTokens);
        Assert.Equal("resolved-model", response.ModelId);
        using JsonDocument body = JsonDocument.Parse(handler.Requests.Single().Body);
        JsonElement root = body.RootElement;
        Assert.Equal("m", root.GetProperty("model").GetString());
        Assert.Equal(0.9, root.GetProperty("top_p").GetDouble());
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(42, root.GetProperty("seed").GetInt32());
        Assert.Equal(128, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Bearer secret", handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task Throttling_and_server_errors_are_retried_then_succeed()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)429), _ => new HttpResponseMessage(HttpStatusCode.BadGateway), _ => Ok("ok"));
        using var client = new OpenAiCompatibleChatClient(Options(), handler);
        Assert.Equal("ok", (await client.GetResponseAsync(Messages)).Text);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Null(request.Authorization));
    }

    [Fact]
    public async Task A_client_error_is_not_retried_and_exhausted_retries_surface()
    {
        var badRequest = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using (var client = new OpenAiCompatibleChatClient(Options(), badRequest))
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(Messages));
        Assert.Single(badRequest.Requests);

        var down = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using (var client = new OpenAiCompatibleChatClient(Options(), down))
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(Messages));
        Assert.Equal(3, down.Requests.Count);
    }

    [Fact]
    public void A_key_is_never_configured_for_plain_http_to_a_remote_host()
    {
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleChatClient(new OpenAiCompatibleChatClientOptions
        {
            Endpoint = new Uri("http://example.com/v1"),
            Model = "m",
            ApiKey = "secret"
        }));
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleChatClient(new OpenAiCompatibleChatClientOptions { Model = "m" }));
    }
}

/// <summary>V1-51: OpenEvolve's manual (human-in-the-loop) mode.</summary>
public sealed class ManualProgramChatClientTests : IDisposable
{
    private readonly string _queue = Path.Combine(Path.GetTempPath(), "manual-queue-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_queue)) Directory.Delete(_queue, recursive: true);
    }

    [Fact]
    public async Task A_prompt_is_queued_and_the_written_answer_returned()
    {
        var client = new ManualProgramChatClient(_queue, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(20));
        Task<ProgramChatResponse> pending = client.GetResponseAsync(new[] { ProgramChatMessage.User("improve this") },
            new ProgramChatOptions { Temperature = 0.5 });
        string prompt = Path.Combine(_queue, "000001.prompt.json");
        for (int i = 0; i < 200 && !File.Exists(prompt); i++) await Task.Delay(20);
        Assert.Contains("improve this", await File.ReadAllTextAsync(prompt));
        string staged = Path.Combine(_queue, "staging.txt");
        await File.WriteAllTextAsync(staged, "def solve(): return 1");
        File.Move(staged, Path.Combine(_queue, "000001.response.txt"));
        Assert.Equal("def solve(): return 1", (await pending).Text);
    }

    [Fact]
    public async Task An_unanswered_prompt_times_out_and_numbering_survives_a_restart()
    {
        var client = new ManualProgramChatClient(_queue, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetResponseAsync(new[] { ProgramChatMessage.User("a") }));
        var resumed = new ManualProgramChatClient(_queue, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<TimeoutException>(() => resumed.GetResponseAsync(new[] { ProgramChatMessage.User("b") }));
        Assert.True(File.Exists(Path.Combine(_queue, "000002.prompt.json")), "a resumed client must not overwrite prompt 1");
    }
}
