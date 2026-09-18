using Newtonsoft.Json;

namespace AiDotNet.Evolution.Programs;

/// <summary>A bounded caller-owned panel; every member is contacted once per panel request.</summary>
public sealed class ProgramJudgePanel : IProgramChatClient
{
    /// <summary>Creates a fixed panel. No provider is constructed or contacted here.</summary>
    public ProgramJudgePanel(IEnumerable<ProgramJudgeMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var copy = members.Take(33).ToArray();
        if (copy.Length is < 1 or > 32 || copy.Any(m => m is null))
            throw new ArgumentException("A panel requires 1 to 32 members.", nameof(members));
        Members = Array.AsReadOnly(copy);
    }

    /// <summary>Immutable ordered members and weights.</summary>
    public IReadOnlyList<ProgramJudgeMember> Members { get; }
    /// <inheritdoc/>
    public string ModelId => "judge-panel-" + EvolutionHash.Compute(JsonConvert.SerializeObject(
        Members.Select(m => new { m.Client.ModelId, m.Weight })));

    /// <summary>Returns aligned responses; ordinary member failures become null, cancellation and fatal errors propagate.</summary>
    public Task<IReadOnlyList<ProgramChatResponse?>> GetAllResponsesAsync(
        IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null,
        CancellationToken cancellationToken = default) => GetResponsesAsync(messages, options, null, cancellationToken);

    internal async Task<IReadOnlyList<ProgramChatResponse?>> GetResponsesAsync(
        IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options,
        Action? beforeDispatch, CancellationToken cancellationToken)
    {
        var responses = new ProgramChatResponse?[Members.Count];
        for (int i = 0; i < Members.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeDispatch?.Invoke();
            try
            {
                responses[i] = await Members[i].Client.GetResponseAsync(messages, options is null ? null : new ProgramChatOptions
                {
                    Temperature = options.Temperature,
                    MaxOutputTokens = options.MaxOutputTokens,
                    Seed = options.Seed,
                    ResponseFormat = options.ResponseFormat
                }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            { }
        }
        return Array.AsReadOnly(responses);
    }

    /// <inheritdoc/>
    public Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages,
        ProgramChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Set JudgeWithEveryEnsembleMember to use a judge panel.");
}

/// <summary>An immutable provider and positive finite panel weight.</summary>
public sealed class ProgramJudgeMember
{
    /// <summary>Creates a panel member.</summary>
    public ProgramJudgeMember(IProgramChatClient client, double weight = 1)
    {
        ArgumentNullException.ThrowIfNull(client);
        ProgramGuard.NotNullOrWhiteSpace(client.ModelId);
        if (!double.IsFinite(weight) || weight <= 0) throw new ArgumentOutOfRangeException(nameof(weight));
        Client = client; Weight = weight;
    }
    /// <summary>Caller-owned provider.</summary>
    public IProgramChatClient Client { get; }
    /// <summary>Relative voting weight.</summary>
    public double Weight { get; }
}
