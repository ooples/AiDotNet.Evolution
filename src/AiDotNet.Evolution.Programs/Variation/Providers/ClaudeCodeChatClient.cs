using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution.Programs;

/// <summary>Settings for <see cref="ClaudeCodeChatClient"/>, mirroring OpenEvolve's <c>claude_code</c> provider.</summary>
public sealed class ClaudeCodeChatClientOptions
{
    /// <summary>Gets or sets the Claude Code executable; the default is <c>claude</c> on PATH.</summary>
    public string Executable { get; set; } = "claude";
    /// <summary>Gets or sets arguments placed before the provider's own, for a wrapper such as an interpreter.</summary>
    public IList<string> ExecutablePrefixArguments { get; set; } = new List<string>();
    /// <summary>Gets or sets the model or alias passed as <c>--model</c>.</summary>
    public string Model { get; set; } = "sonnet";
    /// <summary>Gets or sets the per-call spending cap passed as <c>--max-budget-usd</c>, or <c>null</c> for none.</summary>
    public decimal? MaxBudgetUsd { get; set; }
    /// <summary>Gets or sets the per-call timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Gets or sets how many times a failed or timed-out call is retried.</summary>
    public int MaxRetries { get; set; } = 3;
    /// <summary>Gets or sets the delay before the first retry; each further retry doubles it.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Gets or sets whether the call is isolated from the user's Claude Code profile: no tools, MCP servers or setting
    /// sources, an empty working directory, and no inherited <c>ANTHROPIC_</c>, <c>CLAUDE_CODE_</c> or proxy variables.
    /// On by default, so that proposals see only the prompt the search built.
    /// </summary>
    public bool Isolate { get; set; } = true;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable)) throw new ArgumentException("Executable is required.", nameof(Executable));
        if (ExecutablePrefixArguments is null) throw new ArgumentException("Prefix arguments cannot be null.", nameof(ExecutablePrefixArguments));
        if (string.IsNullOrWhiteSpace(Model) || Model.Any(char.IsWhiteSpace)) throw new ArgumentException("Model must be a single word.", nameof(Model));
        if (MaxBudgetUsd is <= 0) throw new ArgumentOutOfRangeException(nameof(MaxBudgetUsd));
        if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (MaxRetries is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(MaxRetries));
        if (RetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RetryDelay));
    }
}

/// <summary>An <see cref="IProgramChatClient"/> backed by the Claude Code CLI (<c>claude -p</c>) and its login session.</summary>
/// <remarks>
/// OpenEvolve's <c>claude_code</c> provider passes the prompt as a command-line argument (subject to OS length limits),
/// keeps only the user turns, and runs with the user's full profile. This client sends the whole conversation on
/// standard input, keeps every turn, reads token usage from the CLI's JSON result, and by default runs isolated
/// (<see cref="ClaudeCodeChatClientOptions.Isolate"/>).
/// </remarks>
public sealed class ClaudeCodeChatClient : IProgramChatClient
{
    private const int MaxOutputBytes = 8 * 1024 * 1024;
    private static readonly string[] ScrubbedPrefixes = { "ANTHROPIC_", "CLAUDE_CODE_", "CLAUDECODE", "TOKEN_OPTIMIZER_" };
    private readonly ClaudeCodeChatClientOptions _options;

    /// <summary>Creates a client; the options are copied.</summary>
    public ClaudeCodeChatClient(ClaudeCodeChatClientOptions options)
    {
        ProgramGuard.NotNull(options);
        options.Validate();
        _options = new ClaudeCodeChatClientOptions
        {
            Executable = options.Executable,
            ExecutablePrefixArguments = new List<string>(options.ExecutablePrefixArguments),
            Model = options.Model.Trim(),
            MaxBudgetUsd = options.MaxBudgetUsd,
            Timeout = options.Timeout,
            MaxRetries = options.MaxRetries,
            RetryDelay = options.RetryDelay,
            Isolate = options.Isolate
        };
    }

    /// <inheritdoc/>
    public string ModelId => _options.Model;

    /// <inheritdoc/>
    public async Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ProgramGuard.NotNull(messages);
        if (options?.ResponseFormat is ProgramChatResponseFormat.Json)
            throw new NotSupportedException("The Claude Code provider requests text responses only.");
        string system = string.Join("\n\n", messages.Where(m => m.Role == ProgramChatRole.System).Select(m => m.Text));
        string conversation = JsonSerializer.Serialize(messages.Where(m => m.Role != ProgramChatRole.System)
            .Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Text }).ToArray());
        TimeSpan delay = _options.RetryDelay;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(system, conversation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or InvalidDataException or IOException &&
                attempt < _options.MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
            }
        }
    }

    private async Task<ProgramChatResponse> RunOnceAsync(string system, string conversation, CancellationToken cancellationToken)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "aidotnet-claude-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(workspace);
        try
        {
            var start = new ProcessStartInfo(_options.Executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workspace,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (string argument in _options.ExecutablePrefixArguments) start.ArgumentList.Add(argument);
            foreach (string argument in new[] { "-p", "--output-format", "json", "--model", _options.Model, "--no-session-persistence", "--max-turns", "1" })
                start.ArgumentList.Add(argument);
            if (system.Length > 0) { start.ArgumentList.Add("--system-prompt"); start.ArgumentList.Add(system); }
            if (_options.MaxBudgetUsd is { } budget) { start.ArgumentList.Add("--max-budget-usd"); start.ArgumentList.Add(budget.ToString(CultureInfo.InvariantCulture)); }
            if (_options.Isolate)
            {
                string mcp = Path.Combine(workspace, "no-mcp.json");
                File.WriteAllText(mcp, "{\"mcpServers\":{}}");
                foreach (string argument in new[] { "--tools", "", "--strict-mcp-config", "--mcp-config", mcp, "--setting-sources", "" })
                    start.ArgumentList.Add(argument);
                foreach (string name in start.Environment.Keys.ToArray())
                    if (ScrubbedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) start.Environment.Remove(name);
            }

            using var process = Process.Start(start) ?? throw new IOException("The Claude Code CLI did not start.");
            try
            {
                await process.StandardInput.WriteAsync(conversation).ConfigureAwait(false);
                process.StandardInput.Close();
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                var clock = Stopwatch.StartNew();
                while (!process.WaitForExit(50))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clock.Elapsed > _options.Timeout) throw new TimeoutException("Claude Code did not finish within " + _options.Timeout.TotalSeconds + " seconds.");
                }
                process.WaitForExit();
                string text = await output.ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                if (text.Length > MaxOutputBytes) throw new InvalidDataException("Claude Code output exceeds its bound.");
                if (process.ExitCode != 0) throw new IOException("Claude Code exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
                return Parse(text);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { /* a leftover empty workspace is harmless */ }
        }
    }

    private ProgramChatResponse Parse(string text)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException exception) { throw new InvalidDataException("Claude Code returned malformed JSON.", exception); }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out JsonElement result) || result.ValueKind != JsonValueKind.String ||
                (root.TryGetProperty("is_error", out JsonElement error) && error.ValueKind == JsonValueKind.True))
                throw new InvalidDataException("Claude Code reported an unsuccessful generation.");
            ProgramChatUsage? usage = null;
            if (root.TryGetProperty("usage", out JsonElement reported) && reported.ValueKind == JsonValueKind.Object &&
                reported.TryGetProperty("input_tokens", out JsonElement input) && input.TryGetInt32(out int inputTokens) &&
                reported.TryGetProperty("output_tokens", out JsonElement output) && output.TryGetInt32(out int outputTokens))
                usage = new ProgramChatUsage(inputTokens, outputTokens);
            return new ProgramChatResponse(ProgramChatMessage.Assistant(result.GetString() ?? string.Empty), usage, _options.Model);
        }
    }
}
