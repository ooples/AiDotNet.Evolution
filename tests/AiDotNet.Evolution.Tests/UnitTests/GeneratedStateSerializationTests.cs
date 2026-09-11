using System.Text.Json;
using AiDotNet.Evolution;
using Xunit;
using static AiDotNet.Evolution.EvolutionEngineDocuments;

namespace AiDotNet.Evolution.Tests;

public sealed class GeneratedStateSerializationTests
{
    [Fact]
    public void GeneratedCheckpointDocumentsPreserveLegacyBytesIncludingNestedFieldsAndNullPolicy()
    {
        var entry = new ArchiveEntryDocument
        {
            CellBins = new[] { 0 },
            EvaluationId = long.MaxValue,
            GenomeId = "id/é",
            GenomePayload = "7",
            Lineage = new LineageDocument { ParentIds = new() { "parent" }, SeedStream = ulong.MaxValue },
            Evaluation = new EvaluationDocument { Quality = 7, Descriptors = new() { ["x"] = 0.25 }, MeasurementOriginJson = "origin" }
        };
        var state = new EngineStateDocument
        {
            SchemaVersion = 7,
            SemanticOptions = new() { new() { Name = "mode", Value = "batch" } },
            BudgetOptions = new(),
            SeedPayloads = new() { "7" },
            NextEvaluationId = long.MaxValue,
            EarlyStoppingArchiveMetric = 0.1,
            GlobalElites = new() { new() { Island = 0, Entry = entry } },
            IslandHistories = new() { new() { entry } },
            StatusCounts = new() { new() { Status = EvolutionEvaluationStatus.Completed, Count = 1 } },
            Cache = new() { new() { GenomeId = "cached", Result = new() { Quality = 5 } } },
            Failures = new() { new() { Code = "test", Message = "é", Data = new() { ["x"] = "value" } } },
            PendingArtifacts = new() { new() { GenomeId = "id", Artifacts = new() { new() { Key = "raw", Text = "x\ny" } } } },
            Islands = new() { new() { Entries = new() { entry }, Descriptors = new() { new() { Name = "x", Minimum = 0, Maximum = 1, BinCount = 2 } } } }
        };
        // Reflection is used only by this managed compatibility oracle, never the runtime path.
        string before = JsonSerializer.Serialize(state, EvolutionJson.Compact);
        string generated = JsonSerializer.Serialize(state, EvolutionStateJsonContext.Default.EngineStateDocument);
        Assert.Equal(before, generated);
        Assert.Equal(before, JsonSerializer.Serialize(JsonSerializer.Deserialize(generated, EvolutionStateJsonContext.Default.EngineStateDocument)!,
            EvolutionStateJsonContext.Default.EngineStateDocument));
        Assert.DoesNotContain("\"MeasurementOriginJson\":null", generated);
    }

    [Fact]
    public void GeneratedProposalStatePreservesSortedIdentityAndExactDecimalBytes()
    {
        var state = new ResourceMeteredVariationDocuments.State
        {
            VersionHash = "v1",
            Backend = "backend-state",
            Pending = new()
            {
                [long.MaxValue] = new()
                {
                    Charged = new() { ["cost_units"] = 0.1234567890123456789012345678m },
                    Outcome = EvolutionResourceOutcome.Completed,
                    Dispatched = true
                }
            }
        };
        string before = JsonSerializer.Serialize(state, EvolutionJson.Compact);
        string generated = JsonSerializer.Serialize(state, EvolutionStateJsonContext.Default.MeteredVariationState);
        Assert.Equal(before, generated);
        Assert.Equal(before, JsonSerializer.Serialize(JsonSerializer.Deserialize(generated, EvolutionStateJsonContext.Default.MeteredVariationState)!,
            EvolutionStateJsonContext.Default.MeteredVariationState));
        var scores = new Dictionary<string, double> { ["é"] = 0.25, ["parent"] = 100 };
        Assert.Equal(JsonSerializer.Serialize(scores, EvolutionJson.Compact), JsonSerializer.Serialize(scores, EvolutionStateJsonContext.Default.NumericMap));
    }

    [Fact]
    public void GeneratedOriginPreservesPriorAnonymousDocumentBytes()
    {
        var origin = new EvolutionMeasurementOrigin(new string('a', 64), "run", "7", new[] { "observation/é" },
            new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero), 1.25, "cost_units", "stats-v1",
            standardError: 0.5, lowerConfidenceBound: 1, upperConfidenceBound: 2, confidenceLevel: 0.95);
        string prior = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            origin.ScopeKey,
            origin.SourceRunId,
            origin.SourceEvaluationId,
            origin.SampleIds,
            origin.ObservedAt,
            origin.OriginalCostUnits,
            origin.CostUnit,
            origin.StatisticsVersion,
            origin.Kind,
            origin.StandardError,
            origin.LowerConfidenceBound,
            origin.UpperConfidenceBound,
            origin.ConfidenceLevel
        }, EvolutionJson.Compact);
        Assert.Equal(prior, origin.ToJson()); Assert.Equal(prior, EvolutionMeasurementOrigin.FromJson(prior).ToJson());
    }
}
