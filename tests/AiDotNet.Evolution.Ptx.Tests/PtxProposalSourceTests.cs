using System.Text.Json;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.Ptx.Tests;

public sealed class PtxProposalSourceTests
{
    private static EvolutionResourceLedger Ledger(decimal cost = 1000) => new(Guid.NewGuid().ToString("N"),
        new EvolutionResources(new Dictionary<string, decimal>
        {
            ["cost_units"] = cost,
            ["model_calls"] = 100,
            ["parse_calls"] = 100,
            ["build_calls"] = 100,
            ["audit_calls"] = 100,
            ["input_tokens"] = 10_000_000,
            ["output_tokens"] = 10_000_000,
            ["artifact_bytes"] = 1_000_000_000
        }));

    private static PtxProgramEvolutionOptions Options() => new()
    {
        ModelVersionIdentity = "scripted-ptx-v1",
        AuditDirectory = Path.Combine(Path.GetTempPath(), "aidotnet-ptx-tests", Guid.NewGuid().ToString("N")),
        MaxRepairs = 1,
        IncumbentProfile = new PtxIncumbentProfile(0.198, 0.002, 12, 0, 0, "DRAM-bound: 88% of peak bandwidth")
    };

    private static EvolutionVariationContext<ProgramGenome> Context(ProgramGenome parent)
    {
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL);
        var candidate = new EvolutionCandidate<ProgramGenome>(0, new EvolutionCanonicalGenome<ProgramGenome>(parent, parent.Id), lineage);
        var evaluation = new EvolutionEvaluation(0, parent.Id, EvolutionEvaluationStatus.Completed, 1.0,
            EvolutionOptimizationDirection.Maximize, new Dictionary<string, double> { ["speedup"] = 1.0, ["registers"] = 12 },
            Array.Empty<double>(), Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage,
            EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task-v1", "evaluator-v1", "config-v1");
        var entry = new EvolutionArchiveEntry<ProgramGenome>(new EvolutionCellKey(new[] { 0 }), candidate, evaluation);
        return new(entry, Array.Empty<EvolutionArchiveEntry<ProgramGenome>>(), new StableRandom(1234UL, 7UL), 1, 0);
    }

    private static (PtxProposalSource Source, ScriptedChatClient Client, FakeWorkerTransport Worker, EvolutionResourceLedger Ledger, PtxProgramEvolutionOptions Options) Setup()
    {
        var worker = new FakeWorkerTransport();
        var client = new ScriptedChatClient();
        var ledger = Ledger();
        PtxProgramEvolutionOptions options = Options();
        return (PtxProposalSource.Create(client, new PtxProgramCompiler(Axpy.Contract(), worker), options, ledger), client, worker, ledger, options);
    }

    private static ResourceMeteredVariationOperator<ProgramGenome> Meter(PtxProposalSource source, EvolutionResourceLedger ledger) =>
        new(source, ledger, source.MaximumProposalResources, source.CostUnitVersionHash);

    [Fact]
    public async Task A_rewrite_with_a_launch_change_is_jit_checked_and_returned_with_the_contract_in_the_prompt()
    {
        var (source, client, worker, ledger, options) = Setup();
        var parent = new ProgramGenome(Axpy.Source);
        client.Reply = (_, messages) =>
        {
            using JsonDocument data = JsonDocument.Parse(messages[1].Text);
            return ScriptedChatClient.Rewrite(data.RootElement.GetProperty("parentId").GetString() ?? string.Empty, Axpy.Source,
                new { blockX = 128, blockY = 1, blockZ = 1 });
        };
        ProgramGenome proposed = await Meter(source, ledger).ProposeAsync(Context(parent));
        Assert.Equal(Axpy.Block128, proposed.Source);
        Assert.Equal(1, source.GetUsage().ChatCalls);
        IReadOnlyList<ProgramChatMessage> prompt = Assert.Single(client.Conversations);
        Assert.Equal(PtxProposalSource.Instructions, prompt[0].Text);
        using (JsonDocument data = JsonDocument.Parse(prompt[1].Text))
        {
            JsonElement root = data.RootElement;
            JsonElement contract = root.GetProperty("contract");
            Assert.Equal("axpy", contract.GetProperty("entryPoint").GetString());
            Assert.Equal(5, contract.GetProperty("signature").GetArrayLength());
            Assert.Equal("ceil(N/blockX)", contract.GetProperty("launch").GetProperty("grid")[0].GetString());
            Assert.Equal(65536, contract.GetProperty("timingShape").GetProperty("N").GetInt64());
            Assert.Equal("sm_75", contract.GetProperty("target").GetProperty("sm").GetString());
            Assert.Equal(0, contract.GetProperty("defaultTolerance").GetProperty("Absolute").GetDouble());
            Assert.Contains("DRAM-bound", root.GetProperty("incumbentProfile").GetProperty("Bottleneck").GetString(), StringComparison.Ordinal);
            Assert.Equal(12, root.GetProperty("measuredParent").GetProperty("descriptors").GetProperty("registers").GetDouble());
            Assert.True(root.GetProperty("catalog").GetArrayLength() > 10);
        }
        Assert.Equal(ProgramChatResponseFormat.Json, client.LastOptions?.ResponseFormat);
        Assert.Single(worker.Requests, r => r.Operation == PtxWorkerOperation.Compile);
        Assert.Single(Directory.GetFiles(options.AuditDirectory, "*.json"));
        Assert.Equal(1, ledger.Snapshot().Spent["model_calls"]);
        Assert.Equal(1, ledger.Snapshot().Spent["build_calls"]);
        Directory.Delete(options.AuditDirectory, recursive: true);
    }

    [Fact]
    public async Task A_line_patch_is_applied_to_the_catalog_and_a_bad_reply_gets_repair_feedback()
    {
        var (source, client, _, ledger, options) = Setup();
        var parent = new ProgramGenome(Axpy.Source);
        client.Reply = (call, messages) =>
        {
            if (call == 1) return "not json";
            using JsonDocument data = JsonDocument.Parse(messages[1].Text);
            JsonElement root = data.RootElement;
            int line = Axpy.Source.Split('\n').ToList().IndexOf(Axpy.Fma) + 1;
            JsonElement target = root.GetProperty("catalog").EnumerateArray().Single(t => t.GetProperty("line").GetInt32() == line);
            return JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                parentId = root.GetProperty("parentId").GetString(),
                hypothesis = "Separate multiply and add.",
                edits = new[] { new { line, expectedSha256 = target.GetProperty("expectedSha256").GetString(), replacement = "    mul.rn.f32 %f4, %f1, %f2;\n    add.rn.f32 %f4, %f4, %f3;" } }
            });
        };
        ProgramGenome proposed = await Meter(source, ledger).ProposeAsync(Context(parent));
        Assert.Contains("add.rn.f32 %f4, %f4, %f3;", proposed.Source, StringComparison.Ordinal);
        Assert.DoesNotContain(Axpy.Fma, proposed.Source, StringComparison.Ordinal);
        Assert.Equal(2, client.Conversations.Count);
        IReadOnlyList<ProgramChatMessage> repair = client.Conversations[1];
        Assert.Equal(4, repair.Count);
        Assert.Contains("not a valid bounded rewrite or patch", repair[3].Text, StringComparison.Ordinal);
        Assert.Equal(1, source.GetUsage().Retries);
        Directory.Delete(options.AuditDirectory, recursive: true);
    }

    [Fact]
    public async Task Jit_errors_are_fed_back_and_an_unrepaired_proposal_is_rejected_with_its_work_charged()
    {
        var (source, client, worker, ledger, options) = Setup();
        worker.Compile = _ => new PtxWorkerCompiled { Loaded = false, ErrorLog = "ptxas application ptx input, line 38; error   : Unknown symbol '%f9'" };
        var parent = new ProgramGenome(Axpy.Source);
        client.Reply = (_, messages) =>
        {
            using JsonDocument data = JsonDocument.Parse(messages[1].Text);
            return ScriptedChatClient.Rewrite(data.RootElement.GetProperty("parentId").GetString() ?? string.Empty, Axpy.Wrong);
        };
        EvolutionResourceResult<ProgramGenome> result = await source.ProposeAsync(Context(parent));
        Assert.Equal(EvolutionResourceOutcome.Rejected, result.Outcome);
        // A rejected proposal returns the parent itself (an owned snapshot of it), not a variant.
        Assert.NotNull(result.Value);
        Assert.Equal(parent.Id, result.Value.Id);
        Assert.Equal(parent.Source, result.Value.Source);
        Assert.Contains("PTXAS-ERROR at line 38", client.Conversations[1][3].Text, StringComparison.Ordinal);
        Assert.Equal(2m, result.Actual["model_calls"]);
        Assert.Equal(2m, result.Actual["build_calls"]);
        Assert.Equal(1, source.GetUsage().AbandonedProposals);
        Directory.Delete(options.AuditDirectory, recursive: true);
    }

    [Fact]
    public async Task A_missing_gpu_abandons_the_proposal_instead_of_spending_repairs()
    {
        var (source, client, worker, _, options) = Setup();
        worker.Status = "cuda-unavailable";
        var parent = new ProgramGenome(Axpy.Source);
        client.Reply = (_, messages) =>
        {
            using JsonDocument data = JsonDocument.Parse(messages[1].Text);
            return ScriptedChatClient.Rewrite(data.RootElement.GetProperty("parentId").GetString() ?? string.Empty, Axpy.Wrong);
        };
        EvolutionResourceResult<ProgramGenome> result = await source.ProposeAsync(Context(parent));
        Assert.Equal(EvolutionResourceOutcome.Failed, result.Outcome);
        Assert.Single(client.Conversations);
        Directory.Delete(options.AuditDirectory, recursive: true);
    }

    [Fact]
    public void Usage_state_round_trips_and_refuses_inconsistent_input()
    {
        var (source, _, _, _, options) = Setup();
        string state = source.CaptureState();
        source.RestoreState(state);
        Assert.Equal(state, source.CaptureState());
        Assert.Throws<ArgumentException>(() => source.RestoreState(state.Replace("\"Errors\":0", "\"Errors\":1", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => source.RestoreState("{}"));
        Directory.Delete(options.AuditDirectory, recursive: true);
    }

    [Fact]
    public void The_public_factory_requires_matching_cost_identities()
    {
        var worker = new FakeWorkerTransport();
        var compiler = new PtxProgramCompiler(Axpy.Contract(), worker);
        PtxProgramEvolutionOptions options = Options();
        var ledger = Ledger();
        Assert.Throws<ArgumentException>(() => PtxProgramVariation.Create(new ScriptedChatClient(), compiler, options,
            new ProgramEvolutionResourceOptions(ledger, 10, "other-units")));
        MeteredProgramVariationOperator variation = PtxProgramVariation.Create(new ScriptedChatClient(), compiler, options,
            new ProgramEvolutionResourceOptions(ledger, 10, options.CostUnitVersionHash));
        // The metering wrapper prefixes the operator id (ResourceMeteredVariationOperator), so traces name the metered operator.
        Assert.Equal("resource-metered:" + options.Id, variation.Id);
        Assert.Equal(0.1m, ledger.Snapshot().Spent["cost_units"]);
        options.AllowPatches = false;
        options.AllowRewrites = false;
        Assert.Throws<ArgumentException>(() => PtxProgramVariation.Create(new ScriptedChatClient(), compiler, options,
            new ProgramEvolutionResourceOptions(Ledger(), 10, options.CostUnitVersionHash)));
        Directory.Delete(options.AuditDirectory, recursive: true);
    }
}