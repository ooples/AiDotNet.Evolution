// Source: ooples/AiDotNet 66d7602c92101e5ab2bd9db8cfa7f7526fa2c75d:tests/AiDotNet.Tests/UnitTests/Evolution/Programs/PersistentProgramFitnessEvaluatorTests.cs; original BSL license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.IO;
using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNetTests.UnitTests.Evolution.Programs;

public sealed class PersistentProgramFitnessEvaluatorTests
{
    private static readonly ProgramGenome Genome = new("return 1;", ProgramLanguage.CSharp);
    private static EvolutionEvaluationContext Context(long id = 0) => new(id, 1, 2, 1);

    [Fact]
    public async Task Warm_fitness_keeps_original_samples_and_runs_current_correctness()
    {
        var fixture = new Fixture();
        int checks = 0;
        var correctness = new DelegateProgramFitnessEvaluator((_, _, _) =>
        {
            checks++; return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1, costUnits: 3));
        });
        var cold = await new CorrectnessGatedProgramFitnessEvaluator(correctness, fixture.Create("cold")).EvaluateAsync(Genome, Context());
        var warm = await new CorrectnessGatedProgramFitnessEvaluator(correctness, fixture.Create("warm")).EvaluateAsync(Genome, Context());
        Assert.Equal(2, checks); Assert.Equal(1, fixture.Backend.Calls);
        Assert.Equal(8, cold.CostUnits); Assert.Equal(3, warm.CostUnits);
        Assert.Equal(EvolutionMeasurementOriginKind.PersistentReuse, warm.MeasurementOrigin!.Kind);
        Assert.Equal(cold.MeasurementOrigin!.SampleSetHash, warm.MeasurementOrigin.SampleSetHash);
        Assert.Equal(5, warm.MeasurementOrigin.OriginalCostUnits);
        Assert.Equal(2, fixture.Store.Reads); Assert.Equal(1, fixture.Store.Writes);
        Assert.Equal(1, fixture.Evidence.Verifies); Assert.Equal(1, fixture.Evidence.Retains);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Standalone_engine_rechecks_correctness_and_accounts_only_current_work(bool passes)
    {
        var fixture = new Fixture(); int checks = 0;
        var ledgers = new List<EvolutionResourceLedger>();
        foreach (string run in new[] { "cold", "warm" })
        {
            // Engine evaluation IDs restart at zero. Each independent engine owns a distinct run ledger;
            // the durable evidence store is shared, not the engine's exactly-once operation namespace.
            var ledger = new EvolutionResourceLedger(run, fixture.Ledger.Limits); ledgers.Add(ledger);
            var archive = new MapElitesArchive<ProgramGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 1, 1) });
            var correctness = new DelegateProgramFitnessEvaluator((_, _, _) =>
            { checks++; return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, passes ? 1 : 0, costUnits: 3)); });
            var engine = ProgramEvolution.CreateEngine(new NoProposals(ledger), correctness, fixture.Create(run, ledger: ledger),
                new ProgramEvolutionResourceOptions(ledger, 8, "cost-v1"), Options(), _ => archive);
            await engine.RunAsync(new[] { Genome });
            Assert.Equal(passes, archive.Best is not null);
        }
        Assert.Equal(2, checks); Assert.Equal(passes ? 1 : 0, fixture.Backend.Calls);
        Assert.Equal(passes ? 2 : 0, fixture.Store.Reads);
        Assert.Equal(passes ? 11m : 6m, ledgers.Sum(ledger => ledger.Snapshot().Spent["cost_units"]));
        Assert.Equal(passes ? 3m : 0m, ledgers.Sum(ledger => ledger.Snapshot().Spent["cache_store_invocations"]));
        Assert.Equal(passes ? 2m : 0m, ledgers.Sum(ledger => ledger.Snapshot().Spent[PersistentProgramFitnessEvaluator.EvidenceInvocationResource]));
        Assert.All(ledgers, ledger => Assert.Equal(0, ledger.Snapshot().Unknown));
    }

    [Theory]
    [InlineData("memo")]
    [InlineData("resume")]
    [InlineData("checkpoint")]
    public void Standalone_engine_refuses_bypassed_freshness_or_uncoordinated_checkpointing_before_work(string mode)
    {
        var fixture = new Fixture(); var search = Options();
        search.EnableEvaluationCache = mode == "memo";
        search.Resume = mode == "resume";
        search.CheckpointInterval = mode == "checkpoint" ? 1 : 0;
        var correctness = new DelegateProgramFitnessEvaluator((_, _, _) => throw new InvalidOperationException("Must not evaluate."));
        Assert.Throws<ArgumentException>(() => ProgramEvolution.CreateEngine(new NoProposals(fixture.Ledger), correctness,
            fixture.Create("run"), new ProgramEvolutionResourceOptions(fixture.Ledger, 8, "cost-v1"), search,
            _ => new MapElitesArchive<ProgramGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 1, 1) })));
        Assert.Equal(0, fixture.Backend.Calls); Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Ledger.Snapshot().Admitted);
    }

    [Fact]
    public void Standalone_engine_rejects_split_persistence_ledger_before_any_work()
    {
        var fixture = new Fixture();
        var other = new EvolutionResourceLedger("other", fixture.Ledger.Limits);
        var correctness = new DelegateProgramFitnessEvaluator((_, _, _) => throw new InvalidOperationException("Must not evaluate."));
        Assert.Throws<ArgumentException>(() => ProgramEvolution.CreateEngine(new NoProposals(other), correctness,
            fixture.Create("run"), new ProgramEvolutionResourceOptions(other, 8, "cost-v1"), Options(),
            _ => throw new InvalidOperationException("Must not create archive.")));
        Assert.Equal(0, fixture.Backend.Calls);
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Ledger.Snapshot().Admitted);
        Assert.Equal(0, other.Snapshot().Admitted);
    }

    [Fact]
    public void Standalone_engine_cannot_hide_fitness_reuse_behind_a_delegate_and_skip_correctness()
    {
        var fixture = new Fixture();
        var persistent = fixture.Create("run");
        var wrapped = new DelegateProgramFitnessEvaluator(persistent.EvaluateAsync);
        var search = Options(); search.EnableEvaluationCache = true;
        Assert.Throws<ArgumentException>(() => ProgramEvolution.CreateEngine(new NoProposals(fixture.Ledger), wrapped, wrapped,
            new ProgramEvolutionResourceOptions(fixture.Ledger, 8, "cost-v1"), search,
            _ => throw new InvalidOperationException("Must not create archive.")));
        Assert.Equal(0, fixture.Backend.Calls);
        Assert.Equal(0, fixture.Ledger.Snapshot().Admitted);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("force-fresh")]
    [InlineData("missing-evidence")]
    [InlineData("evidence-io")]
    [InlineData("changed-source")]
    [InlineData("changed-description")]
    [InlineData("changed-scope")]
    public async Task Ineligible_evidence_requires_a_fresh_measurement(string reason)
    {
        var fixture = new Fixture();
        var cold = await fixture.Create("cold").EvaluateAsync(Genome, Context());
        if (reason == "expired") fixture.Now += TimeSpan.FromHours(2);
        if (reason == "missing-evidence") fixture.Evidence.Available = false;
        if (reason == "evidence-io") fixture.Evidence.Failure = new IOException("unavailable");
        if (reason == "changed-scope") fixture.Backend.Scope = Scope(fixture.Backend, "new-data");
        var genome = reason == "changed-source" ? new ProgramGenome("return 2;", ProgramLanguage.CSharp) :
            reason == "changed-description" ? new ProgramGenome(Genome.Source, Genome.Language, "new description") : Genome;
        var result = await fixture.Create("warm", reason == "force-fresh").EvaluateAsync(genome, Context(1));
        Assert.Equal(2, fixture.Backend.Calls); Assert.Equal(5, result.CostUnits);
        Assert.Equal(EvolutionMeasurementOriginKind.Measured, result.MeasurementOrigin!.Kind);
        Assert.NotEqual(cold.MeasurementOrigin!.SampleSetHash, result.MeasurementOrigin.SampleSetHash);
        Assert.Equal(reason == "force-fresh" ? 1 : 2, fixture.Store.Reads);
    }

    [Fact]
    public async Task Evidence_verification_cannot_extend_the_original_measurement_lifetime()
    {
        var fixture = new Fixture(); await fixture.Create("cold").EvaluateAsync(Genome, Context());
        fixture.Evidence.AfterVerify = () => fixture.Now += TimeSpan.FromHours(2);
        var result = await fixture.Create("warm").EvaluateAsync(Genome, Context(1));
        Assert.Equal(2, fixture.Backend.Calls);
        Assert.Equal(EvolutionMeasurementOriginKind.Measured, result.MeasurementOrigin!.Kind);
    }

    [Theory]
    [InlineData(null, 1, false)]
    [InlineData(0.1, 1, true)]
    [InlineData(0.6, 1, false)]
    [InlineData(0.1, 2, false)]
    public async Task Existing_samples_require_explicit_sufficient_uncertainty_evidence(double? standardError, int minimumSamples, bool eligible)
    {
        var fixture = new Fixture(); fixture.Backend.StandardError = standardError;
        var policy = new EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode.ExistingSamples, TimeSpan.FromHours(1), minimumSamples, 0.5);
        var original = await fixture.Create("cold", policy: policy).EvaluateAsync(Genome, Context());
        var result = await fixture.Create("warm", policy: policy).EvaluateAsync(Genome, Context(1));
        Assert.Equal(eligible ? 1 : 2, fixture.Backend.Calls);
        Assert.Equal(eligible ? EvolutionMeasurementOriginKind.PersistentReuse : EvolutionMeasurementOriginKind.Measured, result.MeasurementOrigin!.Kind);
        if (eligible) Assert.Equal(original.MeasurementOrigin!.SampleSetHash, result.MeasurementOrigin.SampleSetHash);
    }

    [Theory]
    [InlineData("no-origin")]
    [InlineData("reused-origin")]
    [InlineData("wrong-scope")]
    [InlineData("rejected")]
    [InlineData("infeasible")]
    [InlineData("no-raw")]
    [InlineData("invalid-digest")]
    [InlineData("disabled")]
    [InlineData("oversized")]
    public async Task Fresh_results_are_never_fabricated_into_eligible_evidence(string reason)
    {
        var fixture = new Fixture(); fixture.Backend.Mode = reason;
        if (reason == "no-raw") fixture.Evidence.Digest = null;
        if (reason == "invalid-digest") fixture.Evidence.Digest = "not-a-digest";
        var genome = reason == "oversized" ? new ProgramGenome(new string('x', 70000), ProgramLanguage.CSharp) : Genome;
        var result = await fixture.Create("cold", disabled: reason == "disabled").EvaluateAsync(genome, Context());
        Assert.Equal(1, fixture.Backend.Calls); Assert.Equal(5, result.CostUnits); Assert.Equal(0, fixture.Store.Writes);
        Assert.Equal(reason is "disabled" or "oversized" ? 0 : 1, fixture.Store.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identity_drift_is_checked_even_before_a_warm_lookup(bool evidence)
    {
        var fixture = new Fixture(); var evaluator = fixture.Create("run");
        await evaluator.EvaluateAsync(Genome, Context());
        if (evidence) fixture.Evidence.VersionHash = "changed"; else fixture.Backend.VersionHash = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => evaluator.EvaluateAsync(Genome, Context(1)).AsTask());
        Assert.Equal(1, fixture.Store.Reads); Assert.Equal(1, fixture.Backend.Calls);
    }

    [Fact]
    public async Task Exhausted_evidence_budget_declines_reuse_before_dispatch()
    {
        var fixture = new Fixture(evidenceBudget: 1);
        await fixture.Create("cold").EvaluateAsync(Genome, Context());
        await fixture.Create("warm").EvaluateAsync(Genome, Context(1));
        Assert.Equal(2, fixture.Backend.Calls); Assert.Equal(0, fixture.Evidence.Verifies);
        Assert.Equal(1, fixture.Evidence.Retains); Assert.Equal(1, fixture.Store.Writes);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_a_cache_miss()
    {
        var fixture = new Fixture(); await fixture.Create("cold").EvaluateAsync(Genome, Context());
        fixture.Evidence.Failure = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Create("warm").EvaluateAsync(Genome, Context()).AsTask());
        Assert.Equal(1, fixture.Backend.Calls);
    }

    [Fact]
    public void Invalid_scope_or_undeclared_evidence_budget_is_refused()
    {
        var fixture = new Fixture(); fixture.Backend.VersionHash = "changed";
        Assert.Throws<ArgumentException>(() => fixture.Create("run"));
        fixture.Backend.Scope = Scope(fixture.Backend);
        Assert.Throws<ArgumentException>(() => new PersistentProgramFitnessEvaluator(fixture.Backend, fixture.Store, fixture.Evidence,
            fixture.Backend.Scope, new(EvolutionEvaluationReuseMode.Deterministic, TimeSpan.FromHours(1)),
            new EvolutionResourceLedger("run", EvolutionResources.Of("cache_store_invocations", 10)), "stats-v1", "run"));
    }

    private static EvolutionReuseScope Scope(Backend backend, string data = "data-v1") => new(backend.Id, backend.VersionHash,
        backend.VersionHash, new ProgramGenomeCodec().Id, new ProgramGenomeCodec().VersionHash, "constraints-v1", data,
        "fidelity-v1", "compiler-na-v1", "runtime-na-v1", "hardware-na-v1", "correctness-v1");

    private static EvolutionEngineOptions Options() => new()
    { MaxEvaluationAttempts = 1, MaxProposals = 1, MaxGenerations = 1, EnableEvaluationCache = false };

    private sealed class NoProposals(EvolutionResourceLedger ledger) : IProgramVariationOperator, IProgramResourceLedgerProvider, IEvolutionProposalCostProvider
    {
        public EvolutionResourceLedger Ledger => ledger;
        public string CostUnitVersionHash => "cost-v1";
        public EvolutionProposalCost GetProposalCost(long generation) => throw new InvalidOperationException("No proposals allowed.");
        public string Id => "no-proposals";
        public string VersionHash => "no-proposals-v1";
        public ValueTask<ProgramGenome> ProposeAsync(EvolutionVariationContext<ProgramGenome> context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Only the authored seed may be evaluated.");
        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult) { }
        public ProgramEvolutionLlmUsage GetUsage() => new();
    }

    private sealed class Fixture
    {
        internal DateTimeOffset Now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        internal readonly Backend Backend = new();
        internal readonly MemoryStore Store = new();
        internal readonly EvidenceStore Evidence = new();
        internal readonly EvolutionResourceLedger Ledger;
        internal Fixture(decimal evidenceBudget = 100)
        {
            Backend.Scope = Scope(Backend); Backend.Clock = () => Now;
            Ledger = new EvolutionResourceLedger("campaign", new EvolutionResources(new Dictionary<string, decimal>
            { ["cache_store_invocations"] = 100, [PersistentProgramFitnessEvaluator.EvidenceInvocationResource] = evidenceBudget, ["cost_units"] = 100 }));
        }
        internal PersistentProgramFitnessEvaluator Create(string run, bool forceFresh = false, bool disabled = false, EvolutionResourceLedger? ledger = null,
            EvolutionEvaluationReusePolicy? policy = null) => new(
            Backend, Store, Evidence, Backend.Scope, policy ?? new(disabled ? EvolutionEvaluationReuseMode.Disabled : EvolutionEvaluationReuseMode.Deterministic,
                TimeSpan.FromHours(1)), ledger ?? Ledger, "stats-v1", run, forceFresh, () => Now);
    }

    private sealed class Backend : IProgramFitnessEvaluator
    {
        public string Id => "authored-fitness";
        public string VersionHash { get; set; } = "fitness-v1";
        internal EvolutionReuseScope Scope = null!;
        internal Func<DateTimeOffset> Clock = null!;
        internal int Calls;
        internal double? StandardError;
        internal string Mode = "valid";
        public ValueTask<EvolutionTaskResult> EvaluateAsync(ProgramGenome candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            var result = new EvolutionTaskResult(Mode == "rejected" ? EvolutionEvaluationStatus.Rejected : EvolutionEvaluationStatus.Completed,
                1, descriptors: new Dictionary<string, double> { ["x"] = 0.5 }, costUnits: 5,
                constraintViolations: Mode == "infeasible" ? new[] { 1.0 } : null);
            if (Mode == "no-origin") return new(result);
            var origin = new EvolutionMeasurementOrigin(Mode == "wrong-scope" ? EvolutionHash.Compute("wrong") : Scope.StableKey,
                "acquisition", "evaluation-" + Calls, new[] { "sample-" + Calls }, Clock(), 5, "cost-v1", "stats-v1", standardError: StandardError);
            if (Mode == "reused-origin") origin = origin.AsReused(EvolutionMeasurementOriginKind.PersistentReuse);
            return new(result.WithMeasurementOrigin(origin));
        }
    }

    private sealed class MemoryStore : IEvolutionEvaluationStore
    {
        private readonly Dictionary<string, EvolutionEvaluationCacheRecord> _records = new();
        internal int Reads, Writes;
        public ValueTask<EvolutionEvaluationCacheRecord?> ReadAsync(EvolutionEvaluationCacheKey key, CancellationToken cancellationToken = default)
        { Reads++; return new(_records.TryGetValue(key.StableKey, out var record) ? record : null); }
        public ValueTask<bool> TryWriteAsync(EvolutionEvaluationCacheRecord record, CancellationToken cancellationToken = default)
        { Writes++; _records[record.Key.StableKey] = record; return new(true); }
    }

    // Scripted raw-evidence availability, not a real runtime benchmark or production evidence store.
    private sealed class EvidenceStore : IProgramMeasurementEvidenceStore
    {
        public string VersionHash { get; set; } = "raw-v1";
        internal string? Digest = EvolutionHash.Compute("authored-raw-observation");
        internal bool Available = true;
        internal Exception? Failure;
        internal Action? AfterVerify;
        internal int Retains, Verifies;
        public ValueTask<string?> RetainAsync(ProgramGenome candidate, EvolutionTaskResult measurement, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        { Retains++; if (Failure is not null) throw Failure; return new(Digest); }
        public ValueTask<bool> VerifyAsync(ProgramGenome candidate, EvolutionTaskResult measurement, string evidenceSha256, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        { Verifies++; if (Failure is not null) throw Failure; AfterVerify?.Invoke(); return new(Available && evidenceSha256 == Digest); }
    }
}
