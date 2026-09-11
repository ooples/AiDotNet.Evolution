using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionRepertoireTests
{
    [Fact]
    public async Task RoundTrip_PreservesProvenanceButAlwaysRequiresFreshFitness()
    {
        var task = new IntTask(); var codec = new IntCodec();
        var repertoire = await EvolutionRepertoire.ExportAsync(new[] { 3, 1, 2, 1 }, task, codec, Scope(task, codec), Provenance());
        string json = repertoire.ToJson();
        var restored = EvolutionRepertoire.FromJson(json);
        Assert.Equal(json, restored.ToJson());
        Assert.Equal(new[] { "1", "2", "3" }, restored.Entries.Select(entry => entry.SourceGenomeId));
        Assert.Equal(0, task.Evaluations);
        var imported = await restored.ImportAsync(task, codec, Scope(task, codec));
        Assert.True(imported.IsExactScopeMatch);
        Assert.Equal(0, imported.RejectedCount);
        Assert.Equal(0, imported.DuplicateCount);
        Assert.Equal(3, imported.Decisions.Count);
        Assert.All(imported.Decisions, decision =>
        {
            Assert.Equal(EvolutionRepertoireImportStatus.Accepted, decision.Status);
            Assert.Equal(decision.SourceGenomeId, decision.CurrentGenomeId);
        });
        Assert.Equal(0, task.Evaluations);
        Assert.Equal(125.5, imported.SourceProvenance.PriorCostUnits);
        Assert.Equal("worker-calls-v1", imported.SourceProvenance.CostUnit);
        Assert.Equal(TimeSpan.Zero, imported.SourceProvenance.CreatedAt.Offset);
        var result = await Engine(task).RunAsync(imported.Seeds.Select(seed => seed.Genome));
        Assert.Equal(3, task.Evaluations);
        Assert.Equal(3, result.Counters.EvaluationAttempts);
        Assert.Equal(3, result.Best!.Candidate.CanonicalGenome.Genome);
        Assert.DoesNotContain("Quality", json);
        Assert.DoesNotContain("Evaluation", json);
    }

    [Fact]
    public async Task ChangedConstraintsAndEvaluator_RevalidateSeedsAndDiscardInadmissibleOnes()
    {
        var source = new IntTask(); var codec = new IntCodec();
        var repertoire = await EvolutionRepertoire.ExportAsync(new[] { 1, 2, 3 }, source, codec, Scope(source, codec), Provenance());
        var target = new IntTask { Version = "task-v2", EvaluatorVersion = "evaluator-v2", Maximum = 2, ReverseQuality = true };
        var imported = await repertoire.ImportAsync(target, codec, Scope(target, codec, constraints: "constraints-v2", data: "data-v2"));
        Assert.False(imported.IsExactScopeMatch);
        Assert.Equal(new[] { 1, 2 }, imported.Seeds.Select(seed => seed.Genome));
        Assert.Equal(1, imported.RejectedCount);
        var rejected = Assert.Single(imported.Decisions, decision => decision.Status == EvolutionRepertoireImportStatus.Rejected);
        Assert.Equal("3", rejected.SourceGenomeId);
        Assert.Null(rejected.CurrentGenomeId);
        var result = await Engine(target).RunAsync(imported.Seeds.Select(seed => seed.Genome));
        Assert.Equal(2, target.Evaluations);
        Assert.Equal(1, result.Best!.Candidate.CanonicalGenome.Genome);
    }

    [Fact]
    public async Task ChangedCanonicalSemantics_DeduplicateCurrentSeeds()
    {
        var source = new IntTask(); var codec = new IntCodec();
        var repertoire = await EvolutionRepertoire.ExportAsync(new[] { 1, 3 }, source, codec, Scope(source, codec), Provenance());
        var target = new IntTask { Version = "modulo-v2", Modulo = true };
        var imported = await repertoire.ImportAsync(target, codec, Scope(target, codec));
        Assert.Equal(1, Assert.Single(imported.Seeds).Genome);
        Assert.Equal(1, imported.DuplicateCount);
        var duplicate = Assert.Single(imported.Decisions, decision => decision.Status == EvolutionRepertoireImportStatus.Duplicate);
        Assert.Equal("3", duplicate.SourceGenomeId);
        Assert.Equal("1", duplicate.CurrentGenomeId);
        Assert.Equal(0, imported.RejectedCount);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("codec")]
    [InlineData("schema")]
    public async Task IncompatibleTaskOrSchema_RequiresExplicitMigration(string change)
    {
        var task = new IntTask(); var codec = new IntCodec();
        var repertoire = await EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, Scope(task, codec), Provenance());
        if (change == "task") task.TaskId = "another-task";
        if (change == "codec") codec.CodecId = "another-codec";
        if (change == "schema") codec.Version = "schema-v2";
        await Assert.ThrowsAsync<InvalidOperationException>(() => repertoire.ImportAsync(task, codec, Scope(task, codec)));
    }

    [Theory]
    [InlineData("export")]
    [InlineData("import")]
    public async Task IdentityDriftDuringCanonicalization_AbortsInsteadOfReturningPartialSuccess(string operation)
    {
        var task = new IntTask(); var codec = new IntCodec(); var scope = Scope(task, codec);
        var repertoire = await EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, scope, Provenance());
        task.OnCanonicalize = () => task.Version = "changed";
        if (operation == "export")
            await Assert.ThrowsAsync<InvalidOperationException>(() => EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, scope, Provenance()));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => repertoire.ImportAsync(task, codec, scope));
    }

    [Theory]
    [InlineData("export")]
    [InlineData("import")]
    public async Task CancellationIsNotARejectedSeed(string operation)
    {
        var task = new IntTask(); var codec = new IntCodec(); var scope = Scope(task, codec);
        var repertoire = await EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, scope, Provenance());
        using var cancellation = new CancellationTokenSource();
        task.OnCanonicalize = cancellation.Cancel;
        if (operation == "export")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, scope, Provenance(), cancellation.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repertoire.ImportAsync(task, codec, scope, cancellation.Token));
    }

    [Theory]
    [InlineData("decode")]
    [InlineData("roundtrip")]
    [InlineData("identity")]
    public async Task MalformedCurrentContracts_DoNotBecomeSeeds(string failure)
    {
        var task = new IntTask(); var codec = new IntCodec();
        var repertoire = await EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, Scope(task, codec), Provenance());
        if (failure == "decode") codec.Decode = _ => throw new InvalidDataException("private diagnostic");
        if (failure == "roundtrip") { codec.Decode = text => int.Parse(text, CultureInfo.InvariantCulture) + 1; task.Version = "changed"; }
        if (failure == "identity") task.Modulo = true;
        // Identity case uses a changed canonical result without declaring a new task version.
        if (failure == "identity") repertoire = Mutate(repertoire, p => ((JsonObject)((JsonArray)p["Entries"]!)[0]!)["SourceGenomeId"] = "incorrect-id");
        var imported = await repertoire.ImportAsync(task, codec, Scope(task, codec));
        Assert.Empty(imported.Seeds);
        Assert.Equal(1, imported.RejectedCount);
    }

    [Fact]
    public async Task FatalEvaluatorInfrastructureFailureIsNotSwallowedAsInvalidInput()
    {
        var task = new IntTask(); var codec = new IntCodec();
        var repertoire = await EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, Scope(task, codec), Provenance());
        task.OnCanonicalize = () => throw new OutOfMemoryException();
        await Assert.ThrowsAsync<OutOfMemoryException>(() => repertoire.ImportAsync(task, codec, Scope(task, codec)));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("empty")]
    [InlineData("payload")]
    [InlineData("utf8")]
    [InlineData("aggregate")]
    [InlineData("unicode")]
    public async Task ExportEnforcesBoundsBeforeRetainingPayloads(string limit)
    {
        var task = new IntTask(); var codec = new IntCodec();
        int[] inputs = limit == "count" ? Enumerable.Range(0, 257).ToArray() : limit == "empty" ? Array.Empty<int>() :
            limit == "aggregate" ? Enumerable.Range(0, 40).ToArray() : new[] { 1 };
        if (limit == "payload") codec.Encode = _ => new string('x', 65537);
        if (limit == "utf8") codec.Encode = _ => new string('\u20ac', 30000);
        if (limit == "unicode") codec.Encode = _ => "\uD800";
        if (limit == "aggregate")
        {
            codec.Encode = n => n.ToString(CultureInfo.InvariantCulture).PadRight(65536, ' ');
            codec.Decode = text => int.Parse(text, CultureInfo.InvariantCulture);
        }
        await Assert.ThrowsAnyAsync<ArgumentException>(() => EvolutionRepertoire.ExportAsync(inputs, task, codec, Scope(task, codec), Provenance()));
    }

    [Fact]
    public async Task ExportRejectsCodecRoundTripAndCanonicalIdentityCollisions()
    {
        var task = new IntTask(); var codec = new IntCodec { Decode = _ => 2 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, Scope(task, codec), Provenance()));
        codec = new IntCodec(); task.FixedIdentity = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvolutionRepertoire.ExportAsync(new[] { 1, 2 }, task, codec, Scope(task, codec), Provenance()));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("version")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("trailing")]
    public async Task JsonEnvelopeRejectsAmbiguityAndCorruption(string failure)
    {
        var task = new IntTask(); var codec = new IntCodec();
        string json = (await EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, Scope(task, codec), Provenance())).ToJson();
        var envelope = JsonNode.Parse(json)!.AsObject();
        if (failure == "checksum") envelope["Checksum"] = new string('0', 64);
        if (failure == "version") envelope["SchemaVersion"] = 2;
        if (failure == "unknown") envelope["Unexpected"] = true;
        if (failure == "missing") envelope.Remove("Checksum");
        if (failure == "null") envelope["Payload"] = null;
        json = envelope.ToJsonString();
        if (failure == "duplicate") json = json.Insert(1, "\"SchemaVersion\":1,");
        if (failure == "trailing") json += "{}";
        Assert.ThrowsAny<Exception>(() => EvolutionRepertoire.FromJson(json));
    }

    [Theory]
    [InlineData("payload-hash")]
    [InlineData("base64")]
    [InlineData("invalid-utf8")]
    [InlineData("scope-count")]
    [InlineData("scope-label")]
    [InlineData("entry-count")]
    [InlineData("empty")]
    [InlineData("duplicate-id")]
    [InlineData("payload-size")]
    [InlineData("aggregate")]
    [InlineData("provenance")]
    public async Task InnerBundleIsValidatedEvenWithARecomputedEnvelopeChecksum(string failure)
    {
        var task = new IntTask(); var codec = new IntCodec();
        var source = await EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, Scope(task, codec), Provenance());
        Assert.ThrowsAny<Exception>(() => Mutate(source, payload =>
        {
            var entries = payload["Entries"]!.AsArray(); var entry = entries[0]!.AsObject();
            if (failure == "payload-hash") entry["PayloadSha256"] = new string('0', 64);
            if (failure == "base64") entry["PayloadBase64"] = "!";
            if (failure == "invalid-utf8") entry["PayloadBase64"] = Convert.ToBase64String(new byte[] { 0xff });
            if (failure == "scope-count") payload["Scope"]!.AsArray().RemoveAt(0);
            if (failure == "scope-label") payload["Scope"]!.AsArray()[0] = "bad\nlabel";
            if (failure == "empty") entries.Clear();
            if (failure == "duplicate-id") entries.Add(entry.DeepClone());
            if (failure == "entry-count") for (int i = 0; i < 256; i++) { var next = entry.DeepClone().AsObject(); next["SourceGenomeId"] = "seed-" + i; entries.Add(next); }
            if (failure == "payload-size") entry["PayloadBase64"] = new string('A', 100000);
            if (failure == "aggregate")
            {
                string text = new string('x', 65536);
                entry["PayloadBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)); entry["PayloadSha256"] = EvolutionHash.Compute(text);
                for (int i = 0; i < 33; i++) { var next = entry.DeepClone().AsObject(); next["SourceGenomeId"] = "seed-" + i; entries.Add(next); }
            }
            if (failure == "provenance") payload["Provenance"]!["PriorCostUnits"] = -1;
        }));
    }

    [Fact]
    public void JsonByteLimitAndNullFailBeforeParsing()
    {
        Assert.Throws<ArgumentNullException>(() => EvolutionRepertoire.FromJson(null!));
        Assert.Throws<InvalidDataException>(() => EvolutionRepertoire.FromJson(new string(' ', EvolutionRepertoire.MaximumJsonBytes + 1)));
        Assert.Throws<InvalidDataException>(() => EvolutionRepertoire.FromJson(new string('\u20ac', EvolutionRepertoire.MaximumJsonBytes / 2)));
    }

    [Fact]
    public void EveryApplicabilityFacetParticipatesInIdentity()
    {
        var task = new IntTask(); var codec = new IntCodec(); var scope = Scope(task, codec);
        string[] parts = { scope.TaskId, scope.TaskVersion, scope.EvaluatorVersion, scope.CodecId, scope.CodecVersion,
            scope.ConstraintsVersion, scope.DataVersion, scope.FidelityVersion, scope.CompilerVersion, scope.RuntimeVersion,
            scope.HardwareVersion, scope.CorrectnessPolicyVersion };
        for (int i = 0; i < parts.Length; i++)
        {
            var modified = (string[])parts.Clone(); modified[i] += "changed";
            Assert.NotEqual(scope.StableKey, new EvolutionReuseScope(modified).StableKey);
        }
        Assert.Equal(scope.StableKey, new EvolutionReuseScope(parts).StableKey);
        parts[0] = "mutated";
        Assert.Equal("integer-task", scope.TaskId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nlabel")]
    public void ScopeRejectsInvalidLabels(string value)
    {
        var task = new IntTask(); var codec = new IntCodec();
        Assert.Throws<ArgumentException>(() => Scope(task, codec, data: value));
    }

    [Fact]
    public void ScopeRejectsUnpairedSurrogateConstructedAtRuntime()
    {
        // Attribute blobs encode strings as UTF8 and cannot preserve this invalid UTF16 value.
        string value = "bad" + new string((char)0xd800, 1);
        Assert.Throws<ArgumentException>(() => Scope(new IntTask(), new IntCodec(), data: value));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("evidence")]
    [InlineData("time")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("negative")]
    [InlineData("unit")]
    [InlineData("long-label")]
    public void ProvenanceRejectsUnusableDeclarations(string invalid)
    {
        var valid = Provenance();
        Assert.ThrowsAny<ArgumentException>(() => new EvolutionRepertoireProvenance(
            invalid == "long-label" ? new string('x', 257) : valid.SourceRunId,
            invalid == "state" ? "bad" : valid.SourceStateHash,
            invalid == "evidence" ? new string('Z', 64) : valid.EvidenceSha256,
            invalid == "time" ? default : valid.CreatedAt,
            invalid == "nan" ? double.NaN : invalid == "infinity" ? double.PositiveInfinity : invalid == "negative" ? -1 : 1,
            invalid == "unit" ? " " : valid.CostUnit));
    }

    [Fact]
    public async Task NullArgumentsAndPreCanceledCallsFailBeforeCodecWork()
    {
        var task = new IntTask(); var codec = new IntCodec(); var scope = Scope(task, codec);
        var repertoire = await EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, scope, Provenance());
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvolutionRepertoire.ExportAsync<int>(null!, task, codec, scope, Provenance()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, null!, Provenance()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, scope, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => repertoire.ImportAsync<int>(null!, codec, scope));
        await Assert.ThrowsAsync<ArgumentNullException>(() => repertoire.ImportAsync(task, null!, scope));
        await Assert.ThrowsAsync<ArgumentNullException>(() => repertoire.ImportAsync(task, codec, null!));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        codec.Decode = _ => throw new InvalidOperationException("Must not be called.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repertoire.ImportAsync(task, codec, scope, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvolutionRepertoire.ExportAsync(new[] { 1 }, task, codec, scope, Provenance(), cancellation.Token));
    }

    private static EvolutionRepertoire Mutate(EvolutionRepertoire source, Action<JsonObject> action)
    {
        var envelope = JsonNode.Parse(source.ToJson())!.AsObject();
        var payload = JsonNode.Parse(envelope["Payload"]!.GetValue<string>())!.AsObject();
        action(payload);
        string text = payload.ToJsonString(); envelope["Payload"] = text; envelope["Checksum"] = EvolutionHash.Compute(text);
        return EvolutionRepertoire.FromJson(envelope.ToJsonString());
    }

    private static EvolutionReuseScope Scope(IntTask task, IntCodec codec, string constraints = "nonnegative-v1", string data = "data-v1") =>
        new(task.Id, task.VersionHash, task.EvaluatorVersionHash, codec.Id, codec.VersionHash, constraints, data,
            "full-v1", "not-applicable-v1", "runtime-v1", "hardware-v1", "oracle-v1");

    private static EvolutionRepertoireProvenance Provenance() => new("source-run", EvolutionHash.Compute("source-state"),
        EvolutionHash.Compute("raw-evidence"), new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.FromHours(-4)), 125.5, "worker-calls-v1");

    private static EvolutionEngine<int> Engine(IntTask task) => new(task, new IntVariation(),
        _ => new MapElitesArchive<int>(new[] { new EvolutionDescriptorDefinition("value", 0, 100, 100) }),
        new EvolutionEngineOptions { RunId = "import-run", MaxGenerations = 0, MaxProposals = 10, MaxEvaluationAttempts = 10 });

    private sealed class IntTask : IEvolutionTask<int>
    {
        internal string TaskId { get; set; } = "integer-task";
        internal string Version { get; set; } = "task-v1";
        internal string EvaluatorVersion { get; set; } = "evaluator-v1";
        internal int Maximum { get; set; } = int.MaxValue;
        internal bool ReverseQuality { get; set; }
        internal bool Modulo { get; set; }
        internal bool FixedIdentity { get; set; }
        internal Action? OnCanonicalize { get; set; }
        internal int Evaluations { get; private set; }
        public string Id => TaskId;
        public string VersionHash => Version;
        public string EvaluatorVersionHash => EvaluatorVersion;
        public ValueTask<EvolutionCanonicalGenome<int>> CanonicalizeAsync(int genome, CancellationToken cancellationToken = default)
        {
            OnCanonicalize?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            if (genome < 0 || genome > Maximum) throw new ArgumentException("Outside current constraints.", nameof(genome));
            int canonical = Modulo ? genome % 2 : genome;
            return new(new EvolutionCanonicalGenome<int>(canonical, FixedIdentity ? "fixed" : canonical.ToString(CultureInfo.InvariantCulture)));
        }
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<int> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            Evaluations++;
            int value = candidate.CanonicalGenome.Genome;
            return new(EvolutionTaskResult.Completed(ReverseQuality ? 100 - value : value, new Dictionary<string, double> { ["value"] = value }));
        }
    }

    private sealed class IntCodec : IEvolutionGenomeCodec<int>
    {
        internal string CodecId { get; set; } = "integer-codec";
        internal string Version { get; set; } = "schema-v1";
        internal Func<int, string> Encode { get; set; } = n => n.ToString(CultureInfo.InvariantCulture);
        internal Func<string, int> Decode { get; set; } = text => int.Parse(text, CultureInfo.InvariantCulture);
        public string Id => CodecId;
        public string VersionHash => Version;
        public string Serialize(int genome) => Encode(genome);
        public int Deserialize(string payload) => Decode(payload);
    }

    private sealed class IntVariation : IVariationOperator<int>
    {
        public string Id => "integer-variation";
        public string VersionHash => "v1";
        public ValueTask<int> ProposeAsync(EvolutionVariationContext<int> context, CancellationToken cancellationToken = default) => new(0);
    }
}
