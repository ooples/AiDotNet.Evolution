namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>Explicit async embedding cache with synchronous lookups. Not a pure engine distance metric.</summary>
public sealed class EmbeddingCosineGenomeDistance
{
    /// <summary>The metric identity reported as <see cref="Id"/>.</summary>
    public const string MetricId = "program-embedding-cosine";
    private const int MaximumComponents = 1_048_576;
    private readonly IProgramEmbeddingClient _client;
    private readonly IGenomeDistance<ProgramGenome> _fallback;
    private readonly Dictionary<string, EmbeddingVector> _vectors = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _prime = new(1, 1);
    private readonly string _identity;
    private int _components;
    private int? _dimensions;
    private long _epoch, _primeRequests, _cosineComparisons, _fallbackComparisons;

    /// <summary>Creates an empty cache over an embedding client.</summary>
    /// <param name="client">Produces embeddings. Its model and version must not change while this object is in use.</param>
    /// <param name="fallback">The distance used when either program has no cached embedding; defaults to <see cref="ProgramTokenSetDistance"/>.</param>
    /// <param name="cacheCapacity">How many embeddings are kept, 1 to 8192; the oldest are evicted first.</param>
    public EmbeddingCosineGenomeDistance(IProgramEmbeddingClient client,
        IGenomeDistance<ProgramGenome>? fallback = null, int cacheCapacity = 1024)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (cacheCapacity is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
        _client = client; _fallback = fallback ?? new ProgramTokenSetDistance(); CacheCapacity = cacheCapacity;
        _identity = CaptureIdentity();
        VersionHash = EvolutionHash.Combine(new[] { MetricId, "v2", _identity, cacheCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }
    /// <summary>Gets the embedding client.</summary>
    public IProgramEmbeddingClient Client => _client;

    /// <summary>Gets the distance used when an embedding is missing.</summary>
    public IGenomeDistance<ProgramGenome> Fallback => _fallback;

    /// <summary>Gets how many embeddings are kept.</summary>
    public int CacheCapacity { get; }

    /// <summary>Gets the metric identity.</summary>
    public string Id => MetricId;

    /// <summary>Gets the identity of the client, fallback and capacity, which changes when any of them does.</summary>
    public string VersionHash { get; }

    /// <summary>Gets how many embedding requests priming has sent.</summary>
    public long PrimeRequests => Interlocked.Read(ref _primeRequests);

    /// <summary>Gets how many distances were computed from cached embeddings.</summary>
    public long CosineComparisons => Interlocked.Read(ref _cosineComparisons);

    /// <summary>Gets how many distances fell back to <see cref="Fallback"/>.</summary>
    public long FallbackComparisons => Interlocked.Read(ref _fallbackComparisons);

    /// <summary>Gets how many embeddings are cached.</summary>
    public int PrimedCount { get { lock (_gate) return _vectors.Count; } }

    /// <summary>Embeds the programs not yet cached, in one request.</summary>
    /// <param name="genomes">At most 257 programs.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>true</c> when every program is cached afterwards; <c>false</c> when the batch is too large for the
    /// cache, the client failed, or its vectors were inconsistent. A provider failure is reported this way and never
    /// throws.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="genomes"/> is null.</exception>
    /// <exception cref="ArgumentException">More than 257 programs, or a null one.</exception>
    /// <exception cref="InvalidOperationException">The client's or fallback's identity changed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async ValueTask<bool> PrimeAsync(IEnumerable<ProgramGenome> genomes, CancellationToken cancellationToken = default) =>
        (await PrimeWithReceiptAsync(genomes, cancellationToken).ConfigureAwait(false)).Success;

    internal async ValueTask<(bool Success, int Requests)> PrimeWithReceiptAsync(IEnumerable<ProgramGenome> genomes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(genomes);
        cancellationToken.ThrowIfCancellationRequested(); EnsureIdentity();
        var requested = genomes.Take(EmbeddingBatch.MaximumVectors + 1).ToArray();
        if (requested.Length > EmbeddingBatch.MaximumVectors || requested.Any(g => g is null))
            throw new ArgumentException("A priming batch requires at most 257 non-null genomes.", nameof(genomes));
        requested = requested.DistinctBy(g => g.Id).ToArray();
        if (requested.Length > CacheCapacity || requested.Sum(g => (long)g.Source.Length) > ProgramGenome.MaxSourceLength)
            return (false, 0);
        await _prime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureIdentity();
            ProgramGenome[] missing; long epoch;
            lock (_gate) { missing = requested.Where(g => !_vectors.ContainsKey(g.Id)).ToArray(); epoch = _epoch; }
            if (missing.Length == 0) return (true, 0);
            var texts = Array.AsReadOnly(missing.Select(g => g.Language + "\n" + g.Source).ToArray());
            EmbeddingBatch batch;
            Interlocked.Increment(ref _primeRequests);
            try { batch = await _client.EmbedAsync(texts, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { cancellationToken.ThrowIfCancellationRequested(); EnsureIdentity(); return (false, 1); }
            cancellationToken.ThrowIfCancellationRequested(); EnsureIdentity();
            if (batch is null || !batch.Succeeded || batch.Vectors.Count != missing.Length) return (false, 1);
            lock (_gate)
            {
                if (epoch != _epoch) return (false, 1);
                var all = requested.Where(g => _vectors.ContainsKey(g.Id)).Select(g => _vectors[g.Id]).Concat(batch.Vectors).ToArray();
                if (all.Select(v => v.Dimensions).Distinct().Count() != 1 || all.Sum(v => (long)v.Dimensions) > MaximumComponents)
                    return (false, 1);
                if (_dimensions is { } dimensions && all[0].Dimensions != dimensions) return (false, 1);
                _dimensions = all[0].Dimensions;
                // Preserve requested cached neighbours while evicting older unrelated entries.
                var protectedIds = requested.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
                int additional = batch.Vectors.Sum(v => v.Dimensions);
                while (_vectors.Count + missing.Length > CacheCapacity || _components + additional > MaximumComponents)
                {
                    string id = _order.Dequeue();
                    if (protectedIds.Contains(id)) { _order.Enqueue(id); continue; }
                    _components -= _vectors[id].Dimensions; _vectors.Remove(id);
                }
                for (int i = 0; i < missing.Length; i++)
                { _vectors.Add(missing[i].Id, batch.Vectors[i]); _order.Enqueue(missing[i].Id); _components += batch.Vectors[i].Dimensions; }
            }
            return (true, 1);
        }
        finally { _prime.Release(); }
    }

    /// <summary>Reports whether a program's embedding is cached.</summary>
    /// <param name="genome">The program.</param>
    /// <returns><c>true</c> when cached.</returns>
    public bool IsPrimed(ProgramGenome genome)
    { ArgumentNullException.ThrowIfNull(genome); EnsureIdentity(); lock (_gate) return _vectors.ContainsKey(genome.Id); }

    /// <summary>Empties the cache. A priming call already in flight will not repopulate it.</summary>
    public void Clear() { lock (_gate) { _vectors.Clear(); _order.Clear(); _components = 0; _epoch++; } }

    /// <summary>Returns the cosine similarity of two cached programs.</summary>
    /// <param name="first">One program.</param>
    /// <param name="second">The other.</param>
    /// <returns>The similarity, or <c>null</c> when either is not cached.</returns>
    public double? Similarity(ProgramGenome first, ProgramGenome second)
    {
        ArgumentNullException.ThrowIfNull(first); ArgumentNullException.ThrowIfNull(second); EnsureIdentity();
        lock (_gate)
        {
            if (!_vectors.TryGetValue(first.Id, out var a) || !_vectors.TryGetValue(second.Id, out var b) || a.Dimensions != b.Dimensions) return null;
            return EmbeddingVector.CosineSimilarity(a, b);
        }
    }
    /// <summary>Returns one minus cosine similarity when both are cached, otherwise the fallback distance.</summary>
    /// <param name="first">One program.</param>
    /// <param name="second">The other.</param>
    /// <returns>A distance in [0, 1].</returns>
    /// <exception cref="InvalidOperationException">The fallback returned a value outside [0, 1].</exception>
    public double Distance(ProgramGenome first, ProgramGenome second)
    {
        if (Similarity(first, second) is { } cosine) { Interlocked.Increment(ref _cosineComparisons); return Math.Clamp(1 - cosine, 0, 1); }
        Interlocked.Increment(ref _fallbackComparisons);
        double fallback = _fallback.Distance(first, second);
        if (!double.IsFinite(fallback) || fallback is < 0 or > 1) throw new InvalidOperationException("Fallback distance must be within [0,1].");
        return fallback;
    }
    internal void EnsureIdentity()
    { if (CaptureIdentity() != _identity) throw new InvalidOperationException("Embedding provider or fallback identity changed."); }
    private string CaptureIdentity()
    {
        var parts = new[] { _client.ModelId, _client.VersionHash, _fallback.Id, _fallback.VersionHash };
        foreach (var p in parts) ProgramGuard.NotNullOrWhiteSpace(p);
        return EvolutionHash.Combine(parts);
    }
}
