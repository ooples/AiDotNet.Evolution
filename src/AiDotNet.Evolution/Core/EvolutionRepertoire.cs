using System.Collections.ObjectModel;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>Declared provenance and prior-information cost retained with a portable seed repertoire.</summary>
public sealed class EvolutionRepertoireProvenance
{
    /// <summary>Creates provenance; the caller retains the raw evidence artifact and accounts for its construction cost.</summary>
    public EvolutionRepertoireProvenance(string sourceRunId, string sourceStateHash, string evidenceSha256,
        DateTimeOffset createdAt, double priorCostUnits, string costUnit)
    {
        EvolutionReuseEncoding.Label(sourceRunId, nameof(sourceRunId));
        EvolutionReuseEncoding.Digest(sourceStateHash, nameof(sourceStateHash));
        EvolutionReuseEncoding.Digest(evidenceSha256, nameof(evidenceSha256));
        EvolutionReuseEncoding.Label(costUnit, nameof(costUnit));
        if (createdAt == default) throw new ArgumentOutOfRangeException(nameof(createdAt));
        if (double.IsNaN(priorCostUnits) || double.IsInfinity(priorCostUnits) || priorCostUnits < 0)
            throw new ArgumentOutOfRangeException(nameof(priorCostUnits));
        SourceRunId = sourceRunId; SourceStateHash = sourceStateHash; EvidenceSha256 = evidenceSha256;
        CreatedAt = createdAt.ToUniversalTime(); PriorCostUnits = priorCostUnits; CostUnit = costUnit;
    }

    /// <summary>Gets the source run's identifier.</summary>
    public string SourceRunId { get; }
    /// <summary>Gets its logical state digest.</summary>
    public string SourceStateHash { get; }
    /// <summary>Gets the separately retained raw evidence artifact's digest.</summary>
    public string EvidenceSha256 { get; }
    /// <summary>Gets the UTC export time.</summary>
    public DateTimeOffset CreatedAt { get; }
    /// <summary>Gets declared prior-information construction cost, not new-run spend or a measured receipt.</summary>
    public double PriorCostUnits { get; }
    /// <summary>Gets the versioned unit shared by the cost declaration.</summary>
    public string CostUnit { get; }
}

/// <summary>A portable canonical seed; intentionally contains no reusable fitness.</summary>
public sealed class EvolutionRepertoireEntry
{
    internal EvolutionRepertoireEntry(string sourceGenomeId, string payload, string payloadSha256)
    {
        EvolutionReuseEncoding.Label(sourceGenomeId, nameof(sourceGenomeId));
        EvolutionReuseEncoding.Digest(payloadSha256, nameof(payloadSha256));
        if (payload is null || payload.Length > EvolutionRepertoire.MaximumPayloadBytes ||
            EvolutionReuseEncoding.Utf8.GetByteCount(payload) > EvolutionRepertoire.MaximumPayloadBytes)
            throw new ArgumentException("A repertoire payload exceeds the 64 KiB UTF8 limit.", nameof(payload));
        if (!string.Equals(EvolutionHash.Compute(payload), payloadSha256, StringComparison.Ordinal))
            throw new ArgumentException("The repertoire payload checksum does not match.", nameof(payloadSha256));
        SourceGenomeId = sourceGenomeId; Payload = payload; PayloadSha256 = payloadSha256;
    }

    /// <summary>Gets the canonical identity under the source task.</summary>
    public string SourceGenomeId { get; }
    /// <summary>Gets untrusted codec text; never execute it or interpolate it into a command.</summary>
    public string Payload { get; }
    /// <summary>Gets the exact payload's SHA256 digest.</summary>
    public string PayloadSha256 { get; }
}

/// <summary>The current task's decision for one source repertoire entry.</summary>
public enum EvolutionRepertoireImportStatus
{
    /// <summary>A distinct canonical seed was accepted for fresh evaluation.</summary>
    Accepted = 0,
    /// <summary>A valid seed duplicated an already accepted current identity.</summary>
    Duplicate = 1,
    /// <summary>The seed failed the current decoding or canonicalization contract.</summary>
    Rejected = 2
}

/// <summary>Provenance-preserving import attribution without untrusted exception text.</summary>
public sealed class EvolutionRepertoireImportDecision
{
    internal EvolutionRepertoireImportDecision(string sourceGenomeId, string? currentGenomeId, EvolutionRepertoireImportStatus status)
    { SourceGenomeId = sourceGenomeId; CurrentGenomeId = currentGenomeId; Status = status; }
    /// <summary>Gets the original source entry identity.</summary>
    public string SourceGenomeId { get; }
    /// <summary>Gets the current canonical identity, or null when current validation failed.</summary>
    public string? CurrentGenomeId { get; }
    /// <summary>Gets whether this source entry supplied a new, duplicate or rejected seed.</summary>
    public EvolutionRepertoireImportStatus Status { get; }
}

/// <summary>The bounded outcome of revalidating portable seeds under the current task.</summary>
public sealed class EvolutionRepertoireImport<TGenome>
{
    internal EvolutionRepertoireImport(IEnumerable<EvolutionCanonicalGenome<TGenome>> seeds,
        IEnumerable<EvolutionRepertoireImportDecision> decisions, bool exactScopeMatch, EvolutionRepertoireProvenance provenance)
    {
        Seeds = Array.AsReadOnly(seeds.ToArray()); Decisions = Array.AsReadOnly(decisions.ToArray());
        RejectedCount = Decisions.Count(decision => decision.Status == EvolutionRepertoireImportStatus.Rejected);
        DuplicateCount = Decisions.Count(decision => decision.Status == EvolutionRepertoireImportStatus.Duplicate);
        IsExactScopeMatch = exactScopeMatch; SourceProvenance = provenance;
    }

    /// <summary>Gets immutable current-task canonical seeds, which still require fresh evaluation.</summary>
    public IReadOnlyList<EvolutionCanonicalGenome<TGenome>> Seeds { get; }
    /// <summary>Gets one source/current identity decision for every imported entry, including rejected entries.</summary>
    public IReadOnlyList<EvolutionRepertoireImportDecision> Decisions { get; }
    /// <summary>Gets how many seeds failed current decoding, canonicalization or round-trip validation.</summary>
    public int RejectedCount { get; }
    /// <summary>Gets how many valid seeds duplicated an already accepted current identity.</summary>
    public int DuplicateCount { get; }
    /// <summary>Gets whether all declared source and target applicability facets match.</summary>
    public bool IsExactScopeMatch { get; }
    /// <summary>Gets prior-information provenance for fair warm/cold cost reporting.</summary>
    public EvolutionRepertoireProvenance SourceProvenance { get; }
}

/// <summary>A bounded, checksummed portable seed repertoire, separate from checkpoints and evaluation caches.</summary>
/// <remarks>
/// Import always decodes and canonicalizes under the current task, whose canonicalizer must enforce current
/// constraints. Matching scope does not import fitness, count a cache hit as a new sample, or authorize deployment.
/// Changed task/evaluator/environment versions may supply seeds only; a changed task id or codec schema requires
/// an explicit external migration. Provenance/checksums detect corruption, not malicious authorship or truthful
/// cost declarations. Do not export secret payloads or final-test-derived information into a search repertoire.
/// </remarks>
public sealed class EvolutionRepertoire
{
    /// <summary>Maximum distinct seeds in one bounded repertoire.</summary>
    public const int MaximumEntries = 256;
    /// <summary>Maximum UTF8 payload size for one seed.</summary>
    public const int MaximumPayloadBytes = 65536;
    /// <summary>Maximum aggregate UTF8 payload bytes in a repertoire.</summary>
    public const int MaximumTotalPayloadBytes = 2 * 1024 * 1024;
    /// <summary>Maximum UTF8 bytes for the complete JSON envelope.</summary>
    public const int MaximumJsonBytes = 8 * 1024 * 1024;

    private readonly ReadOnlyCollection<EvolutionRepertoireEntry> _entries;

    private EvolutionRepertoire(EvolutionReuseScope scope, EvolutionRepertoireProvenance provenance,
        IEnumerable<EvolutionRepertoireEntry> entries)
    {
        Guard.NotNull(scope); Guard.NotNull(provenance);
        EvolutionRepertoireEntry[] snapshot = entries.Take(MaximumEntries + 1).ToArray();
        if (snapshot.Length == 0 || snapshot.Length > MaximumEntries || snapshot.Any(entry => entry is null))
            throw new ArgumentException("A repertoire must contain between one and 256 seeds.", nameof(entries));
        if (snapshot.Sum(entry => EvolutionReuseEncoding.Utf8.GetByteCount(entry.Payload)) > MaximumTotalPayloadBytes)
            throw new ArgumentException("A repertoire exceeds its aggregate payload budget.", nameof(entries));
        if (snapshot.Select(entry => entry.SourceGenomeId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("A repertoire cannot contain duplicate source identities.", nameof(entries));
        Scope = scope; Provenance = provenance;
        _entries = Array.AsReadOnly(snapshot.OrderBy(entry => entry.SourceGenomeId, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Gets the source applicability declaration.</summary>
    public EvolutionReuseScope Scope { get; }
    /// <summary>Gets the retained source provenance and prior cost declaration.</summary>
    public EvolutionRepertoireProvenance Provenance { get; }
    /// <summary>Gets immutable entries sorted by their source canonical identities.</summary>
    public IReadOnlyList<EvolutionRepertoireEntry> Entries => _entries;

    /// <summary>Exports canonical seeds without evaluator calls or fitness; callers may pass selected archive genomes.</summary>
    public static async Task<EvolutionRepertoire> ExportAsync<TGenome>(IEnumerable<TGenome> genomes,
        IEvolutionTask<TGenome> task, IEvolutionGenomeCodec<TGenome> codec, EvolutionReuseScope scope,
        EvolutionRepertoireProvenance provenance, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(genomes); Guard.NotNull(scope); Guard.NotNull(provenance);
        cancellationToken.ThrowIfCancellationRequested();
        scope.Validate(task, codec);
        TGenome[] inputs = genomes.Take(MaximumEntries + 1).ToArray();
        if (inputs.Length == 0 || inputs.Length > MaximumEntries)
            throw new ArgumentException("Export accepts between one and 256 input seeds.", nameof(genomes));
        var entries = new Dictionary<string, EvolutionRepertoireEntry>(StringComparer.Ordinal);
        int totalBytes = 0;
        foreach (TGenome input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EvolutionCanonicalGenome<TGenome> canonical = await task.CanonicalizeAsync(input, cancellationToken).ConfigureAwait(false);
            string payload = codec.Serialize(canonical.Genome);
            var entry = new EvolutionRepertoireEntry(canonical.Id, payload, EvolutionHash.Compute(payload));
            EvolutionCanonicalGenome<TGenome> roundTrip = await task.CanonicalizeAsync(codec.Deserialize(payload), cancellationToken).ConfigureAwait(false);
            scope.Validate(task, codec);
            if (!string.Equals(canonical.Id, roundTrip.Id, StringComparison.Ordinal) ||
                !string.Equals(payload, codec.Serialize(roundTrip.Genome), StringComparison.Ordinal))
                throw new InvalidOperationException("The repertoire codec does not preserve canonical identity and payload.");
            scope.Validate(task, codec);
            if (entries.TryGetValue(canonical.Id, out var previous))
            {
                if (!string.Equals(payload, previous.Payload, StringComparison.Ordinal))
                    throw new InvalidOperationException("Distinct repertoire payloads share a source canonical identity.");
                continue;
            }
            totalBytes += EvolutionReuseEncoding.Utf8.GetByteCount(payload);
            if (totalBytes > MaximumTotalPayloadBytes) throw new ArgumentException("Export exceeds the aggregate payload budget.", nameof(genomes));
            entries.Add(canonical.Id, entry);
        }
        cancellationToken.ThrowIfCancellationRequested();
        scope.Validate(task, codec);
        return new EvolutionRepertoire(scope, provenance, entries.Values);
    }

    /// <summary>Revalidates seeds under current semantics; every accepted seed still needs evaluation in the new run.</summary>
    public async Task<EvolutionRepertoireImport<TGenome>> ImportAsync<TGenome>(IEvolutionTask<TGenome> task,
        IEvolutionGenomeCodec<TGenome> codec, EvolutionReuseScope targetScope, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(targetScope);
        cancellationToken.ThrowIfCancellationRequested();
        targetScope.Validate(task, codec);
        if (!string.Equals(Scope.TaskId, targetScope.TaskId, StringComparison.Ordinal) ||
            !string.Equals(Scope.CodecId, targetScope.CodecId, StringComparison.Ordinal) ||
            !string.Equals(Scope.CodecVersion, targetScope.CodecVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("A different task identity or genome schema requires explicit migration.");
        var accepted = new Dictionary<string, EvolutionCanonicalGenome<TGenome>>(StringComparer.Ordinal);
        var decisions = new List<EvolutionRepertoireImportDecision>();
        foreach (EvolutionRepertoireEntry entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            targetScope.Validate(task, codec);
            EvolutionCanonicalGenome<TGenome> canonical;
            try
            {
                canonical = await task.CanonicalizeAsync(codec.Deserialize(entry.Payload), cancellationToken).ConfigureAwait(false);
                if (string.Equals(Scope.TaskVersion, targetScope.TaskVersion, StringComparison.Ordinal) &&
                    !string.Equals(entry.SourceGenomeId, canonical.Id, StringComparison.Ordinal))
                    throw new InvalidDataException("The source identity is inconsistent with unchanged task semantics.");
                string currentPayload = codec.Serialize(canonical.Genome);
                var current = new EvolutionRepertoireEntry(canonical.Id, currentPayload, EvolutionHash.Compute(currentPayload));
                var roundTrip = await task.CanonicalizeAsync(codec.Deserialize(current.Payload), cancellationToken).ConfigureAwait(false);
                if (!string.Equals(canonical.Id, roundTrip.Id, StringComparison.Ordinal) ||
                    !string.Equals(current.Payload, codec.Serialize(roundTrip.Genome), StringComparison.Ordinal))
                    throw new InvalidDataException("An imported seed failed its current codec round trip.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException && error is not StackOverflowException)
            {
                decisions.Add(new EvolutionRepertoireImportDecision(entry.SourceGenomeId, null, EvolutionRepertoireImportStatus.Rejected));
                continue;
            }
            finally { targetScope.Validate(task, codec); }
            bool duplicate = accepted.ContainsKey(canonical.Id);
            decisions.Add(new EvolutionRepertoireImportDecision(entry.SourceGenomeId, canonical.Id,
                duplicate ? EvolutionRepertoireImportStatus.Duplicate : EvolutionRepertoireImportStatus.Accepted));
            if (!duplicate) accepted.Add(canonical.Id, canonical);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new EvolutionRepertoireImport<TGenome>(accepted.Values, decisions,
            string.Equals(Scope.StableKey, targetScope.StableKey, StringComparison.Ordinal), Provenance);
    }

    /// <summary>Serializes a strict versioned envelope. Code is base64-encoded data, never an executable instruction.</summary>
    public string ToJson()
    {
        string payload = JsonSerializer.Serialize(new
        {
            Scope = Scope.CopyParts(),
            Provenance,
            Entries = _entries.Select(entry => new
            {
                entry.SourceGenomeId,
                entry.PayloadSha256,
                PayloadBase64 = Convert.ToBase64String(EvolutionReuseEncoding.Utf8.GetBytes(entry.Payload))
            }).ToArray()
        }, EvolutionJson.Compact);
        string json = JsonSerializer.Serialize(new { SchemaVersion = 1, Checksum = EvolutionHash.Compute(payload), Payload = payload }, EvolutionJson.Compact);
        ValidateJsonSize(json);
        return json;
    }

    /// <summary>Rejects oversized, malformed, ambiguous, unknown-version or checksum-mismatched envelopes.</summary>
    public static EvolutionRepertoire FromJson(string json)
    {
        ValidateJsonSize(json);
        using JsonDocument envelope = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = envelope.RootElement;
        RequireFields(root, "SchemaVersion", "Checksum", "Payload");
        if (root.GetProperty("SchemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported repertoire schema.");
        string payload = Text(root, "Payload");
        if (!string.Equals(EvolutionHash.Compute(payload), Text(root, "Checksum"), StringComparison.Ordinal))
            throw new InvalidDataException("The repertoire envelope checksum does not match.");
        ValidateJsonSize(payload);
        using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
        root = document.RootElement;
        RequireFields(root, "Scope", "Provenance", "Entries");
        string[] parts = root.GetProperty("Scope").EnumerateArray().Take(13).Select(part => part.GetString()!).ToArray();
        var scope = new EvolutionReuseScope(parts);
        JsonElement p = root.GetProperty("Provenance");
        RequireFields(p, "SourceRunId", "SourceStateHash", "EvidenceSha256", "CreatedAt", "PriorCostUnits", "CostUnit");
        var provenance = new EvolutionRepertoireProvenance(Text(p, "SourceRunId"), Text(p, "SourceStateHash"),
            Text(p, "EvidenceSha256"), p.GetProperty("CreatedAt").GetDateTimeOffset(), p.GetProperty("PriorCostUnits").GetDouble(), Text(p, "CostUnit"));
        var entries = new List<EvolutionRepertoireEntry>();
        int totalBytes = 0;
        foreach (JsonElement entry in root.GetProperty("Entries").EnumerateArray())
        {
            if (entries.Count == MaximumEntries) throw new InvalidDataException("Too many repertoire entries.");
            RequireFields(entry, "SourceGenomeId", "PayloadSha256", "PayloadBase64");
            string base64 = Text(entry, "PayloadBase64");
            if (base64.Length > ((MaximumPayloadBytes + 2) / 3) * 4) throw new InvalidDataException("Oversized encoded payload.");
            byte[] bytes = Convert.FromBase64String(base64);
            if (bytes.Length > MaximumPayloadBytes || (totalBytes += bytes.Length) > MaximumTotalPayloadBytes)
                throw new InvalidDataException("The repertoire payload budget was exceeded.");
            entries.Add(new EvolutionRepertoireEntry(Text(entry, "SourceGenomeId"), EvolutionReuseEncoding.Utf8.GetString(bytes), Text(entry, "PayloadSha256")));
        }
        return new EvolutionRepertoire(scope, provenance, entries);
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()
        ?? throw new InvalidDataException("A required repertoire string is null.");

    private static void RequireFields(JsonElement element, params string[] fields)
    {
        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new InvalidDataException("Duplicate or unknown repertoire field.");
        if (remaining.Count != 0) throw new InvalidDataException("A required repertoire field is missing.");
    }

    private static void ValidateJsonSize(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        if (json.Length > MaximumJsonBytes || EvolutionReuseEncoding.Utf8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("The repertoire JSON exceeds its byte limit.");
    }
}
