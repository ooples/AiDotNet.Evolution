using AiDotNet.Evolution.Programs;
// Migrated text-chat fixture from AiDotNet9cd7d5d:ProgramEvolutionTestDoubles.cs; original BSL license retained in Programs/Legacy.
using System.Collections.ObjectModel;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

internal sealed class FakeChatClient : IProgramChatClient
{
    private readonly IReadOnlyList<string> _responses;
    private readonly List<IReadOnlyList<ProgramChatMessage>> _conversations = new();
    private int _calls;

    public FakeChatClient(params string[] responses) => _responses = responses;

    public string ModelId => "fake-model";

    public int Calls => _calls;

    public ProgramChatOptions? LastOptions { get; private set; }

    public IReadOnlyList<IReadOnlyList<ProgramChatMessage>> Conversations => _conversations;

    public Exception? ThrowOnFirstCall { get; set; }

    public ProgramChatUsage? Usage { get; set; }

    public Task<ProgramChatResponse> GetResponseAsync(
        IReadOnlyList<ProgramChatMessage> messages,
        ProgramChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _conversations.Add(new ReadOnlyCollection<ProgramChatMessage>(new List<ProgramChatMessage>(messages)));
        LastOptions = options;
        int index = _calls;
        _calls++;

        if (index == 0 && ThrowOnFirstCall is not null) throw ThrowOnFirstCall;

        string text = _responses.Count == 0
            ? string.Empty
            : _responses[Math.Min(index, _responses.Count - 1)];

        return Task.FromResult(new ProgramChatResponse(ProgramChatMessage.Assistant(text), usage: Usage));
    }

}
