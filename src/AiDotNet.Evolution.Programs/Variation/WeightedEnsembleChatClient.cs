using System.Globalization;

namespace AiDotNet.Evolution.Programs;

/// <summary>One member of a <see cref="WeightedEnsembleChatClient"/>: a client and its sampling weight.</summary>
/// <param name="Client">The member's client.</param>
/// <param name="Weight">Its relative sampling weight; positive and finite.</param>
public sealed record WeightedChatModel(IProgramChatClient Client, double Weight);

/// <summary>
/// An <see cref="IProgramChatClient"/> that sends each request to one member of a weighted model set: OpenEvolve's
/// <c>llm.models</c> (or <c>primary_model</c> / <c>secondary_model</c> with weights) and AlphaEvolve's fast-plus-strong
/// ensemble.
/// </summary>
/// <remarks>
/// <para>The member is chosen from the request's <see cref="ProgramChatOptions.Seed"/>, which the variation operator derives
/// from the proposal's seeded random stream, so a replay of the same run sends every proposal to the same model. A
/// request without a seed falls back to a deterministic sequence over the members.</para>
/// <para>A member that fails is charged the failure (see <see cref="GetMemberStatistics"/>) and the exception propagates:
/// the ensemble never silently re-routes a failed call to another model, which would hide an outage and change what
/// the run spent. Each response records the member that answered in <see cref="ProgramChatResponse.ModelId"/>.</para>
/// <para>For LLM-based evaluation (OpenEvolve's <c>evaluator_models</c>), give the judge its own ensemble.</para>
/// </remarks>
public sealed class WeightedEnsembleChatClient : IProgramChatClient
{
    /// <summary>The most members one ensemble may have.</summary>
    public const int MaximumMembers = 256;

    private readonly WeightedChatModel[] _members;
    private readonly double[] _cumulative;
    private readonly long[] _calls;
    private readonly long[] _failures;
    private long _unseeded;

    /// <summary>Creates an ensemble over <paramref name="members"/>.</summary>
    public WeightedEnsembleChatClient(IEnumerable<WeightedChatModel> members)
    {
        ProgramGuard.NotNull(members);
        // Bounded before materializing, so an unbounded sequence cannot exhaust memory.
        var list = new List<WeightedChatModel>();
        foreach (WeightedChatModel member in members)
        {
            if (list.Count == MaximumMembers)
                throw new ArgumentException($"An ensemble may have at most {MaximumMembers} models.", nameof(members));
            list.Add(member);
        }
        _members = list.ToArray();
        if (_members.Length == 0) throw new ArgumentException("An ensemble needs at least one model.", nameof(members));
        if (_members.Any(member => member is null || member.Client is null))
            throw new ArgumentException("Ensemble members and their clients cannot be null.", nameof(members));
        if (_members.Any(member => double.IsNaN(member.Weight) || double.IsInfinity(member.Weight) || member.Weight <= 0))
            throw new ArgumentException("Every weight must be positive and finite.", nameof(members));
        double total = _members.Sum(member => member.Weight);
        if (double.IsInfinity(total)) throw new ArgumentException("The weights' total must be finite.", nameof(members));
        _cumulative = new double[_members.Length];
        double running = 0;
        for (int i = 0; i < _members.Length; i++) { running += _members[i].Weight / total; _cumulative[i] = running; }
        _cumulative[_members.Length - 1] = 1.0;
        _calls = new long[_members.Length];
        _failures = new long[_members.Length];
        // Length-prefixed, so ids containing the separators cannot make two different ensembles share an identity.
        ModelId = "ensemble(" + string.Join(",", _members.Select(member =>
            member.Client.ModelId.Length.ToString(CultureInfo.InvariantCulture) + ":" + member.Client.ModelId + ":" +
            member.Weight.ToString("R", CultureInfo.InvariantCulture))) + ")";
    }

    /// <inheritdoc/>
    public string ModelId { get; }

    /// <summary>Gets each member's model id, calls routed to it and failures charged to it, in member order.</summary>
    public IReadOnlyList<(string ModelId, long Calls, long Failures)> GetMemberStatistics() =>
        _members.Select((member, i) => (member.Client.ModelId, Interlocked.Read(ref _calls[i]), Interlocked.Read(ref _failures[i]))).ToArray();

    /// <summary>Returns the member index a request with <paramref name="seed"/> is routed to.</summary>
    internal int Choose(int? seed)
    {
        double draw = seed is { } value
            ? Unit((ulong)(uint)value)
            : Unit((ulong)Interlocked.Increment(ref _unseeded));
        for (int i = 0; i < _cumulative.Length; i++)
            if (draw < _cumulative[i]) return i;
        return _cumulative.Length - 1;
    }

    /// <inheritdoc/>
    public async Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        int index = Choose(options?.Seed);
        Interlocked.Increment(ref _calls[index]);
        IProgramChatClient member = _members[index].Client;
        try
        {
            ProgramChatResponse response = await member.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            return new ProgramChatResponse(response.Message, response.Usage, response.ModelId ?? member.ModelId);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            Interlocked.Increment(ref _failures[index]);
            throw;
        }
    }

    // A SplitMix64 finalizer spreads consecutive seeds uniformly over [0, 1).
    private static double Unit(ulong value)
    {
        ulong z = unchecked(value + 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }
}
