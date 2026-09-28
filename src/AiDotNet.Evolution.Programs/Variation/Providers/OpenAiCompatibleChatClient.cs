using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AiDotNet.Evolution.Programs;

/// <summary>Settings for <see cref="OpenAiCompatibleChatClient"/>, mirroring OpenEvolve's per-model options.</summary>
public sealed class OpenAiCompatibleChatClientOptions
{
    /// <summary>Gets or sets the base URL, for example <c>https://api.openai.com/v1</c> (OpenEvolve's <c>api_base</c>).</summary>
    public Uri? Endpoint { get; set; }
    /// <summary>Gets or sets the model name sent with every request.</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>Gets or sets the bearer key, or <c>null</c> for an endpoint that needs none (OpenEvolve's <c>api_key</c>).</summary>
    public string? ApiKey { get; set; }
    /// <summary>Gets or sets the per-request timeout (OpenEvolve's <c>timeout</c>).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Gets or sets how many times a throttled, failed or timed-out request is retried (OpenEvolve's <c>retries</c>).</summary>
    public int MaxRetries { get; set; } = 3;
    /// <summary>Gets or sets the delay before the first retry; each further retry doubles it (OpenEvolve's <c>retry_delay</c>).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Gets or sets the largest response body accepted, in bytes.</summary>
    public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;

    internal void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri || Endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("Endpoint must be an absolute http(s) URL.", nameof(Endpoint));
        if (Endpoint.Scheme == "http" && !Endpoint.IsLoopback && ApiKey is not null)
            throw new ArgumentException("An API key is only sent over https, or to a loopback address.", nameof(Endpoint));
        if (string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("Model is required.", nameof(Model));
        if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (MaxRetries is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(MaxRetries));
        if (RetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        if (MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
    }
}

/// <summary>An <see cref="IProgramChatClient"/> for any OpenAI-compatible <c>chat/completions</c> endpoint.</summary>
/// <remarks>
/// Throttling (429), server errors (5xx) and timeouts are retried with a doubling delay; other failures are not, because
/// repeating them cannot succeed. A timeout that is not the caller's cancellation surfaces as <see cref="TimeoutException"/>
/// so the variation operator counts it as a failed call. Redirects are not followed, so a key is never sent elsewhere.
/// </remarks>
public sealed class OpenAiCompatibleChatClient : IProgramChatClient, IDisposable
{
    private readonly OpenAiCompatibleChatClientOptions _options;
    private readonly HttpClient _http;
    private readonly Uri _completions;

    /// <summary>Creates a client; the options are copied.</summary>
    public OpenAiCompatibleChatClient(OpenAiCompatibleChatClientOptions options)
        : this(options, new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    internal OpenAiCompatibleChatClient(OpenAiCompatibleChatClientOptions options, HttpMessageHandler handler)
    {
        ProgramGuard.NotNull(options);
        ProgramGuard.NotNull(handler);
        options.Validate();
        _options = new OpenAiCompatibleChatClientOptions
        {
            Endpoint = options.Endpoint,
            Model = options.Model.Trim(),
            ApiKey = options.ApiKey,
            Timeout = options.Timeout,
            MaxRetries = options.MaxRetries,
            RetryDelay = options.RetryDelay,
            MaxResponseBytes = options.MaxResponseBytes
        };
        string root = _options.Endpoint!.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? _options.Endpoint.AbsoluteUri : _options.Endpoint.AbsoluteUri + "/";
        _completions = new Uri(new Uri(root, UriKind.Absolute), "chat/completions");
        _http = new HttpClient(handler) { Timeout = _options.Timeout, MaxResponseContentBufferSize = _options.MaxResponseBytes };
        if (_options.ApiKey is { Length: > 0 } key) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    /// <inheritdoc/>
    public string ModelId => _options.Model;

    /// <inheritdoc/>
    public async Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ProgramGuard.NotNull(messages);
        byte[] body = BuildBody(messages, options);
        TimeSpan delay = _options.RetryDelay;
        for (int attempt = 0; ; attempt++)
        {
            bool retryable;
            Exception failure;
            try
            {
                using var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using HttpResponseMessage response = await _http.PostAsync(_completions, content, cancellationToken).ConfigureAwait(false);
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return Parse(bytes);
                retryable = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
                failure = new HttpRequestException("Model endpoint returned " + (int)response.StatusCode + ".");
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                retryable = true;
                failure = new TimeoutException("Model endpoint did not respond within " + _options.Timeout.TotalSeconds + " seconds.", exception);
            }
            catch (HttpRequestException exception)
            {
                retryable = true;
                failure = exception;
            }
            if (!retryable || attempt >= _options.MaxRetries) throw failure;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
        }
    }

    private byte[] BuildBody(IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options)
    {
        if (options?.ResponseFormat is ProgramChatResponseFormat.Json)
            throw new NotSupportedException("This client requests text completions only.");
        var body = new Dictionary<string, object>
        {
            ["model"] = _options.Model,
            ["messages"] = messages.Select(message => new Dictionary<string, string>
            {
                ["role"] = message.Role.ToString().ToLowerInvariant(),
                ["content"] = message.Text
            }).ToArray()
        };
        if (options?.Temperature is { } temperature) body["temperature"] = temperature;
        if (options?.TopP is { } topP) body["top_p"] = topP;
        if (options?.MaxOutputTokens is { } maxTokens) body["max_tokens"] = maxTokens;
        if (options?.Seed is { } seed) body["seed"] = seed;
        if (options?.ReasoningEffort is { } effort) body["reasoning_effort"] = effort.ToString().ToLowerInvariant();
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private static ProgramChatResponse Parse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out JsonElement message) || !message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Model response has no message content.");
        ProgramChatUsage? usage = null;
        if (root.TryGetProperty("usage", out JsonElement reported) && reported.ValueKind == JsonValueKind.Object &&
            reported.TryGetProperty("prompt_tokens", out JsonElement input) && input.TryGetInt32(out int inputTokens) &&
            reported.TryGetProperty("completion_tokens", out JsonElement output) && output.TryGetInt32(out int outputTokens))
            usage = new ProgramChatUsage(inputTokens, outputTokens);
        string? model = root.TryGetProperty("model", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        return new ProgramChatResponse(ProgramChatMessage.Assistant(content.GetString() ?? string.Empty), usage, model);
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();
}
