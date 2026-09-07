using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>
/// Counts retained checkpoint collections while JSON is still a token stream, before the serializer
/// can allocate attacker-controlled lists and dictionaries.
/// </summary>
internal static class EvolutionCheckpointJsonPreflight
{
    internal static void Validate(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        var frames = new List<ContainerFrame>(16);
        int retainedEntryCount = 0;

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        ContainerFrame objectFrame = CurrentObject(frames);
                        if (objectFrame.CountObjectProperties)
                            Increment(objectFrame, ref retainedEntryCount);
                        objectFrame.PendingCollection = ClassifyProperty(ref reader);
                        break;

                    case JsonTokenType.StartArray:
                        {
                            CheckpointCollectionKind nestedKind = NestedArrayKind(frames);
                            CountArrayElement(frames, ref retainedEntryCount);
                            CheckpointCollectionKind kind = TakePendingCollection(frames);
                            if (kind == CheckpointCollectionKind.None) kind = nestedKind;
                            frames.Add(ContainerFrame.Array(kind, ArrayLimit(kind)));
                            break;
                        }

                    case JsonTokenType.StartObject:
                        {
                            CountArrayElement(frames, ref retainedEntryCount);
                            CheckpointCollectionKind kind = TakePendingCollection(frames);
                            int objectLimit = ObjectLimit(kind);
                            frames.Add(ContainerFrame.Object(kind, objectLimit, objectLimit != int.MaxValue));
                            break;
                        }

                    case JsonTokenType.EndArray:
                        Pop(frames, isArray: true);
                        break;

                    case JsonTokenType.EndObject:
                        Pop(frames, isArray: false);
                        break;

                    default:
                        CountArrayElement(frames, ref retainedEntryCount);
                        ClearPendingCollection(frames);
                        break;
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The evolution engine state payload is invalid.", exception);
        }

        if (frames.Count != 0)
            throw new InvalidDataException("The evolution engine state payload is incomplete.");
    }

    private static ContainerFrame CurrentObject(IReadOnlyList<ContainerFrame> frames)
    {
        if (frames.Count == 0 || frames[frames.Count - 1].IsArray)
            throw new InvalidDataException("The evolution engine state payload has an invalid property boundary.");
        return frames[frames.Count - 1];
    }

    private static void CountArrayElement(
        IReadOnlyList<ContainerFrame> frames,
        ref int retainedEntryCount)
    {
        if (frames.Count == 0) return;
        ContainerFrame frame = frames[frames.Count - 1];
        if (frame.IsArray) Increment(frame, ref retainedEntryCount);
    }

    private static void Increment(ContainerFrame frame, ref int retainedEntryCount)
    {
        frame.Count++;
        if (frame.Count > frame.Limit)
            throw new InvalidDataException(
                $"Checkpoint collection '{frame.Collection}' exceeds its package safety limit.");

        if (frame.Collection is CheckpointCollectionKind.ArchiveEntries or
            CheckpointCollectionKind.GlobalElites)
        {
            if (retainedEntryCount == EvolutionCollectionLimits.MaximumResultEntries)
                throw new InvalidDataException(
                    "The checkpoint exceeds the aggregate candidate-entry limit.");
            retainedEntryCount++;
        }
    }

    private static CheckpointCollectionKind NestedArrayKind(IReadOnlyList<ContainerFrame> frames)
    {
        if (frames.Count == 0) return CheckpointCollectionKind.None;
        ContainerFrame parent = frames[frames.Count - 1];
        return parent.IsArray && parent.Collection == CheckpointCollectionKind.IslandHistories
            ? CheckpointCollectionKind.ArchiveEntries
            : CheckpointCollectionKind.None;
    }

    private static CheckpointCollectionKind TakePendingCollection(IReadOnlyList<ContainerFrame> frames)
    {
        if (frames.Count == 0) return CheckpointCollectionKind.None;
        ContainerFrame parent = frames[frames.Count - 1];
        CheckpointCollectionKind kind = parent.PendingCollection;
        parent.PendingCollection = CheckpointCollectionKind.None;
        return kind;
    }

    private static void ClearPendingCollection(IReadOnlyList<ContainerFrame> frames)
    {
        if (frames.Count == 0) return;
        ContainerFrame parent = frames[frames.Count - 1];
        if (!parent.IsArray) parent.PendingCollection = CheckpointCollectionKind.None;
    }

    private static void Pop(List<ContainerFrame> frames, bool isArray)
    {
        if (frames.Count == 0 || frames[frames.Count - 1].IsArray != isArray)
            throw new InvalidDataException("The evolution engine state payload has mismatched containers.");
        frames.RemoveAt(frames.Count - 1);
    }

    private static CheckpointCollectionKind ClassifyProperty(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("SemanticOptions")) return CheckpointCollectionKind.SemanticOptions;
        if (reader.ValueTextEquals("BudgetOptions")) return CheckpointCollectionKind.BudgetOptions;
        if (reader.ValueTextEquals("SeedPayloads")) return CheckpointCollectionKind.SeedPayloads;
        if (reader.ValueTextEquals("IslandGenerations")) return CheckpointCollectionKind.IslandGenerations;
        if (reader.ValueTextEquals("GlobalElites")) return CheckpointCollectionKind.GlobalElites;
        if (reader.ValueTextEquals("IslandHistories")) return CheckpointCollectionKind.IslandHistories;
        if (reader.ValueTextEquals("StatusCounts")) return CheckpointCollectionKind.StatusCounts;
        if (reader.ValueTextEquals("SeenGenomeIds")) return CheckpointCollectionKind.SeenGenomeIds;
        if (reader.ValueTextEquals("Cache")) return CheckpointCollectionKind.Cache;
        if (reader.ValueTextEquals("Failures")) return CheckpointCollectionKind.Failures;
        if (reader.ValueTextEquals("PendingArtifacts")) return CheckpointCollectionKind.PendingArtifacts;
        if (reader.ValueTextEquals("Islands")) return CheckpointCollectionKind.Islands;
        if (reader.ValueTextEquals("Entries")) return CheckpointCollectionKind.ArchiveEntries;
        if (reader.ValueTextEquals("Descriptors")) return CheckpointCollectionKind.Descriptors;
        if (reader.ValueTextEquals("CellBins")) return CheckpointCollectionKind.CellBins;
        if (reader.ValueTextEquals("ParentIds")) return CheckpointCollectionKind.ParentIds;
        if (reader.ValueTextEquals("InspirationIds")) return CheckpointCollectionKind.InspirationIds;
        if (reader.ValueTextEquals("Objectives")) return CheckpointCollectionKind.Objectives;
        if (reader.ValueTextEquals("ConstraintViolations")) return CheckpointCollectionKind.ConstraintViolations;
        if (reader.ValueTextEquals("StageCostUnits")) return CheckpointCollectionKind.StageCostUnits;
        if (reader.ValueTextEquals("Diagnostics")) return CheckpointCollectionKind.Diagnostics;
        if (reader.ValueTextEquals("Metrics")) return CheckpointCollectionKind.Metrics;
        if (reader.ValueTextEquals("Artifacts")) return CheckpointCollectionKind.Artifacts;
        if (reader.ValueTextEquals("Data")) return CheckpointCollectionKind.DiagnosticData;
        return CheckpointCollectionKind.None;
    }

    private static int ArrayLimit(CheckpointCollectionKind kind) => kind switch
    {
        CheckpointCollectionKind.SemanticOptions or CheckpointCollectionKind.BudgetOptions =>
            EvolutionCollectionLimits.MaximumHashComponents,
        CheckpointCollectionKind.IslandGenerations or CheckpointCollectionKind.IslandHistories or
        CheckpointCollectionKind.Islands => EvolutionCollectionLimits.MaximumResultIslands,
        CheckpointCollectionKind.Descriptors or CheckpointCollectionKind.CellBins =>
            EvolutionCollectionLimits.MaximumArchiveDimensions,
        CheckpointCollectionKind.ParentIds or CheckpointCollectionKind.InspirationIds =>
            EvolutionCollectionLimits.MaximumLineageIdentities,
        CheckpointCollectionKind.Objectives or CheckpointCollectionKind.ConstraintViolations =>
            EvolutionTaskResult.MaximumVectorValues,
        CheckpointCollectionKind.StageCostUnits => EvolutionCollectionLimits.MaximumCascadeStages,
        CheckpointCollectionKind.Diagnostics => EvolutionTaskResult.MaximumDiagnostics,
        CheckpointCollectionKind.Artifacts => EvolutionTaskResult.MaximumArtifacts,
        CheckpointCollectionKind.SeedPayloads or CheckpointCollectionKind.GlobalElites or
        CheckpointCollectionKind.StatusCounts or CheckpointCollectionKind.SeenGenomeIds or
        CheckpointCollectionKind.Cache or CheckpointCollectionKind.Failures or
        CheckpointCollectionKind.PendingArtifacts or CheckpointCollectionKind.ArchiveEntries =>
            EvolutionCollectionLimits.MaximumResultEntries,
        _ => int.MaxValue
    };

    private static int ObjectLimit(CheckpointCollectionKind kind) => kind switch
    {
        CheckpointCollectionKind.Descriptors or CheckpointCollectionKind.Metrics =>
            EvolutionTaskResult.MaximumNamedValues,
        CheckpointCollectionKind.DiagnosticData => EvolutionDiagnostic.MaximumDataEntries,
        _ => int.MaxValue
    };

    private enum CheckpointCollectionKind
    {
        None = 0,
        SemanticOptions,
        BudgetOptions,
        SeedPayloads,
        IslandGenerations,
        GlobalElites,
        IslandHistories,
        StatusCounts,
        SeenGenomeIds,
        Cache,
        Failures,
        PendingArtifacts,
        Islands,
        ArchiveEntries,
        Descriptors,
        CellBins,
        ParentIds,
        InspirationIds,
        Objectives,
        ConstraintViolations,
        StageCostUnits,
        Diagnostics,
        Metrics,
        Artifacts,
        DiagnosticData
    }

    private sealed class ContainerFrame
    {
        private ContainerFrame(
            bool isArray,
            CheckpointCollectionKind collection,
            int limit,
            bool countObjectProperties)
        {
            IsArray = isArray;
            Collection = collection;
            Limit = limit;
            CountObjectProperties = countObjectProperties;
        }

        internal bool IsArray { get; }
        internal CheckpointCollectionKind Collection { get; }
        internal int Limit { get; }
        internal bool CountObjectProperties { get; }
        internal int Count { get; set; }
        internal CheckpointCollectionKind PendingCollection { get; set; }

        internal static ContainerFrame Array(CheckpointCollectionKind collection, int limit) =>
            new(isArray: true, collection, limit, countObjectProperties: false);

        internal static ContainerFrame Object(
            CheckpointCollectionKind collection,
            int limit,
            bool countObjectProperties) =>
            new(isArray: false, collection, limit, countObjectProperties);
    }
}
