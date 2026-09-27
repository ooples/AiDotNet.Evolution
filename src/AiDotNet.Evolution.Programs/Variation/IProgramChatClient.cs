namespace AiDotNet.Evolution.Programs;

/// <summary>Caller-owned, non-streaming text proposal provider. The library supplies no network connector.</summary>
public interface IProgramChatClient
{
    /// <summary>Stable model/provider identity for compatibility and provenance.</summary>
    string ModelId { get; }
    /// <summary>Returns text and optional provider-reported usage; cancellation must be honored by the provider.</summary>
    Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages,
        ProgramChatOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Roles supported by a text proposal conversation.</summary>
public enum ProgramChatRole { System, User, Assistant }

/// <summary>An immutable text message. Provider adapters must not reinterpret source as tool requests.</summary>
public sealed class ProgramChatMessage
{
    /// <summary>Creates a role-tagged text message.</summary>
    public ProgramChatMessage(ProgramChatRole role, string text)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        Role = role;
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }
    /// <summary>Author role.</summary>
    public ProgramChatRole Role { get; }
    /// <summary>Exact message text.</summary>
    public string Text { get; }
    /// <summary>Creates system instructions.</summary>
    public static ProgramChatMessage System(string text) => new(ProgramChatRole.System, text);
    /// <summary>Creates user input.</summary>
    public static ProgramChatMessage User(string text) => new(ProgramChatRole.User, text);
    /// <summary>Creates an assistant response.</summary>
    public static ProgramChatMessage Assistant(string text) => new(ProgramChatRole.Assistant, text);
}

/// <summary>Sampling settings; null means the caller's provider default.</summary>
public sealed class ProgramChatOptions
{
    /// <summary>Requested format; an adapter must honor or explicitly reject unsupported formats.</summary>
    public ProgramChatResponseFormat ResponseFormat { get; set; }
    /// <summary>Sampling temperature.</summary>
    public double? Temperature { get; set; }
    /// <summary>Maximum generated tokens.</summary>
    public int? MaxOutputTokens { get; set; }
    /// <summary>Proposal-local sampling seed.</summary>
    public int? Seed { get; set; }
}

/// <summary>Provider response-format request, not a guarantee that returned text is valid.</summary>
public enum ProgramChatResponseFormat { Text, Json }

/// <summary>Exposes a wrapped client so judging can discover an explicitly configured panel.</summary>
public interface IProgramChatClientDecorator : IProgramChatClient
{
    /// <summary>Wrapped client.</summary>
    IProgramChatClient Inner { get; }
}

/// <summary>Immutable provider-reported token usage; not a monetary charge or budget admission.</summary>
public sealed class ProgramChatUsage
{
    /// <summary>Creates nonnegative token counts.</summary>
    public ProgramChatUsage(int inputTokens, int outputTokens)
    {
        if (inputTokens < 0) throw new ArgumentOutOfRangeException(nameof(inputTokens));
        if (outputTokens < 0) throw new ArgumentOutOfRangeException(nameof(outputTokens));
        InputTokens = inputTokens; OutputTokens = outputTokens;
    }
    /// <summary>Input tokens.</summary>
    public int InputTokens { get; }
    /// <summary>Output tokens.</summary>
    public int OutputTokens { get; }
    /// <summary>Total tokens without Int32 overflow.</summary>
    public long TotalTokens => (long)InputTokens + OutputTokens;
}

/// <summary>Immutable assistant text and optional provider usage/identity.</summary>
public sealed class ProgramChatResponse
{
    /// <summary>Creates an assistant response.</summary>
    public ProgramChatResponse(ProgramChatMessage message, ProgramChatUsage? usage = null, string? modelId = null)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        if (message.Role != ProgramChatRole.Assistant) throw new ArgumentException("The response must be authored by the assistant.", nameof(message));
        Usage = usage; ModelId = modelId;
    }
    /// <summary>Assistant message.</summary>
    public ProgramChatMessage Message { get; }
    /// <summary>Exact response text.</summary>
    public string Text => Message.Text;
    /// <summary>Usage when reported; null means unknown, not proof of free work.</summary>
    public ProgramChatUsage? Usage { get; }
    /// <summary>Reported model identity, if available.</summary>
    public string? ModelId { get; }
}
