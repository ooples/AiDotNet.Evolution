using System.Text.Json.Serialization;

namespace AiDotNet.Evolution;

// Persisted data contains codec payloads, not TGenome. Keep the wire schema independent
// of generic engine closures so NativeAOT can generate complete serializer metadata.
internal static class EvolutionEngineDocuments
{
    internal sealed class EngineStateDocument
    {
        public int SchemaVersion { get; set; }
        public List<OptionFieldDocument>? SemanticOptions { get; set; }
        public List<OptionFieldDocument>? BudgetOptions { get; set; }
        public List<string>? SeedPayloads { get; set; }
        public int SeedIndex { get; set; }
        public long NextEvaluationId { get; set; }
        public long Proposals { get; set; }
        public long EvaluationAttempts { get; set; }
        public long CompletedEvaluations { get; set; }
        public long Generation { get; set; }
        public long EventSequence { get; set; }
        public long CompletionSequence { get; set; }
        public int BatchesSinceMigration { get; set; }
        public long LastMigrationGeneration { get; set; }
        public List<long>? IslandGenerations { get; set; }
        public List<EliteRecordDocument>? GlobalElites { get; set; }
        public List<List<ArchiveEntryDocument>>? IslandHistories { get; set; }
        public string? SelectionState { get; set; }

        /// <summary>State of a variation operator that remembers something between proposals.</summary>
        public string? VariationState { get; set; }
        public List<StatusCountDocument>? StatusCounts { get; set; }
        public List<string>? SeenGenomeIds { get; set; }
        public List<CacheDocument>? Cache { get; set; }
        public List<DiagnosticDocument>? Failures { get; set; }
        public double? EarlyStoppingBest { get; set; }
        public double? EarlyStoppingArchiveMetric { get; set; }
        public long EarlyStoppingArchiveValueCount { get; set; }
        public long EvaluationsSinceImprovement { get; set; }
        public long AbandonedEvaluations { get; set; }
        public List<PendingArtifactDocument>? PendingArtifacts { get; set; }
        public List<ArchiveDocument>? Islands { get; set; }
    }

    internal sealed class OptionFieldDocument
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;

        public static OptionFieldDocument From(KeyValuePair<string, string> field) =>
            new() { Name = field.Key, Value = field.Value };
    }

    internal sealed class PendingArtifactDocument
    {
        public string GenomeId { get; set; } = string.Empty;
        public List<ArtifactDocument>? Artifacts { get; set; }
    }

    internal sealed class ArtifactDocument
    {
        public string Key { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public bool IsTruncated { get; set; }
        public bool IsRedacted { get; set; }

        public static ArtifactDocument From(EvolutionArtifact artifact) => new()
        {
            Key = artifact.Key,
            Text = artifact.Text,
            IsTruncated = artifact.IsTruncated,
            IsRedacted = artifact.IsRedacted
        };

        public EvolutionArtifact ToArtifact()
        {
            try
            {
                return new EvolutionArtifact(Key, Text, IsTruncated, IsRedacted);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("A checkpoint artifact is invalid.", exception);
            }
        }
    }

    internal sealed class StatusCountDocument
    {
        public EvolutionEvaluationStatus Status { get; set; }
        public long Count { get; set; }
    }

    internal sealed class CacheDocument
    {
        public string GenomeId { get; set; } = string.Empty;
        public TaskResultDocument? Result { get; set; }
    }

    internal sealed class ArchiveDocument
    {
        public long Version { get; set; }
        public List<ArchiveEntryDocument>? Entries { get; set; }

        /// <summary>The descriptor ranges in force at checkpoint time, which a Grow axis widens during a run.</summary>
        public List<DescriptorDocument>? Descriptors { get; set; }

        public static ArchiveDocument From<TGenome>(IEvolutionArchive<TGenome> archive, Func<TGenome, string> serializeGenome) => new()
        {
            Version = archive.Version,
            Descriptors = archive.Descriptors.Select(DescriptorDocument.From).ToList(),
            Entries = archive.Entries.OrderBy(item => item.Cell.StableKey, StringComparer.Ordinal)
                .Select(entry => ArchiveEntryDocument.From(entry, serializeGenome)).ToList()
        };
    }

    internal sealed class DescriptorDocument
    {
        public string Name { get; set; } = string.Empty;
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public int BinCount { get; set; }
        public EvolutionOutOfRangePolicy OutOfRangePolicy { get; set; }

        public static DescriptorDocument From(EvolutionDescriptorDefinition descriptor) => new()
        {
            Name = descriptor.Name,
            Minimum = descriptor.Minimum,
            Maximum = descriptor.Maximum,
            BinCount = descriptor.BinCount,
            OutOfRangePolicy = descriptor.OutOfRangePolicy
        };

        public EvolutionDescriptorDefinition ToDefinition()
        {
            if (string.IsNullOrWhiteSpace(Name) || BinCount <= 0 ||
                !Enum.IsDefined(typeof(EvolutionOutOfRangePolicy), OutOfRangePolicy))
                throw new InvalidDataException("A checkpoint descriptor definition is invalid.");
            try
            {
                return new EvolutionDescriptorDefinition(Name, Minimum, Maximum, BinCount, OutOfRangePolicy);
            }
            catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
            {
                throw new InvalidDataException("A checkpoint descriptor definition is invalid.", exception);
            }
        }
    }

    internal sealed class EliteRecordDocument
    {
        public int Island { get; set; }
        public ArchiveEntryDocument? Entry { get; set; }
    }

    internal sealed class ArchiveEntryDocument
    {
        public int[]? CellBins { get; set; }
        public long EvaluationId { get; set; }
        public string GenomeId { get; set; } = string.Empty;
        public string? GenomePayload { get; set; }
        public LineageDocument? Lineage { get; set; }
        public EvaluationDocument? Evaluation { get; set; }

        public static ArchiveEntryDocument From<TGenome>(EvolutionArchiveEntry<TGenome> entry, Func<TGenome, string> serializeGenome) => new()
        {
            CellBins = entry.Cell.Bins.ToArray(),
            EvaluationId = entry.Evaluation.EvaluationId,
            GenomeId = entry.Evaluation.GenomeId,
            GenomePayload = serializeGenome(entry.Candidate.CanonicalGenome.Genome),
            Lineage = LineageDocument.From(entry.Evaluation.Lineage),
            Evaluation = EvaluationDocument.From(entry.Evaluation)
        };
    }

    internal sealed class LineageDocument
    {
        public List<string>? ParentIds { get; set; }
        public List<string>? InspirationIds { get; set; }
        public string VariationOperatorId { get; set; } = string.Empty;
        public string? RefinerId { get; set; }
        public long Generation { get; set; }
        public int Island { get; set; }
        public ulong SeedStream { get; set; }
        public int? MigrationSourceIsland { get; set; }

        public static LineageDocument From(EvolutionLineage lineage) => new()
        {
            ParentIds = lineage.ParentIds.ToList(),
            InspirationIds = lineage.InspirationIds.ToList(),
            VariationOperatorId = lineage.VariationOperatorId,
            RefinerId = lineage.RefinerId,
            Generation = lineage.Generation,
            Island = lineage.Island,
            SeedStream = lineage.SeedStream,
            MigrationSourceIsland = lineage.MigrationSourceIsland
        };

        public EvolutionLineage ToLineage()
        {
            try
            {
                return new EvolutionLineage(ParentIds, InspirationIds, VariationOperatorId, RefinerId,
                    Generation, Island, SeedStream, MigrationSourceIsland);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("A checkpoint lineage record is invalid.", exception);
            }
        }
    }

    internal sealed class EvaluationDocument
    {
        public EvolutionEvaluationStatus Status { get; set; }
        public double? Quality { get; set; }
        public EvolutionOptimizationDirection Direction { get; set; }
        public Dictionary<string, double>? Descriptors { get; set; }
        public List<double>? Objectives { get; set; }
        public List<double>? ConstraintViolations { get; set; }
        public long ElapsedTicks { get; set; }
        public int AttemptCount { get; set; }
        public double CostUnits { get; set; }
        public List<double>? StageCostUnits { get; set; }
        public int? RejectedStage { get; set; }
        public EvolutionCacheStatus CacheStatus { get; set; }
        public List<DiagnosticDocument>? Diagnostics { get; set; }
        public Dictionary<string, double>? Metrics { get; set; }
        public List<ArtifactDocument>? Artifacts { get; set; }
        public string TaskVersionHash { get; set; } = string.Empty;
        public string EvaluatorVersionHash { get; set; } = string.Empty;
        public string ConfigurationHash { get; set; } = string.Empty;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MeasurementOriginJson { get; set; }

        public static EvaluationDocument From(EvolutionEvaluation evaluation) => new()
        {
            Status = evaluation.Status,
            Quality = evaluation.Quality,
            Direction = evaluation.Direction,
            Descriptors = evaluation.Descriptors.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            Objectives = evaluation.Objectives.ToList(),
            ConstraintViolations = evaluation.ConstraintViolations.ToList(),
            ElapsedTicks = evaluation.Cost.Elapsed.Ticks,
            AttemptCount = evaluation.Cost.AttemptCount,
            CostUnits = evaluation.Cost.CostUnits,
            StageCostUnits = evaluation.Cost.StageCostUnits.ToList(),
            RejectedStage = evaluation.Cost.RejectedStage,
            CacheStatus = evaluation.CacheStatus,
            Diagnostics = evaluation.Diagnostics.Select(DiagnosticDocument.From).ToList(),
            Metrics = evaluation.Metrics.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            Artifacts = evaluation.Artifacts.Select(ArtifactDocument.From).ToList(),
            TaskVersionHash = evaluation.TaskVersionHash,
            EvaluatorVersionHash = evaluation.EvaluatorVersionHash,
            ConfigurationHash = evaluation.ConfigurationHash,
            MeasurementOriginJson = evaluation.MeasurementOrigin?.ToJson()
        };

        public EvolutionEvaluation ToEvaluation(long evaluationId, string genomeId, EvolutionLineage lineage)
        {
            var result = new EvolutionEvaluation(
            evaluationId, genomeId, Status, Quality, Direction,
            Descriptors ?? new Dictionary<string, double>(), Objectives ?? new List<double>(),
            ConstraintViolations ?? new List<double>(),
            new EvolutionEvaluationCost(TimeSpan.FromTicks(ElapsedTicks), AttemptCount, CostUnits,
                StageCostUnits ?? new List<double>(), RejectedStage),
            lineage, CacheStatus, (Diagnostics ?? new List<DiagnosticDocument>()).Select(item => item.ToDiagnostic()),
            TaskVersionHash, EvaluatorVersionHash, ConfigurationHash,
            Metrics ?? new Dictionary<string, double>(),
            (Artifacts ?? new List<ArtifactDocument>()).Select(item => item.ToArtifact()));
            return MeasurementOriginJson is null ? result : result.WithMeasurementOrigin(EvolutionMeasurementOrigin.FromJson(MeasurementOriginJson));
        }
    }

    internal sealed class TaskResultDocument
    {
        public EvolutionEvaluationStatus Status { get; set; }
        public double? Quality { get; set; }
        public EvolutionOptimizationDirection Direction { get; set; }
        public Dictionary<string, double>? Descriptors { get; set; }
        public List<double>? Objectives { get; set; }
        public List<double>? ConstraintViolations { get; set; }
        public double CostUnits { get; set; }
        public List<DiagnosticDocument>? Diagnostics { get; set; }
        public Dictionary<string, double>? Metrics { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MeasurementOriginJson { get; set; }

        public static TaskResultDocument From(EvolutionTaskResult result) => new()
        {
            Status = result.Status,
            Quality = result.Quality,
            Direction = result.Direction,
            Descriptors = result.Descriptors.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            Objectives = result.Objectives.ToList(),
            ConstraintViolations = result.ConstraintViolations.ToList(),
            CostUnits = result.CostUnits,
            Diagnostics = result.Diagnostics.Select(DiagnosticDocument.From).ToList(),
            Metrics = result.Metrics.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            MeasurementOriginJson = result.MeasurementOrigin?.ToJson()
        };

        public EvolutionTaskResult ToTaskResult()
        {
            var result = new EvolutionTaskResult(Status, Quality, Direction,
            Descriptors ?? new Dictionary<string, double>(), Objectives ?? new List<double>(),
            ConstraintViolations ?? new List<double>(), CostUnits,
            (Diagnostics ?? new List<DiagnosticDocument>()).Select(item => item.ToDiagnostic()),
            Metrics ?? new Dictionary<string, double>());
            return MeasurementOriginJson is null ? result : result.WithMeasurementOrigin(EvolutionMeasurementOrigin.FromJson(MeasurementOriginJson));
        }
    }

    internal sealed class DiagnosticDocument
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool IsRedacted { get; set; }
        public Dictionary<string, string>? Data { get; set; }

        public static DiagnosticDocument From(EvolutionDiagnostic diagnostic) => new()
        {
            Code = diagnostic.Code,
            Message = diagnostic.Message,
            IsRedacted = diagnostic.IsRedacted,
            Data = diagnostic.Data.Count == 0
                ? null
                : diagnostic.Data.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
        };

        public EvolutionDiagnostic ToDiagnostic()
        {
            try
            {
                return new EvolutionDiagnostic(Code, Message, IsRedacted, Data);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("A checkpoint diagnostic is invalid.", exception);
            }
        }
    }
}
