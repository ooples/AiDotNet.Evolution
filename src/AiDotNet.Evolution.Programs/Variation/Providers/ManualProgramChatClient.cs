using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution.Programs;

/// <summary>A human-in-the-loop <see cref="IProgramChatClient"/>: prompts are written to a queue and answered by a person.</summary>
/// <remarks>
/// The counterpart of OpenEvolve's <c>manual_mode</c>. Each request writes <c>NNNNNN.prompt.json</c> (the full
/// conversation and options) into the queue directory and waits for <c>NNNNNN.response.txt</c>, which a person or another
/// tool writes. The answer is read only once the file is complete: write it under another name and rename it, or the
/// client waits for its length to stop changing. Numbering continues from the files already present, so a resumed run
/// never overwrites an earlier prompt. An unanswered request times out as <see cref="TimeoutException"/>, which the
/// variation operator counts as a failed call.
/// </remarks>
public sealed class ManualProgramChatClient : IProgramChatClient
{
    private readonly string _directory;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private int _next;

    /// <summary>Creates a client over <paramref name="queueDirectory"/>.</summary>
    /// <param name="queueDirectory">Where prompts are written and answers are read.</param>
    /// <param name="timeout">How long to wait for an answer.</param>
    /// <param name="pollInterval">How often to look for an answer; the default is one second.</param>
    /// <param name="modelId">The name recorded for these responses.</param>
    public ManualProgramChatClient(string queueDirectory, TimeSpan timeout, TimeSpan? pollInterval = null, string modelId = "manual")
    {
        ProgramGuard.NotNullOrWhiteSpace(queueDirectory);
        ProgramGuard.NotNullOrWhiteSpace(modelId);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        TimeSpan poll = pollInterval ?? TimeSpan.FromSeconds(1);
        if (poll <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _directory = Path.GetFullPath(queueDirectory);
        _timeout = timeout;
        _pollInterval = poll;
        ModelId = modelId.Trim();
        Directory.CreateDirectory(_directory);
        _next = Directory.EnumerateFiles(_directory, "*.prompt.json")
            .Select(path => Path.GetFileName(path).Split('.')[0])
            .Select(stem => int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : 0)
            .DefaultIfEmpty(0).Max();
    }

    /// <inheritdoc/>
    public string ModelId { get; }

    /// <inheritdoc/>
    public async Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ProgramGuard.NotNull(messages);
        int number;
        lock (_gate) number = ++_next;
        string stem = Path.Combine(_directory, number.ToString("D6", CultureInfo.InvariantCulture));
        var prompt = new
        {
            Model = ModelId,
            Messages = messages.Select(message => new { Role = message.Role.ToString(), message.Text }).ToArray(),
            options?.Temperature,
            options?.TopP,
            options?.MaxOutputTokens,
            ReasoningEffort = options?.ReasoningEffort?.ToString(),
            ResponseFile = Path.GetFileName(stem + ".response.txt")
        };
        string promptPath = stem + ".prompt.json";
        string temporary = promptPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(prompt, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, promptPath);

        string responsePath = stem + ".response.txt";
        DateTime deadline = DateTime.UtcNow + _timeout;
        long lastLength = -1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(responsePath))
            {
                long length = new FileInfo(responsePath).Length;
                // Read once the file stops growing, so a half-written answer is never taken.
                if (length == lastLength) return new ProgramChatResponse(
                    ProgramChatMessage.Assistant(File.ReadAllText(responsePath, new UTF8Encoding(false))), null, ModelId);
                lastLength = length;
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("No answer was written to " + responsePath + " within " + _timeout.TotalSeconds + " seconds.");
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
