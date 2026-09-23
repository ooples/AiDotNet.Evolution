using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution.Programs.Experience;

/// <summary>What an evaluated proposal achieved, for later retrieval as a lesson.</summary>
public enum ProgramExperienceOutcome
{
    /// <summary>A valid program that improved on its parent.</summary>
    Improved,
    /// <summary>A valid program that did not improve.</summary>
    NotImproved,
    /// <summary>The program was invalid, infeasible or failed its checks.</summary>
    Invalid,
    /// <summary>The proposal or its evaluation failed outright.</summary>
    Failed
}

/// <summary>Which data an experience's evidence came from. Final-test evidence is never stored.</summary>
public enum ProgramEvidencePartition
{
    /// <summary>Search-time (development) evidence.</summary>
    Search,
    /// <summary>Validation evidence.</summary>
    Validation,
    /// <summary>Held-out final-test evidence; refused by the store so it can never reach a proposer.</summary>
    FinalTest
}

/// <summary>One retained lesson: the hypothesis tried, what it produced, and under which task assumptions.</summary>
public sealed class ProgramExperienceRecord
{
    /// <summary>Maximum characters of a hypothesis.</summary>
    public const int MaximumHypothesisCharacters = 1024;
    /// <summary>Maximum retained diagnostics per record.</summary>
    public const int MaximumDiagnostics = 8;

    /// <summary>Creates a record; every text field is bounded.</summary>
    public ProgramExperienceRecord(string runId, string taskIdentity, string taskVersion, string hypothesis, string sourceHash,
        ProgramExperienceOutcome outcome, ProgramEvidencePartition partition, double? quality, IEnumerable<string>? diagnostics, long sequence)
    {
        ProgramGuard.NotNullOrWhiteSpace(runId);
        ProgramGuard.NotNullOrWhiteSpace(taskIdentity);
        ProgramGuard.NotNullOrWhiteSpace(taskVersion);
        ProgramGuard.NotNullOrWhiteSpace(hypothesis);
        ProgramGuard.NotNullOrWhiteSpace(sourceHash);
        if (!Enum.IsDefined(typeof(ProgramExperienceOutcome), outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        if (!Enum.IsDefined(typeof(ProgramEvidencePartition), partition)) throw new ArgumentOutOfRangeException(nameof(partition));
        if (quality is { } q && !double.IsFinite(q)) throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be finite.");
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        RunId = runId; TaskIdentity = taskIdentity; TaskVersion = taskVersion;
        Hypothesis = hypothesis.Length > MaximumHypothesisCharacters ? hypothesis[..MaximumHypothesisCharacters] : hypothesis;
        SourceHash = sourceHash; Outcome = outcome; Partition = partition; Quality = quality; Sequence = sequence;
        Diagnostics = (diagnostics ?? Array.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Length > 256 ? d[..256] : d).Take(MaximumDiagnostics).ToArray();
    }

    /// <summary>Gets the run that produced the evidence.</summary>
    public string RunId { get; }
    /// <summary>Gets the task identity the lesson applies to.</summary>
    public string TaskIdentity { get; }
    /// <summary>Gets the task/evaluator version; lessons never cross versions.</summary>
    public string TaskVersion { get; }
    /// <summary>Gets the proposal's stated hypothesis.</summary>
    public string Hypothesis { get; }
    /// <summary>Gets the evaluated program's source hash.</summary>
    public string SourceHash { get; }
    /// <summary>Gets what the proposal achieved.</summary>
    public ProgramExperienceOutcome Outcome { get; }
    /// <summary>Gets which data the evidence came from.</summary>
    public ProgramEvidencePartition Partition { get; }
    /// <summary>Gets the measured quality, if any.</summary>
    public double? Quality { get; }
    /// <summary>Gets bounded diagnostics (at most 8, 256 characters each).</summary>
    public IReadOnlyList<string> Diagnostics { get; }
    /// <summary>Gets the producing run's monotonic sequence, used for recency.</summary>
    public long Sequence { get; }

    internal string Render()
    {
        var text = new StringBuilder().Append('[').Append(Outcome).Append(']').Append(' ').Append(Hypothesis);
        if (Quality is { } q) text.Append(" (quality ").Append(q.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)).Append(')');
        foreach (string diagnostic in Diagnostics.Take(2)) text.Append(" | ").Append(diagnostic);
        return text.ToString();
    }
}

/// <summary>A query for lessons relevant to a new proposal.</summary>
/// <param name="RunId">The current run.</param>
/// <param name="TaskIdentity">The task the proposal is for; other tasks never match.</param>
/// <param name="TaskVersion">The task/evaluator version; other versions never match.</param>
/// <param name="ContextBudgetCharacters">The rendered lessons never exceed this many characters.</param>
/// <param name="IncludeOtherRuns">Opt in to lessons from other runs of the same task and version.</param>
public sealed record ProgramExperienceQuery(string RunId, string TaskIdentity, string TaskVersion, int ContextBudgetCharacters,
    bool IncludeOtherRuns);

/// <summary>Retrieved lessons and the text that fits the context budget.</summary>
public sealed record ProgramExperienceRetrieval(IReadOnlyList<ProgramExperienceRecord> Selected, string Context, int ExcludedIncompatible);

/// <summary>
/// Bounded experience memory for program search (US-18). Final-test evidence is refused on entry, so it can never
/// be retrieved; retrieval never crosses task identity or version, crosses runs only when opted in, and fits a
/// fixed character budget with alternating successes and failures from distinct sources.
/// </summary>
public sealed class ProgramExperienceStore
{
    private readonly int _capacity;
    private readonly List<ProgramExperienceRecord> _records = new();
    private readonly object _gate = new();

    /// <summary>Creates a store that retains at most <paramref name="capacity"/> records, evicting the oldest.</summary>
    public ProgramExperienceStore(int capacity = 4096)
    {
        if (capacity is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>Gets the retained record count.</summary>
    public int Count { get { lock (_gate) return _records.Count; } }

    /// <summary>Retains a record; final-test evidence is refused so it can never inform a proposal.</summary>
    public void Add(ProgramExperienceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Partition == ProgramEvidencePartition.FinalTest)
            throw new ArgumentException("Final-test evidence is never retained as experience.", nameof(record));
        lock (_gate)
        {
            _records.Add(record);
            if (_records.Count > _capacity) _records.RemoveRange(0, _records.Count - _capacity);
        }
    }

    /// <summary>Selects compatible lessons that fit the query's budget, newest first, alternating outcomes.</summary>
    public ProgramExperienceRetrieval Retrieve(ProgramExperienceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ProgramGuard.NotNullOrWhiteSpace(query.RunId);
        ProgramGuard.NotNullOrWhiteSpace(query.TaskIdentity);
        ProgramGuard.NotNullOrWhiteSpace(query.TaskVersion);
        if (query.ContextBudgetCharacters is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(query));
        ProgramExperienceRecord[] all;
        lock (_gate) all = _records.ToArray();
        var compatible = all.Where(r => r.TaskIdentity == query.TaskIdentity && r.TaskVersion == query.TaskVersion &&
            (query.IncludeOtherRuns || r.RunId == query.RunId)).ToArray();
        int excluded = all.Length - compatible.Length;
        // Alternate what worked with what to avoid, newest first, one lesson per distinct source.
        var queues = new Queue<ProgramExperienceRecord>[]
        {
            new(compatible.Where(r => r.Outcome == ProgramExperienceOutcome.Improved).OrderByDescending(r => r.Sequence)),
            new(compatible.Where(r => r.Outcome != ProgramExperienceOutcome.Improved).OrderByDescending(r => r.Sequence))
        };
        var selected = new List<ProgramExperienceRecord>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var context = new StringBuilder();
        for (int turn = 0; queues.Any(q => q.Count > 0); turn++)
        {
            Queue<ProgramExperienceRecord> queue = queues[turn % 2].Count > 0 ? queues[turn % 2] : queues[(turn + 1) % 2];
            ProgramExperienceRecord next = queue.Dequeue();
            if (!sources.Add(next.SourceHash)) continue;
            string line = next.Render();
            int needed = line.Length + (context.Length == 0 ? 0 : 1);
            if (context.Length + needed > query.ContextBudgetCharacters) continue;
            if (context.Length > 0) context.Append('\n');
            context.Append(line);
            selected.Add(next);
        }
        return new ProgramExperienceRetrieval(selected, context.ToString(), excluded);
    }

    /// <summary>Serializes the retained records for checkpointing.</summary>
    public string CaptureState()
    {
        lock (_gate)
            return JsonSerializer.Serialize(_records.Select(r => new StoredRecord(r.RunId, r.TaskIdentity, r.TaskVersion, r.Hypothesis,
                r.SourceHash, r.Outcome, r.Partition, r.Quality, r.Diagnostics.ToArray(), r.Sequence)).ToArray());
    }

    /// <summary>Restores records captured by <see cref="CaptureState"/>; malformed or final-test state is refused.</summary>
    public void RestoreState(string state)
    {
        ArgumentNullException.ThrowIfNull(state);
        StoredRecord[]? stored;
        try { stored = JsonSerializer.Deserialize<StoredRecord[]>(state); }
        catch (JsonException exception) { throw new InvalidDataException("The experience state is invalid.", exception); }
        if (stored is null || stored.Length > _capacity) throw new InvalidDataException("The experience state is invalid or over capacity.");
        List<ProgramExperienceRecord> records;
        try
        {
            records = stored.Select(s => new ProgramExperienceRecord(s.RunId, s.TaskIdentity, s.TaskVersion, s.Hypothesis, s.SourceHash,
                s.Outcome, s.Partition, s.Quality, s.Diagnostics, s.Sequence)).ToList();
        }
        catch (ArgumentException exception) { throw new InvalidDataException("The experience state holds an invalid record.", exception); }
        if (records.Any(r => r.Partition == ProgramEvidencePartition.FinalTest))
            throw new InvalidDataException("Final-test evidence is never restored as experience.");
        lock (_gate) { _records.Clear(); _records.AddRange(records); }
    }

    private sealed record StoredRecord(string RunId, string TaskIdentity, string TaskVersion, string Hypothesis, string SourceHash,
        ProgramExperienceOutcome Outcome, ProgramEvidencePartition Partition, double? Quality, string[] Diagnostics, long Sequence);
}