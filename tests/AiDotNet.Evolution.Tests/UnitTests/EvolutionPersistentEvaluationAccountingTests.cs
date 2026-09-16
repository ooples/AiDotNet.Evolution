using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed partial class EvolutionPersistentEvaluationTests
{
    [Fact]
    public async Task Coordinator_charges_dispatched_calls_once_and_denies_before_dispatch()
    {
        var store = new StubStore { Read = () => Record() }; var ledger = CacheLedger(2);
        var cache = Cache(store, ledger); var key = Key();
        var hit = await cache.LookupAsync(key, "first", Observed);
        Assert.Equal(EvolutionEvaluationReuseDecision.Eligible, hit.Decision);
        Assert.Equal(EvolutionMeasurementOriginKind.PersistentReuse, hit.ReusedResult!.MeasurementOrigin!.Kind);
        Assert.Equal(0, hit.ReusedResult.CostUnits);
        Assert.Equal(Record().EvidenceSha256, hit.EvidenceSha256);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.LookupAsync(key, "first", Observed).AsTask());
        Assert.Equal(1, store.ReadCalls);
        Assert.Equal(EvolutionEvaluationCacheWriteStatus.Stored, await cache.TryStoreAsync(Record(), "write"));
        Assert.Equal(EvolutionEvaluationReuseDecision.BudgetExhausted, (await cache.LookupAsync(key, "denied", Observed)).Decision);
        Assert.Equal(EvolutionEvaluationCacheWriteStatus.BudgetExhausted, await cache.TryStoreAsync(Record(), "denied-write"));
        Assert.Equal(1, store.ReadCalls); Assert.Equal(1, store.WriteCalls);
        Assert.Equal(2, ledger.Snapshot().Spent[EvolutionPersistentEvaluationCache.StoreInvocationResource]);
        Assert.All(ledger.Snapshot().Receipts, receipt => Assert.Equal(EvolutionResourceStage.Persistence, receipt.Stage));
        Assert.Equal(0, ledger.Snapshot().Unknown);
        var restoredLedger = CacheLedger(3); restoredLedger.RestoreState(ledger.CaptureState());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Cache(store, restoredLedger).LookupAsync(key, "first", Observed).AsTask());
        Assert.Equal(1, store.ReadCalls);
    }

    [Fact]
    public async Task Disabled_and_force_fresh_skip_storage_even_without_budget()
    {
        var store = new StubStore { Read = () => throw new IOException("must not dispatch") }; var ledger = CacheLedger(0);
        var cache = Cache(store, ledger);
        Assert.Equal(EvolutionEvaluationReuseDecision.ForceFresh, (await cache.LookupAsync(Key(), "fresh", Observed, true)).Decision);
        var disabled = new EvolutionPersistentEvaluationCache(store,
            new EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode.Disabled, TimeSpan.FromHours(1)), ledger);
        Assert.Equal(EvolutionEvaluationReuseDecision.Disabled, (await disabled.LookupAsync(Key(), "disabled", Observed)).Decision);
        Assert.Equal(0, store.ReadCalls); Assert.Equal(0, ledger.Snapshot().Admitted);
        Assert.NotEqual(cache.VersionHash, disabled.VersionHash); Assert.Same(disabled.Policy, disabled.Policy);
        Assert.Throws<ArgumentException>(() => new EvolutionPersistentEvaluationCache(store, cache.Policy,
            new EvolutionResourceLedger("wrong", EvolutionResources.Of("cost_units", 1))));
    }

    [Fact]
    public async Task Failed_and_rejected_storage_never_return_a_hit_and_keep_invocation_receipts()
    {
        var store = new StubStore { Read = () => throw new IOException("private path must not escape") }; var ledger = CacheLedger(10);
        var cache = Cache(store, ledger);
        var failed = await cache.LookupAsync(Key(), "failed", Observed);
        Assert.Equal(EvolutionEvaluationReuseDecision.StorageUnavailable, failed.Decision); Assert.Null(failed.ReusedResult); Assert.Null(failed.EvidenceSha256);
        store.Read = () => Record(key: Key(genome: "wrong"));
        var mismatch = await cache.LookupAsync(Key(), "wrong", Observed);
        Assert.Equal(EvolutionEvaluationReuseDecision.KeyMismatch, mismatch.Decision); Assert.Null(mismatch.ReusedResult);
        store.Read = () => null;
        Assert.Equal(EvolutionEvaluationReuseDecision.Missing, (await cache.LookupAsync(Key(), "missing", Observed)).Decision);
        store.Write = () => throw new UnauthorizedAccessException("private path");
        Assert.Equal(EvolutionEvaluationCacheWriteStatus.StorageUnavailable, await cache.TryStoreAsync(Record(), "failed-write"));
        store.Write = () => false;
        Assert.Equal(EvolutionEvaluationCacheWriteStatus.NotStored, await cache.TryStoreAsync(Record(), "refused-write"));
        Assert.Equal(5, ledger.Snapshot().Spent[EvolutionPersistentEvaluationCache.StoreInvocationResource]);
        Assert.Equal(3, ledger.Snapshot().Receipts.Count(receipt => receipt.Outcome == EvolutionResourceOutcome.Failed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_dispatch_cancellation_retains_the_store_invocation_but_pre_cancellation_does_not(bool write)
    {
        var store = new StubStore { Read = () => throw new OperationCanceledException(), Write = () => throw new OperationCanceledException() };
        var ledger = CacheLedger(2); var cache = Cache(store, ledger);
        if (write) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.TryStoreAsync(Record(), "canceled").AsTask());
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.LookupAsync(Key(), "canceled", Observed).AsTask());
        Assert.Equal(1, ledger.Snapshot().Spent[EvolutionPersistentEvaluationCache.StoreInvocationResource]);
        Assert.Equal(EvolutionResourceOutcome.Canceled, Assert.Single(ledger.Snapshot().Receipts).Outcome);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        if (write) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.TryStoreAsync(Record(), "pre-canceled", cancellation.Token).AsTask());
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.LookupAsync(Key(), "pre-canceled", Observed, cancellationToken: cancellation.Token).AsTask());
        Assert.Equal(1, store.ReadCalls + store.WriteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fatal_store_failures_are_not_suppressed_and_dispatched_invocations_remain_charged(bool write)
    {
        var store = new StubStore { Read = () => throw new OutOfMemoryException(), Write = () => throw new OutOfMemoryException() };
        var ledger = CacheLedger(2); var cache = Cache(store, ledger);
        if (write) await Assert.ThrowsAsync<OutOfMemoryException>(() => cache.TryStoreAsync(Record(), "fatal").AsTask());
        else await Assert.ThrowsAsync<OutOfMemoryException>(() => cache.LookupAsync(Key(), "fatal", Observed).AsTask());
        Assert.Equal(1, ledger.Snapshot().Spent[EvolutionPersistentEvaluationCache.StoreInvocationResource]);
    }

    [Fact]
    public async Task Key_factory_revalidates_canonical_bytes_and_scope_without_evaluation()
    {
        var task = new SyntheticEvolutionTask(); var codec = new FactoryCodec();
        var scope = FactoryScope(task, codec); var candidate = await task.CanonicalizeAsync(new TestGenome(1));
        var key = await EvolutionEvaluationCacheKey.CreateAsync(candidate, task, codec, scope, "fixed-v1");
        Assert.Equal(EvolutionHash.Compute("1"), key.GenomePayloadSha256); Assert.Equal(0, task.Calls);
        codec.Decode = _ => new TestGenome(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvolutionEvaluationCacheKey.CreateAsync(candidate, task, codec, scope, "fixed-v1").AsTask());
        codec.Decode = _ => new TestGenome(1); codec.Encode = _ => new string('x', EvolutionRepertoire.MaximumPayloadBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => EvolutionEvaluationCacheKey.CreateAsync(candidate, task, codec, scope, "fixed-v1").AsTask());
        codec.Encode = _ => new string((char)0xd800, 1);
        await Assert.ThrowsAsync<System.Text.EncoderFallbackException>(() => EvolutionEvaluationCacheKey.CreateAsync(candidate, task, codec, scope, "fixed-v1").AsTask());
        codec.Encode = _ => { codec.Version = "changed"; return "1"; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvolutionEvaluationCacheKey.CreateAsync(candidate, task, codec, scope, "fixed-v1").AsTask());
        codec.Version = "factory-v1"; codec.Encode = _ => "1";
        var wrongCanonicalId = new EvolutionCanonicalGenome<TestGenome>(new TestGenome(1), "wrong");
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvolutionEvaluationCacheKey.CreateAsync(wrongCanonicalId, task, codec, scope, "fixed-v1").AsTask());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        codec.Encode = _ => throw new InvalidOperationException("must not dispatch");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvolutionEvaluationCacheKey.CreateAsync(candidate, task, codec, scope, "fixed-v1", cancellation.Token).AsTask());
    }

    private static EvolutionResourceLedger CacheLedger(decimal maximum) => new("cache-test",
        EvolutionResources.Of(EvolutionPersistentEvaluationCache.StoreInvocationResource, maximum));
    private static EvolutionPersistentEvaluationCache Cache(IEvolutionEvaluationStore store, EvolutionResourceLedger ledger) =>
        new(store, new EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode.Deterministic, TimeSpan.FromHours(1)), ledger);

    private sealed class StubStore : IEvolutionEvaluationStore
    {
        internal Func<EvolutionEvaluationCacheRecord?> Read { get; set; } = () => null;
        internal Func<bool> Write { get; set; } = () => true;
        internal int ReadCalls { get; private set; }
        internal int WriteCalls { get; private set; }
        public ValueTask<EvolutionEvaluationCacheRecord?> ReadAsync(EvolutionEvaluationCacheKey key, CancellationToken cancellationToken = default)
        { ReadCalls++; return new(Read()); }
        public ValueTask<bool> TryWriteAsync(EvolutionEvaluationCacheRecord record, CancellationToken cancellationToken = default)
        { WriteCalls++; return new(Write()); }
    }

    private static EvolutionReuseScope FactoryScope(IEvolutionTask<TestGenome> task, IEvolutionGenomeCodec<TestGenome> codec) =>
        new(task.Id, task.VersionHash, task.EvaluatorVersionHash, codec.Id, codec.VersionHash, "constraints-v1", "data-v1", "full-v1",
            "compiler-v1", "runtime-v1", "hardware-v1", "correctness-v1");

    private sealed class FactoryCodec : IEvolutionGenomeCodec<TestGenome>
    {
        internal string Version { get; set; } = "factory-v1";
        internal Func<TestGenome, string> Encode { get; set; } = genome => genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        internal Func<string, TestGenome> Decode { get; set; } = payload => new(int.Parse(payload, System.Globalization.CultureInfo.InvariantCulture));
        public string Id => "factory";
        public string VersionHash => Version;
        public string Serialize(TestGenome genome) => Encode(genome);
        public TestGenome Deserialize(string payload) => Decode(payload);
    }
}
