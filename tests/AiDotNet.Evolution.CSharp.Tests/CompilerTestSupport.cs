// Migrated from ooples/AiDotNet 66d7602c92101e5ab2bd9db8cfa7f7526fa2c75d:tests/AiDotNet.Evolution.CSharp.Tests/CompilerTestSupport.cs
// Original license retained in AIDOTNET-LICENSE.txt.
using System.Text.Json;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.CSharp.Tests;

internal static class CompilerTestSupport
{
    internal const string Source = "public static class C { public static int F() { return 1; } }";
    internal static ProgramGenome Parent(string source = Source) => new(source, ProgramLanguage.CSharp);
    internal static CSharpProgramSourceOptions Program(bool enforce = false) => new()
    {
        Language = ProgramLanguage.CSharp,
        EnforceEvolveBlocks = enforce
    };
    internal static CSharpProgramEvolutionOptions Options(string? directory = null) => new()
    {
        ReferencePaths = new[] { typeof(object).Assembly.Location },
        TargetIdentity = "test-runtime:" + Environment.Version,
        ModelVersionIdentity = "scripted-test-v1",
        AuditDirectory = directory ?? Path.Combine(Path.GetTempPath(), "aidotnet-csharp-tests", Guid.NewGuid().ToString("N")),
        MaxRepairs = 1
    };
    internal static EvolutionResourceLedger Ledger(decimal cost = 100) => new(Guid.NewGuid().ToString("N"),
        new EvolutionResources(new Dictionary<string, decimal>
        {
            ["cost_units"] = cost,
            ["model_calls"] = 100,
            ["parse_calls"] = 100,
            ["build_calls"] = 100,
            ["audit_calls"] = 100,
            ["input_tokens"] = 1_000_000,
            ["output_tokens"] = 1_000_000,
            ["artifact_bytes"] = 100_000_000,
            ["reference_bytes"] = CSharpPatchCompiler.MaximumReferenceBytes
        }));
    internal static EvolutionVariationContext<ProgramGenome> Context(ProgramGenome? parent = null, long generation = 1)
    {
        parent ??= Parent();
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL);
        var candidate = new EvolutionCandidate<ProgramGenome>(0, new EvolutionCanonicalGenome<ProgramGenome>(parent, parent.Id), lineage);
        var evaluation = new EvolutionEvaluation(0, parent.Id, EvolutionEvaluationStatus.Completed, 0.5,
            EvolutionOptimizationDirection.Maximize, new Dictionary<string, double> { ["x"] = 0.5 },
            Array.Empty<double>(), Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage,
            EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task-v1", "evaluator-v1", "config-v1");
        var entry = new EvolutionArchiveEntry<ProgramGenome>(new EvolutionCellKey(new[] { 0 }), candidate, evaluation);
        return new(entry, Array.Empty<EvolutionArchiveEntry<ProgramGenome>>(), new StableRandom(1234UL, 7UL), generation, 0);
    }
    internal static object Edit(CSharpEditTarget node, string replacement) => new
    {
        start = node.Start,
        length = node.Length,
        kind = node.Kind,
        expectedSha256 = node.ExpectedSha256,
        replacement
    };
    internal static string Patch(CSharpPatchPreparation prepared, string replacement = "2", string kind = "NumericLiteralExpression") =>
        Patch(prepared.Parent.Id, Edit(prepared.Targets.First(item => item.Kind == kind), replacement));
    internal static string Patch(string parentId, params object[] edits) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        parentId,
        hypothesis = "Reduce the measured operation count while preserving required outputs.",
        edits
    });
    internal static string Reply(IReadOnlyList<CompilerChatMessage> messages, string replacement)
    {
        using JsonDocument data = JsonDocument.Parse(messages[1].Text);
        JsonElement root = data.RootElement;
        JsonElement target = root.GetProperty("catalog").EnumerateArray().First(item => item.GetProperty("kind").GetString() == "NumericLiteralExpression");
        return Patch(root.GetProperty("parentId").GetString()!, new
        {
            start = target.GetProperty("start").GetInt32(),
            length = target.GetProperty("length").GetInt32(),
            kind = target.GetProperty("kind").GetString(),
            expectedSha256 = target.GetProperty("expectedSha256").GetString(),
            replacement
        });
    }
}

internal sealed class ScriptedClient : ICSharpProposalClient
{
    internal Func<int, IReadOnlyList<CompilerChatMessage>, CompilerChatResponse> Handler { get; set; } =
        (_, messages) => Response(CompilerTestSupport.Reply(messages, "2"));
    public string ModelId { get; set; } = "scripted-test";
    internal List<IReadOnlyList<CompilerChatMessage>> Conversations { get; } = new();
    internal CompilerChatOptions? LastOptions { get; private set; }
    internal static CompilerChatResponse Response(string text, CompilerChatUsage? usage = null) =>
        new(CompilerChatMessage.Assistant(text), usage: usage ?? new CompilerChatUsage(100, 50));
    public Task<CompilerChatResponse> GetResponseAsync(IReadOnlyList<CompilerChatMessage> messages, CompilerChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Conversations.Add(messages.ToArray());
        LastOptions = options;
        return Task.FromResult(Handler(Conversations.Count, messages));
    }
}
