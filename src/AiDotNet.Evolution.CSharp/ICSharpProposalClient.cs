namespace AiDotNet.Evolution.CSharp;

/// <summary>Caller-supplied model transport. No credentials, provider, or network activity are inferred.</summary>
public interface ICSharpProposalClient
{
    /// <summary>Gets the model identity recorded with every proposal; it must not change during a run.</summary>
    string ModelId { get; }

    /// <summary>Sends one conversation and returns the model's reply.</summary>
    /// <param name="messages">The conversation, oldest first.</param>
    /// <param name="options">Sampling options, or <c>null</c> for the transport's defaults.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply, with the reporting model and token usage when the transport knows them.</returns>
    Task<CompilerChatResponse> GetResponseAsync(IReadOnlyList<CompilerChatMessage> messages,
        CompilerChatOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>One message of a proposal conversation.</summary>
/// <param name="Role">The author: <c>system</c>, <c>user</c> or <c>assistant</c>. Use the factory methods.</param>
/// <param name="Text">The message text.</param>
public sealed record CompilerChatMessage(string Role, string Text)
{
    /// <summary>Creates standing instructions.</summary>
    /// <param name="text">The instructions.</param>
    /// <returns>A <c>system</c> message.</returns>
    public static CompilerChatMessage System(string text) => new("system", text);

    /// <summary>Creates a request.</summary>
    /// <param name="text">The request.</param>
    /// <returns>A <c>user</c> message.</returns>
    public static CompilerChatMessage User(string text) => new("user", text);

    /// <summary>Creates a model reply, used to replay the previous answer in a repair turn.</summary>
    /// <param name="text">The reply.</param>
    /// <returns>An <c>assistant</c> message.</returns>
    public static CompilerChatMessage Assistant(string text) => new("assistant", text);
}

/// <summary>Sampling options passed to the transport.</summary>
public sealed record CompilerChatOptions
{
    /// <summary>Gets the sampling temperature.</summary>
    public double Temperature { get; init; }

    /// <summary>Gets the most tokens the reply may contain.</summary>
    public int MaxOutputTokens { get; init; }

    /// <summary>Gets the sampling seed, or <c>null</c> when none is requested.</summary>
    public int? Seed { get; init; }
}

/// <summary>Token counts the provider reported for one request.</summary>
public sealed record CompilerChatUsage
{
    /// <summary>Gets the prompt tokens.</summary>
    public long InputTokens { get; }

    /// <summary>Gets the reply tokens.</summary>
    public long OutputTokens { get; }

    /// <summary>Records reported token counts.</summary>
    /// <param name="inputTokens">Prompt tokens; not negative.</param>
    /// <param name="outputTokens">Reply tokens; not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    public CompilerChatUsage(long inputTokens, long outputTokens)
    {
        if (inputTokens < 0 || outputTokens < 0) throw new ArgumentOutOfRangeException(nameof(inputTokens));
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }
}

/// <summary>A model reply.</summary>
/// <param name="message">The reply message.</param>
/// <param name="modelId">The model the provider says answered, when it reports one.</param>
/// <param name="usage">The token counts, when the provider reports them.</param>
public sealed class CompilerChatResponse(CompilerChatMessage message, string? modelId = null, CompilerChatUsage? usage = null)
{
    /// <summary>Gets the reply text.</summary>
    public string Text { get; } = message?.Text ?? throw new ArgumentNullException(nameof(message));

    /// <summary>Gets the model the provider says answered, or <c>null</c>.</summary>
    public string? ModelId { get; } = modelId;

    /// <summary>Gets the reported token counts, or <c>null</c>.</summary>
    public CompilerChatUsage? Usage { get; } = usage;
}
