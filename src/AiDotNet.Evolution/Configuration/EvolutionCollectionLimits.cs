namespace AiDotNet.Evolution;

/// <summary>Hard safety bounds applied at public collection and hashing boundaries.</summary>
public static class EvolutionCollectionLimits
{
    /// <summary>Maximum independent islands in one engine.</summary>
    public const int MaximumIslands = 1024;

    /// <summary>Maximum descriptor dimensions in one archive cell.</summary>
    public const int MaximumArchiveDimensions = 64;

    /// <summary>Maximum stages in one cascade evaluator.</summary>
    public const int MaximumCascadeStages = 256;

    /// <summary>Maximum parent or inspiration identities attached to one lineage record.</summary>
    public const int MaximumLineageIdentities = 256;

    /// <summary>Maximum components accepted by one combined identity hash.</summary>
    public const int MaximumHashComponents = 4096;

    /// <summary>Maximum UTF-16 characters accepted by one combined identity hash.</summary>
    public const int MaximumHashCharacters = 16 * 1024 * 1024;

    /// <summary>Maximum island snapshots accepted by a public result.</summary>
    public const int MaximumResultIslands = 1024;

    /// <summary>Maximum entries accepted by any auxiliary public result collection.</summary>
    public const int MaximumResultEntries = 1_000_000;

    /// <summary>Maximum migrations applied in one round.</summary>
    public const int MaximumMigrationTransfers = 65_536;

    /// <summary>Maximum records materialized into one trace read result.</summary>
    public const int MaximumTraceRecords = 1_000_000;
}
