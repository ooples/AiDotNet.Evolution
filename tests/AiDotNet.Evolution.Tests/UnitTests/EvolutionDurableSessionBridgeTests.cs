using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionDurableSessionBridgeTests
{
    [Fact]
    public async Task BorrowedWireEndpointDeliversDecodedWorkIntoTheOriginalEngine()
    {
        using var fixture = new Fixture(); using var session = Session(); using var coordinator = fixture.Open(session);
        var bridge = Bridge(session, coordinator); using var endpoint = new EvolutionWorkProtocol(coordinator);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ask = Assert.Single(await session.AskAsync(1, guard.Token)); Assert.True(bridge.Enqueue(ask));
        var claim = new JsonObject
        {
            ["id"] = 1,
            ["protocol"] = 1,
            ["op"] = "claim",
            ["worker"] = new JsonObject
            { ["workerId"] = "wire-worker", ["compatibilityHash"] = session.CompatibilityHash }
        };
        var claimed = JsonNode.Parse(endpoint.ProcessJson(claim.ToJsonString()))!;
        Assert.True(claimed["ok"]!.GetValue<bool>());
        JsonNode lease = claimed["lease"]!;
        var envelope = EvolutionDurableEvaluationPayload.FromJson(lease["payload"]!.GetValue<string>());
        Assert.Equal(ask.Context.SeedStream, envelope.Context.SeedStream);
        var commit = new JsonObject
        {
            ["id"] = 2,
            ["protocol"] = 1,
            ["op"] = "commit",
            ["identity"] = lease["identity"]!.DeepClone(),
            ["workerId"] = "wire-worker",
            ["payload"] = envelope.GenomePayload,
            ["provenance"] = "wire-decoder-v1",
            ["actual"] = new JsonObject { ["cost_units"] = "2" },
            ["outcome"] = "completed"
        };
        Assert.Equal("accepted", JsonNode.Parse(endpoint.ProcessJson(commit.ToJsonString()))!["disposition"]!.GetValue<string>());
        Assert.Equal(1, bridge.DeliverAvailableResults());
        Assert.Equal(7, (await session.Completion).Best!.Evaluation.Quality);
        endpoint.Dispose(); // Borrowed coordinator is still owned by the application.
        Assert.Equal(2, coordinator.Resources.Spent["cost_units"]);
        Assert.Equal(0, bridge.DeliverAvailableResults());
    }

    [Fact]
    public async Task RecoveredCoordinatorDeliversOnlyThroughOriginalLiveSessionTicket()
    {
        using var fixture = new Fixture(); using var session = Session();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        EvolutionAskItem<int> ask = Assert.Single(await session.AskAsync(1, guard.Token));
        EvolutionWorkLease lease;
        using (var coordinator = fixture.Open(session))
        {
            var bridge = Bridge(session, coordinator);
            Assert.Equal(session.InstanceId, coordinator.SourceSessionId);
            Assert.True(bridge.Enqueue(ask)); Assert.True(bridge.Enqueue(ask));
            lease = coordinator.Claim(Worker(session, "a"))!;
            Assert.NotEqual(ask.WorkIdentity!.LeaseId, lease.Identity.LeaseId);
            EvolutionDurableEvaluationPayload payload = EvolutionDurableEvaluationPayload.FromJson(lease.Payload);
            Assert.Equal("7", payload.GenomePayload);
            Assert.Equal(ask.Candidate.CanonicalGenome.Id, payload.CanonicalGenomeId);
            Assert.Equal(ask.Context.RootSeed, payload.Context.RootSeed);
            Assert.Equal(ask.Context.SeedStream, payload.Context.SeedStream);
            Assert.Equal(ask.Context.CreateRandom().NextDouble(), payload.Context.CreateRandom().NextDouble());
        }
        using var restored = fixture.Open(session);
        var resumed = Bridge(session, restored);
        Assert.Equal(EvolutionWorkCommitDisposition.Accepted, restored.Commit(lease.Identity, "a", "7", "receipt", Cost(2)));
        Assert.Equal(1, resumed.DeliverAvailableResults()); Assert.Equal(0, resumed.DeliverAvailableResults());
        Assert.Equal(7, (await session.Completion).Best!.Evaluation.Quality);
        Assert.Equal(2, restored.Resources.Spent["cost_units"]); Assert.False(resumed.Enqueue(ask));
    }

    [Fact]
    public void ANewEngineWithIdenticalRunAndCompatibilityRequiresAnExplicitFork()
    {
        using var fixture = new Fixture(); using var first = Session(); using var other = Session();
        using (var coordinator = fixture.Open(first)) { _ = Bridge(first, coordinator); }
        using var restored = fixture.Open(other);
        Assert.Equal(first.RunId, other.RunId); Assert.Equal(first.CompatibilityHash, other.CompatibilityHash);
        Assert.NotEqual(first.InstanceId, other.InstanceId);
        Assert.Contains("fork", Assert.Throws<InvalidOperationException>(() => Bridge(other, restored)).Message);
        Assert.Equal(first.InstanceId, restored.SourceSessionId);
        _ = Bridge(first, restored);
        Assert.Throws<InvalidOperationException>(() => restored.Enqueue(99, 1, "id", "payload", new(), Cost(1), Cost(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnqueuePublicationFailureCanRetryTheSameOriginalAsk(bool afterPublish)
    {
        using var fixture = new Fixture(); using var session = Session();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        EvolutionAskItem<int> ask = Assert.Single(await session.AskAsync(1, guard.Token));
        using (var coordinator = fixture.Open(session))
        {
            var bridge = Bridge(session, coordinator);
            coordinator.Publishing = published => { if (published == afterPublish) throw new IOException("enqueue acknowledgement failed"); };
            Assert.Throws<IOException>(() => bridge.Enqueue(ask));
        }
        using var restored = fixture.Open(session); var resumed = Bridge(session, restored);
        Assert.True(resumed.Enqueue(ask));
        var lease = restored.Claim(Worker(session, "a"))!;
        Assert.Equal(1, restored.Resources.Admitted);
        restored.Commit(lease.Identity, "a", "7", "receipt", Cost(2));
        Assert.Equal(1, resumed.DeliverAvailableResults()); Assert.Equal(7, (await session.Completion).Best!.Evaluation.Quality);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceTellAcknowledgementFailureCannotDeliverOrChargeTwice(bool afterPublish)
    {
        using var fixture = new Fixture(); using var session = Session();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ask = Assert.Single(await session.AskAsync(1, guard.Token));
        using (var coordinator = fixture.Open(session))
        {
            var bridge = Bridge(session, coordinator); bridge.Enqueue(ask);
            var lease = coordinator.Claim(Worker(session, "a"))!;
            coordinator.Commit(lease.Identity, "a", "7", "receipt", Cost(2));
            coordinator.Publishing = published => { if (published == afterPublish) throw new IOException("source acknowledgement failed"); };
            Assert.Throws<IOException>(() => bridge.DeliverAvailableResults());
            Assert.Equal(7, (await session.Completion).Best!.Evaluation.Quality);
        }
        using var restored = fixture.Open(session); var resumed = Bridge(session, restored);
        Assert.Equal(0, resumed.DeliverAvailableResults()); Assert.Equal(0, resumed.DeliverAvailableResults());
        Assert.Equal(1, restored.Resources.Settled); Assert.Equal(2, restored.Resources.Spent["cost_units"]);
    }

    [Fact]
    public async Task EngineTimeoutCancelsOldDurableWorkWithoutOverwritingReplacementOrRefundingCost()
    {
        using var fixture = new Fixture(); using var session = Session(retry: true);
        using var coordinator = fixture.Open(session); var bridge = Bridge(session, coordinator);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = Assert.Single(await session.AskAsync(1, guard.Token)); bridge.Enqueue(first);
        var original = coordinator.Claim(Worker(session, "a"))!;
        var second = Assert.Single(await session.AskAsync(1, guard.Token));
        Assert.Equal(2, second.Context.AttemptCount); Assert.Equal(1, bridge.ReconcileExpiredWork());
        Assert.Equal(EvolutionWorkHeartbeat.Canceled, coordinator.Heartbeat(original.Identity, "a"));
        Assert.False(bridge.Enqueue(first)); Assert.True(bridge.Enqueue(second));
        var replacement = coordinator.Claim(Worker(session, "b"))!;
        Assert.Equal(2, EvolutionDurableEvaluationPayload.FromJson(replacement.Payload).Context.AttemptCount);
        Assert.Equal(EvolutionWorkCommitDisposition.Stale, coordinator.Commit(original.Identity, "a", "999", "late", Cost(3)));
        Assert.Equal(EvolutionWorkCommitDisposition.Accepted, coordinator.Commit(replacement.Identity, "b", "2", "current", Cost(4)));
        Assert.Equal(1, bridge.DeliverAvailableResults()); Assert.Equal(2, (await session.Completion).Best!.Evaluation.Quality);
        Assert.Equal(7, coordinator.Resources.Spent["cost_units"]); Assert.Equal(0, coordinator.Resources.Reserved["cost_units"]);
    }

    [Fact]
    public async Task DecoderFailureKeepsDurableResultAndReceiptForExplicitHandling()
    {
        using var fixture = new Fixture(); using var session = Session(); using var coordinator = fixture.Open(session);
        var bridge = Bridge(session, coordinator);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ask = Assert.Single(await session.AskAsync(1, guard.Token)); bridge.Enqueue(ask);
        var lease = coordinator.Claim(Worker(session, "a"))!;
        coordinator.Commit(lease.Identity, "a", "not a number", "receipt", Cost(2));
        Assert.Throws<FormatException>(() => bridge.DeliverAvailableResults());
        Assert.Equal("not a number", coordinator.GetResult(ask.EvaluationId, 1)!.Payload);
        Assert.Equal(2, coordinator.Resources.Spent["cost_units"]);
        var rejecting = new EvolutionDurableSessionBridge<int>(session, coordinator,
            _ => EvolutionTaskResult.Failed("invalid_external_result", "Explicitly rejected by the caller's decoder."), new(), Cost(1), Cost(5));
        Assert.Equal(1, rejecting.DeliverAvailableResults()); Assert.Null((await session.Completion).Best);
    }

    [Fact]
    public async Task ForeignAndAlteredAskContextsCannotBeQueuedAsOriginalWork()
    {
        using var fixture = new Fixture(); using var session = Session(); using var foreign = Session();
        using var coordinator = fixture.Open(session); var bridge = Bridge(session, coordinator);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ask = Assert.Single(await session.AskAsync(1, guard.Token));
        var wrong = Assert.Single(await foreign.AskAsync(1, guard.Token));
        Assert.False(bridge.Enqueue(wrong));
        Assert.False(bridge.Enqueue(new EvolutionAskItem<int>(ask.Candidate, ask.Context)));
        Assert.False(bridge.Enqueue(new EvolutionAskItem<int>(ask.Candidate,
            new EvolutionEvaluationContext(ask.EvaluationId, 0, 0, 1), ask.WorkIdentity)));
        Assert.True(bridge.Enqueue(ask)); Assert.NotNull(coordinator.Claim(Worker(session, "a")));
    }

    [Fact]
    public void InvalidBridgeConfigurationCannotBindOrRelabelAnUnrelatedStore()
    {
        using var fixture = new Fixture(); using var session = Session(); using var coordinator = fixture.Open(session);
        Assert.Throws<ArgumentException>(() => new EvolutionDurableSessionBridge<int>(session, coordinator, Decode, new(), Cost(6), Cost(5)));
        Assert.Null(coordinator.SourceSessionId);
        using var wrongSession = Session(legacy: true);
        Assert.Throws<ArgumentException>(() => Bridge(wrongSession, coordinator));
        Assert.Null(coordinator.SourceSessionId);
        coordinator.Enqueue(1, 1, "unrelated", "payload", new(), Cost(1), Cost(5));
        Assert.Throws<InvalidOperationException>(() => Bridge(session, coordinator));
    }

    [Fact]
    public void PayloadPreservesAll64SeedBitsAndRejectsMissingLossyOrOversizedContext()
    {
        var context = new EvolutionEvaluationContext(17, ulong.MaxValue, ulong.MaxValue - 1, 2);
        var payload = new EvolutionDurableEvaluationPayload("7", new string('x', 300) + "\n7", context);
        string json = payload.ToJson();
        Assert.Contains("\"18446744073709551615\"", json);
        var restored = EvolutionDurableEvaluationPayload.FromJson(json);
        Assert.Equal(payload.CanonicalGenomeId, restored.CanonicalGenomeId);
        Assert.Equal(context.RootSeed, restored.Context.RootSeed); Assert.Equal(context.SeedStream, restored.Context.SeedStream);
        foreach (string field in new[] { "schema", "genomePayload", "canonicalGenomeId", "evaluationId", "attempt", "rootSeed", "seedStream" })
        {
            JsonObject incomplete = JsonNode.Parse(json)!.AsObject(); incomplete.Remove(field);
            Assert.ThrowsAny<Exception>(() => EvolutionDurableEvaluationPayload.FromJson(incomplete.ToJsonString()));
        }
        Assert.Throws<InvalidDataException>(() => EvolutionDurableEvaluationPayload.FromJson(json.Replace("18446744073709551615", "18446744073709551616")));
        Assert.Throws<InvalidDataException>(() => EvolutionDurableEvaluationPayload.FromJson(json.Replace("18446744073709551615", "01")));
        Assert.Throws<JsonException>(() => EvolutionDurableEvaluationPayload.FromJson(json.Replace("\"18446744073709551615\"", "18446744073709551615")));
        Assert.Throws<ArgumentException>(() => EvolutionDurableEvaluationPayload.FromJson(new string('x', 1024 * 1024 + 1)));
    }

    [Theory]
    [InlineData("old-schema")]
    [InlineData("missing-schema")]
    [InlineData("missing-session")]
    [InlineData("missing-source-ticket")]
    [InlineData("malformed-source-ticket")]
    [InlineData("premature-acknowledgement")]
    public async Task ChecksumValidButIncompatibleOrIncompleteSessionStateFailsClosed(string change)
    {
        using var fixture = new Fixture(); using var session = Session();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ask = Assert.Single(await session.AskAsync(1, guard.Token));
        using (var coordinator = fixture.Open(session)) Bridge(session, coordinator).Enqueue(ask);
        using (var journal = new EvolutionWorkJournal(fixture.DirectoryPath, "unused", 16 * 1024 * 1024))
        {
            JsonObject state = JsonNode.Parse(journal.Payload)!.AsObject();
            JsonObject work = state["Work"]!.AsObject().First().Value!.AsObject();
            switch (change)
            {
                case "old-schema": state["Schema"] = 1; break;
                case "missing-schema": state.Remove("Schema"); break;
                case "missing-session": state.Remove("SourceSessionId"); break;
                case "missing-source-ticket": work.Remove("SourceLeaseId"); break;
                case "malformed-source-ticket": work["SourceLeaseId"] = "not-a-ticket"; break;
                case "premature-acknowledgement": work["SourceTellAccepted"] = true; break;
                default: throw new InvalidOperationException(change);
            }
            journal.Commit(state.ToJsonString());
        }
        Assert.Throws<InvalidDataException>(() => fixture.Open(session));
    }

    private static EvolutionResources Cost(decimal value) => EvolutionResources.Of("cost_units", value);
    private static EvolutionTaskResult Decode(string value) => EvolutionTaskResult.Completed(double.Parse(value, CultureInfo.InvariantCulture), new Dictionary<string, double> { ["x"] = 7 });
    private static EvolutionWorkerProfile Worker(EvolutionSession<int> session, string id) => new(id, session.CompatibilityHash);
    private static EvolutionDurableSessionBridge<int> Bridge(EvolutionSession<int> session, DurableEvolutionWorkCoordinator coordinator) => new(session, coordinator, Decode, new(), Cost(1), Cost(5));
    private static EvolutionSession<int> Session(bool retry = false, bool legacy = false)
    {
        var options = new EvolutionEngineOptions
        {
            RunId = "durable-session",
            Seed = ulong.MaxValue,
            MaxProposals = 1,
            MaxEvaluationAttempts = retry ? 2 : 1,
            MaxRetries = retry ? 1 : 0,
            MaxGenerations = 0,
            CheckpointInterval = 0,
            EvaluationTimeout = TimeSpan.FromSeconds(retry ? 1 : 20)
        };
        EvolutionEngine<int> Factory(IEvolutionTask<int> task) => new(task, new UnusedVariation(),
            _ => new MapElitesArchive<int>(new[] { new EvolutionDescriptorDefinition("x", 0, 10, 10) }), options, genomeCodec: new IntegerCodec());
        return legacy ? new EvolutionSession<int>(Factory, new[] { 7 }, value => value.ToString(CultureInfo.InvariantCulture))
            : new EvolutionSession<int>(Factory, new[] { 7 }, value => value.ToString(CultureInfo.InvariantCulture), new EvolutionExternalTaskIdentity("integer", "task-v1", "evaluator-and-result-codec-v1"));
    }
    private sealed class IntegerCodec : IEvolutionGenomeCodec<int>
    {
        public string Id => "integer";
        public string VersionHash => "v1";
        public string Serialize(int genome) => genome.ToString(CultureInfo.InvariantCulture);
        public int Deserialize(string payload) => int.Parse(payload, CultureInfo.InvariantCulture);
    }
    private sealed class UnusedVariation : IVariationOperator<int>
    {
        public string Id => "unused";
        public string VersionHash => "v1";
        public ValueTask<int> ProposeAsync(EvolutionVariationContext<int> context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Only the seed is admitted.");
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "evolution-session-bridge-" + Guid.NewGuid().ToString("N"));
        internal string DirectoryPath => _path;
        internal DurableEvolutionWorkCoordinator Open(EvolutionSession<int> session) => new(_path, session.RunId, session.CompatibilityHash, Cost(100));
        public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }
    }
}
