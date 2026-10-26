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
    /// <summary>
    /// Gets or sets a spending cap across every call and retry this client makes, or <c>null</c> for none. Each call
    /// reserves its <c>--max-budget-usd</c> out of what remains (the smaller of <see cref="MaxBudgetUsd"/> and the
    /// remainder) before it starts, so concurrent calls cannot jointly overspend. It is then charged the CLI's
    /// reported <c>total_cost_usd</c>, or its whole reservation when no cost is reported. Once nothing remains, a call
    /// throws <see cref="InvalidOperationException"/> without starting the CLI. The CLI checks its own cap between
    /// turns, so a single call may run past its share by the cost of one turn.
    /// </summary>
    public decimal? TotalBudgetUsd { get; set; }
    /// <summary>Gets or sets the per-call timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Gets or sets how many times a failed or timed-out call is retried.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Turns the CLI may take before it stops without answering; 4 by default.</summary>
    /// <remarks>
    /// This was fixed at 1, which a current model trips by thinking: the CLI then returns
    /// <c>subtype: error_max_turns</c> with no <c>result</c> at all, measured at 484 thinking tokens on a
    /// six-line prompt. A reasoning turn is ordinary rather than exceptional, so one turn is not a safe
    /// ceiling. Tool use is already prevented by <see cref="Isolate"/> rather than by this limit.
    /// </remarks>
    public int MaxTurns { get; set; } = 4;
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
        if (TotalBudgetUsd is <= 0) throw new ArgumentOutOfRangeException(nameof(TotalBudgetUsd));
        if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (MaxRetries is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(MaxRetries));
        if (MaxTurns is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(MaxTurns));
        if (RetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RetryDelay));
    }
}

/// <summary>An <see cref="IProgramChatClient"/> backed by the Claude Code CLI (<c>claude -p</c>) and its login session.</summary>
/// <remarks>
/// OpenEvolve's <c>claude_code</c> provider passes the prompt as a command-line argument (subject to OS length limits),
/// keeps only the user turns, and runs with the user's full profile. This client sends the whole conversation on
/// standard input, keeps every turn, reads token usage from the CLI's JSON result, and by default runs isolated
/// (<see cref="ClaudeCodeChatClientOptions.Isolate"/>). A request for
/// <see cref="ProgramChatResponseFormat.Json"/> is honoured by instruction, because the CLI has no flag for
/// one; refusing it instead left the LLM judge unusable here at its own defaults.
/// </remarks>
public sealed class ClaudeCodeChatClient : IProgramChatClient
{
    private const int MaxOutputBytes = 8 * 1024 * 1024;

    /// <summary>Carries a JSON response-format request to a CLI that has no flag for one.</summary>
    internal const string JsonOnlyInstruction =
        "Reply with one JSON object and nothing else: no prose before or after it, and no code fence.";

    private static readonly string[] ScrubbedPrefixes = { "ANTHROPIC_", "CLAUDE_CODE_", "CLAUDECODE", "TOKEN_OPTIMIZER_" };
    private readonly ClaudeCodeChatClientOptions _options;
    private readonly object _budgetLock = new();
    private decimal _spentUsd;
    private decimal _reservedUsd;

    /// <summary>
    /// Gets the dollars charged so far: each finished call's reported <c>total_cost_usd</c>, or its whole per-call cap
    /// when the CLI reported none. Calls still running are not included.
    /// </summary>
    public decimal SpentUsd { get { lock (_budgetLock) return _spentUsd; } }

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
            TotalBudgetUsd = options.TotalBudgetUsd,
            Timeout = options.Timeout,
            MaxRetries = options.MaxRetries,
            MaxTurns = options.MaxTurns,
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
        string system = string.Join("\n\n", messages.Where(m => m.Role == ProgramChatRole.System).Select(m => m.Text));
        if (options?.ResponseFormat is ProgramChatResponseFormat.Json)
        {
            // The CLI has no response-format flag, so the request is honoured by instruction instead of
            // refused. Throwing here made LLM feedback unusable with this provider at its own defaults:
            // LlmFeedbackOptions.RequestJsonResponseFormat is true, so every judgement failed with
            // "the request failed with NotSupportedException" while the evaluation completed on its base
            // quality - a feedback signal silently absent rather than a configuration error.
            // Not silently ignored either: the instruction goes into the prompt the model actually reads.
            system = system.Length > 0 ? system + "\n\n" + JsonOnlyInstruction : JsonOnlyInstruction;
        }

        string conversation = JsonSerializer.Serialize(messages.Where(m => m.Role != ProgramChatRole.System)
            .Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Text }).ToArray());
        TimeSpan delay = _options.RetryDelay;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // Reserved inside the try, but an exhausted budget is an InvalidOperationException, which the retry
                // filter below does not take: it surfaces at once instead of being retried.
                decimal? cap = ReserveCallBudget();
                var cost = new CallCost();
                try
                {
                    return await RunOnceAsync(system, conversation, cap, cost, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    SettleCallBudget(cap, cost.ReportedUsd);
                }
            }
            catch (Exception exception) when (exception is TimeoutException or InvalidDataException or IOException &&
                attempt < _options.MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
            }
        }
    }

    private sealed class CallCost
    {
        public decimal? ReportedUsd;
    }

    // The --max-budget-usd for the next call: the per-call cap, or with a total budget the smaller of that and what is
    // neither spent nor reserved by calls still running, which is then reserved for this call.
    private decimal? ReserveCallBudget()
    {
        if (_options.TotalBudgetUsd is not { } total) return _options.MaxBudgetUsd;
        lock (_budgetLock)
        {
            decimal remaining = total - _spentUsd - _reservedUsd;
            if (remaining <= 0)
                throw new InvalidOperationException("The Claude Code total budget of $" +
                    total.ToString(CultureInfo.InvariantCulture) + " is spent or reserved by calls in flight ($" +
                    _spentUsd.ToString(CultureInfo.InvariantCulture) + " charged); no further call is made.");
            decimal cap = _options.MaxBudgetUsd is { } perCall ? Math.Min(perCall, remaining) : remaining;
            _reservedUsd += cap;
            return cap;
        }
    }

    // Charges a finished call (successful or not) its reported cost, or its whole cap when the CLI reported none.
    private void SettleCallBudget(decimal? cap, decimal? reportedUsd)
    {
        lock (_budgetLock)
        {
            if (_options.TotalBudgetUsd.HasValue && cap is { } reserved) _reservedUsd -= reserved;
            _spentUsd += reportedUsd ?? cap ?? 0m;
        }
    }

    // total_cost_usd from the CLI's JSON result, which it reports for failed generations as well as successful ones.
    private static decimal? ReadReportedCost(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("total_cost_usd", out JsonElement cost) &&
                cost.ValueKind == JsonValueKind.Number && cost.TryGetDecimal(out decimal usd) && usd >= 0
                ? usd : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ProgramChatResponse> RunOnceAsync(string system, string conversation, decimal? capUsd, CallCost cost,
        CancellationToken cancellationToken)
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
            foreach (string argument in new[] { "-p", "--output-format", "json", "--model", _options.Model, "--no-session-persistence", "--max-turns",
                _options.MaxTurns.ToString(CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
            if (system.Length > 0) { start.ArgumentList.Add("--system-prompt"); start.ArgumentList.Add(system); }
            if (capUsd is { } budget) { start.ArgumentList.Add("--max-budget-usd"); start.ArgumentList.Add(budget.ToString(CultureInfo.InvariantCulture)); }
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
                cost.ReportedUsd = ReadReportedCost(text);
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
            // Named separately from the generic failure, and deliberately NOT an InvalidDataException: the retry
            // filter treats that as transient, so a turn limit - which is deterministic - was retried MaxRetries
            // times, paying for every attempt before failing anyway. Each call costs the same whatever it returns.
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("subtype", out JsonElement subtype) &&
                subtype.ValueKind == JsonValueKind.String &&
                string.Equals(subtype.GetString(), "error_max_turns", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Claude Code CLI stopped at its --max-turns limit of " +
                    _options.MaxTurns.ToString(CultureInfo.InvariantCulture) +
                    " without producing an answer, which a model does by spending a turn on reasoning. Raise " +
                    "ClaudeCodeChatClientOptions.MaxTurns; retrying the same request cannot change the outcome.");
            }

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
