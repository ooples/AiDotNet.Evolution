namespace AiDotNet.Evolution.CSharp;

/// <summary>Caller-supplied model transport. No credentials, provider, or network activity are inferred.</summary>
public interface ICSharpProposalClient
{
    string ModelId { get; }
    Task<CompilerChatResponse> GetResponseAsync(IReadOnlyList<CompilerChatMessage> messages,
        CompilerChatOptions? options = null, CancellationToken cancellationToken = default);
}

public sealed record CompilerChatMessage(string Role, string Text)
{
    public static CompilerChatMessage System(string text) => new("system", text);
    public static CompilerChatMessage User(string text) => new("user", text);
    public static CompilerChatMessage Assistant(string text) => new("assistant", text);
}

public sealed record CompilerChatOptions
{
    public double Temperature { get; init; }
    public int MaxOutputTokens { get; init; }
    public int? Seed { get; init; }
}

public sealed record CompilerChatUsage
{
    public long InputTokens { get; }
    public long OutputTokens { get; }
    public CompilerChatUsage(long inputTokens, long outputTokens)
    {
        if (inputTokens < 0 || outputTokens < 0) throw new ArgumentOutOfRangeException(nameof(inputTokens));
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }
}

public sealed class CompilerChatResponse(CompilerChatMessage message, string? modelId = null, CompilerChatUsage? usage = null)
{
    public string Text { get; } = message?.Text ?? throw new ArgumentNullException(nameof(message));
    public string? ModelId { get; } = modelId;
    public CompilerChatUsage? Usage { get; } = usage;
}
