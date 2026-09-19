namespace AiDotNet.Evolution;

/// <summary>A completed offline projection and immutable provenance captured at publication.</summary>
/// <typeparam name="TGenome">The immutable task-specific genome.</typeparam>
/// <remarks>The source is unchanged. The returned archive belongs to the caller and can subsequently
/// change; the report still describes this completed projection. Retain the report and full target
/// geometry with experiment/checkpoint configuration. It does not authorize reuse across task semantics.</remarks>
public sealed class CentroidArchiveProjection<TGenome>
{
    internal CentroidArchiveProjection(CentroidArchive<TGenome> archive, EvolutionArchiveProjectionReport report)
    {
        Archive = archive;
        Report = report;
    }

    /// <summary>Gets the new caller-owned archive after all source entries were validated and routed.</summary>
    public CentroidArchive<TGenome> Archive { get; }
    /// <summary>Gets the immutable source/target provenance for this completed transaction.</summary>
    public EvolutionArchiveProjectionReport Report { get; }
}

/// <summary>Bounded metadata for an offline archive remap, without genome or evaluation payloads.</summary>
public sealed class EvolutionArchiveProjectionReport
{
    internal EvolutionArchiveProjectionReport(string sourceDefinitionHash, long sourceVersion, int sourceCount,
        string targetDefinitionHash, long targetVersion, int retainedCount)
    {
        SourceDefinitionHash = sourceDefinitionHash;
        SourceVersion = sourceVersion;
        SourceEliteCount = sourceCount;
        TargetDefinitionHash = targetDefinitionHash;
        TargetVersion = targetVersion;
        RetainedEliteCount = retainedCount;
    }

    /// <summary>Gets the reporting schema, independent of archive geometry/checkpoint versions.</summary>
    public int SchemaVersion => 1;
    /// <summary>Gets the source archive's normalization/partition/direction fingerprint.</summary>
    public string SourceDefinitionHash { get; }
    /// <summary>Gets the source mutation version captured before projection.</summary>
    public long SourceVersion { get; }
    /// <summary>Gets the number of retained source elites offered to the new partition.</summary>
    public int SourceEliteCount { get; }
    /// <summary>Gets the target archive fingerprint used by checkpoint compatibility.</summary>
    public string TargetDefinitionHash { get; }
    /// <summary>Gets the published target mutation version, not its later live version.</summary>
    public long TargetVersion { get; }
    /// <summary>Gets the number of target elites after deterministic collision resolution.</summary>
    public int RetainedEliteCount { get; }
    /// <summary>Gets source elites lost to target-cell collisions, not failed evaluations.</summary>
    public int CollisionDiscardedEliteCount => SourceEliteCount - RetainedEliteCount;
    /// <summary>Gets whether normalization, partition or other definition semantics changed.</summary>
    public bool DefinitionChanged => SourceDefinitionHash != TargetDefinitionHash;
}
