// US-17 (#35): what the compiler-guided repair loop recovers, and what it costs in model calls.
//
// The story's metrics table was "measured by paired pilot" and empty, because a paired pilot needs a
// paid campaign. This measures the *mechanism* instead, with a scripted client and no spend: the same
// proposals are run with MaxRepairs = 0 and MaxRepairs = 2, so the only difference between the arms is
// whether a proposal that fails to compile gets the compiler's own diagnostics back and another try.
//
// What this is NOT: a measurement of how often a real model proposes a broken patch. That rate is an
// input here (BreakRate), chosen so both outcomes occur on every seed; an LLM's rate is unknown until
// the gate campaign runs. What the numbers below support is the recovery mechanism and its call cost.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiDotNet.Evolution;
using AiDotNet.Evolution.CSharp;
using AiDotNet.Evolution.Programs;

internal static class Program
{
    internal const string Protocol = "repair-ablation-v1";
    // The parent is deliberately tiny: this measures the repair loop, not a search.
    private const string Source = "public static class C { public static int F() { return 1; } }";
    private const int ProposalsPerSeed = 16;
    // Half of the first replies are broken, so every seed exercises both outcomes under both arms.
    private const double BreakRate = 0.5;
    private static readonly ulong[] Seeds = { 1103, 2207, 3301, 4409, 5519, 6607, 7717, 8821 };
    private static readonly int[] RepairBudgets = { 0, 2 };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--output"))
            throw new ArgumentException("Usage: EvolutionRepair [--output new-rows.json]");
        using var stream = args.Length == 2 ? new FileStream(args[1], FileMode.CreateNew, FileAccess.Write) : null;

        var rows = new List<Row>();
        foreach (int budget in RepairBudgets)
            foreach (ulong seed in Seeds)
                rows.Add(await MeasureAsync(budget, seed).ConfigureAwait(false));

        foreach (Row row in rows)
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "repairs={0} seed={1,-5} proposals={2} valid={3,-3} calls={4,-3} repairs_used={5,-3} valid_rate={6:F4} calls_per_valid={7}",
                row.MaxRepairs, row.Seed, row.Proposals, row.Valid, row.ModelCalls, row.RepairsUsed, row.ValidRate,
                row.CallsPerValid is null ? "none" : row.CallsPerValid.Value.ToString("F4", CultureInfo.InvariantCulture)));

        var document = new Report(Protocol, Source.Length, ProposalsPerSeed, BreakRate, Seeds, RepairBudgets, rows.ToArray());
        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (stream is not null)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
            Console.WriteLine("wrote " + args[1]);
        }
        return 0;
    }

    internal sealed record Row(int MaxRepairs, ulong Seed, int Proposals, int Valid, int Invalid, long ModelCalls,
        long RepairsUsed, double ValidRate, double? CallsPerValid, long InputTokens, long OutputTokens);

    internal sealed record Report(string Protocol, int ParentChars, int ProposalsPerSeed, double BreakRate,
        ulong[] Seeds, int[] RepairBudgets, Row[] Rows);

    private static async Task<Row> MeasureAsync(int maxRepairs, ulong seed)
    {
        string audit = Path.Combine(Path.GetTempPath(), "evolution-repair-ablation", maxRepairs.ToString(CultureInfo.InvariantCulture),
            seed.ToString(CultureInfo.InvariantCulture));
        if (Directory.Exists(audit)) Directory.Delete(audit, recursive: true);

        int valid = 0, invalid = 0;
        long calls = 0, repairsUsed = 0, inputTokens = 0, outputTokens = 0;
        for (int proposal = 0; proposal < ProposalsPerSeed; proposal++)
        {
            // The same decision for both arms: whether this proposal's first reply is broken is a
            // function of (seed, proposal) only, so the arms see an identical sequence of model replies.
            bool breakFirst = Decide(seed, proposal);
            var client = new ScriptedClient(breakFirst);
            var options = new CSharpProgramEvolutionOptions
            {
                ReferencePaths = new[] { typeof(object).Assembly.Location },
                TargetIdentity = "repair-ablation:" + Environment.Version,
                ModelVersionIdentity = "scripted-repair-v1",
                AuditDirectory = Path.Combine(audit, proposal.ToString(CultureInfo.InvariantCulture)),
                MaxRepairs = maxRepairs
            };
            var ledger = Ledger();
            // The public factory, so this harness measures the surface a consumer actually gets.
            var resources = new ProgramEvolutionResourceOptions(ledger, maximumEvaluationCostUnits: 100m,
                costUnitVersionHash: options.CostUnitVersionHash);
            MeteredProgramVariationOperator variation = CSharpProgramVariation.Create(
                client, new CSharpProgramSourceOptions { Language = ProgramLanguage.CSharp }, options, resources);
            try
            {
                ProgramGenome child = await variation.ProposeAsync(Context(seed, proposal)).ConfigureAwait(false);
                // A recovered proposal must be the repaired program, not the parent returned unchanged.
                if (child.Source == Source) throw new InvalidOperationException("The proposal source returned the parent unchanged.");
                valid++;
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or ArgumentException)
            {
                invalid++;
            }
            // The client is the only honest source of call counts here: it is the thing being called.
            calls += client.Calls;
            repairsUsed += Math.Max(0, client.Calls - 1);
            inputTokens += client.InputTokens;
            outputTokens += client.OutputTokens;
        }

        return new Row(maxRepairs, seed, ProposalsPerSeed, valid, invalid, calls, repairsUsed,
            valid / (double)ProposalsPerSeed, valid == 0 ? null : calls / (double)valid, inputTokens, outputTokens);
    }

    /// <summary>Deterministic, and identical across arms: the hash of (seed, proposal) decides.</summary>
    private static bool Decide(ulong seed, int proposal)
    {
        byte[] key = Encoding.UTF8.GetBytes(string.Format(CultureInfo.InvariantCulture, "repair-ablation-v1:{0}:{1}", seed, proposal));
        byte[] digest = SHA256.HashData(key);
        ulong draw = BitConverter.ToUInt64(digest, 0);
        return draw / (double)ulong.MaxValue < BreakRate;
    }

    private static EvolutionResourceLedger Ledger() => new(Guid.NewGuid().ToString("N"),
        new EvolutionResources(new Dictionary<string, decimal>
        {
            ["cost_units"] = 1000,
            ["model_calls"] = 100,
            ["parse_calls"] = 100,
            ["build_calls"] = 100,
            ["audit_calls"] = 100,
            ["input_tokens"] = 1_000_000,
            ["output_tokens"] = 1_000_000,
            ["artifact_bytes"] = 100_000_000,
            // The compiler's own ceiling is internal; this is the same 128 MiB in the units the ledger takes.
            ["reference_bytes"] = 128L * 1024 * 1024
        }));

    private static EvolutionVariationContext<ProgramGenome> Context(ulong seed, int proposal)
    {
        var parent = new ProgramGenome(Source, ProgramLanguage.CSharp);
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL);
        var candidate = new EvolutionCandidate<ProgramGenome>(0, new EvolutionCanonicalGenome<ProgramGenome>(parent, parent.Id), lineage);
        var evaluation = new EvolutionEvaluation(0, parent.Id, EvolutionEvaluationStatus.Completed, 0.5,
            EvolutionOptimizationDirection.Maximize, new Dictionary<string, double> { ["x"] = 0.5 },
            Array.Empty<double>(), Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage,
            EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task-v1", "evaluator-v1", "repair-ablation-v1");
        var entry = new EvolutionArchiveEntry<ProgramGenome>(new EvolutionCellKey(new[] { 0 }), candidate, evaluation);
        return new(entry, Array.Empty<EvolutionArchiveEntry<ProgramGenome>>(),
            new StableRandom(seed, (ulong)proposal), proposal + 1, 0);
    }

    /// <summary>A model that proposes one broken patch when told to, then the patch that compiles.</summary>
    /// <remarks>
    /// The broken edit replaces the literal with text that is not an expression, so the compiler - not a
    /// script - decides it is invalid. The repair prompt is the second call, and the reply to it always
    /// compiles, which is what makes the two arms differ by exactly one recovered proposal per break.
    /// </remarks>
    private sealed class ScriptedClient(bool breakFirst) : ICSharpProposalClient
    {
        public string ModelId => "scripted-repair";

        public long Calls { get; private set; }

        public long InputTokens { get; private set; }

        public long OutputTokens { get; private set; }

        public Task<CompilerChatResponse> GetResponseAsync(IReadOnlyList<CompilerChatMessage> messages,
            CompilerChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            InputTokens += 100;
            OutputTokens += 50;
            string replacement = breakFirst && Calls == 1 ? "MISSING" : "2";
            return Task.FromResult(new CompilerChatResponse(CompilerChatMessage.Assistant(Reply(messages, replacement)),
                modelId: ModelId, usage: new CompilerChatUsage(100, 50)));
        }

        /// <summary>Builds an edit against the catalog the proposal source supplied, as a real reply must.</summary>
        private static string Reply(IReadOnlyList<CompilerChatMessage> messages, string replacement)
        {
            using JsonDocument data = JsonDocument.Parse(messages[1].Text);
            JsonElement root = data.RootElement;
            JsonElement target = root.GetProperty("catalog").EnumerateArray()
                .First(item => item.GetProperty("kind").GetString() == "NumericLiteralExpression");
            string parentId = root.GetProperty("parentId").GetString()
                ?? throw new InvalidDataException("The proposal prompt carried no parent identity.");
            return JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                parentId,
                hypothesis = "Reduce the measured operation count while preserving required outputs.",
                edits = new[]
                {
                    new
                    {
                        start = target.GetProperty("start").GetInt32(),
                        length = target.GetProperty("length").GetInt32(),
                        kind = target.GetProperty("kind").GetString(),
                        expectedSha256 = target.GetProperty("expectedSha256").GetString(),
                        replacement
                    }
                }
            });
        }
    }
}
