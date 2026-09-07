using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionPublicContractTests
{
    [Fact]
    public void HashUsesStableUtf8Sha256Output()
    {
        Assert.Equal(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            EvolutionHash.Compute("hello"));
    }

    [Fact]
    public void CombinedHashPreservesComponentBoundaries()
    {
        Assert.NotEqual(
            EvolutionHash.Combine(new[] { "ab", "c" }),
            EvolutionHash.Combine(new[] { "a", "bc" }));
    }

    [Fact]
    public void EngineSnapshotDeepCopiesNestedOptions()
    {
        var source = new EvolutionEngineOptions
        {
            Selection = new EvolutionSelectionOptions { TopInspirationCount = 3 }
        };

        EvolutionEngineOptions snapshot = source.SnapshotAndValidate();
        source.Selection.TopInspirationCount = 9;

        Assert.NotSame(source.Selection, snapshot.Selection);
        Assert.Equal(3, snapshot.Selection.TopInspirationCount);
    }

    [Fact]
    public void ConfigurationHashChangesForSemanticOptionsButNotBudgets()
    {
        var baseline = new EvolutionEngineOptions();
        var semanticChange = new EvolutionEngineOptions { Seed = baseline.Seed + 1 };
        var budgetChange = new EvolutionEngineOptions
        {
            MaxEvaluationAttempts = baseline.MaxEvaluationAttempts + 1
        };

        Assert.NotEqual(baseline.GetConfigurationHash(), semanticChange.GetConfigurationHash());
        Assert.Equal(baseline.GetConfigurationHash(), budgetChange.GetConfigurationHash());
    }

    [Fact]
    public void TraceSnapshotNormalizesWithoutMutatingSource()
    {
        var source = new EvolutionTraceOptions
        {
            Enabled = true,
            Path = "  evolution.jsonl  "
        };

        EvolutionTraceOptions snapshot = source.SnapshotAndValidate();

        Assert.Equal("  evolution.jsonl  ", source.Path);
        Assert.Equal("evolution.jsonl", snapshot.Path);
    }

    [Fact]
    public void TraceRetentionCannotExceedPackageLevelResourceLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionTraceOptions
        {
            MaxRecords = EvolutionCollectionLimits.MaximumTraceRecords + 1L
        }.SnapshotAndValidate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionTraceOptions
        {
            MaxBytes = EvolutionCollectionLimits.MaximumTraceBytes + 1
        }.SnapshotAndValidate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionTraceOptions
        {
            ParentQualityCacheSize = EvolutionCollectionLimits.MaximumParentQualityCacheEntries + 1
        }.SnapshotAndValidate());
    }

    [Fact]
    public void ArtifactRetentionCannotExceedIndividualOrAggregatePackageLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionArtifactOptions
        {
            MaxArtifactBytes = EvolutionCollectionLimits.MaximumArtifactBytes + 1
        }.SnapshotAndValidate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionArtifactOptions
        {
            MaxBytesPerEvaluation = EvolutionCollectionLimits.MaximumArtifactBytesPerEvaluation + 1
        }.SnapshotAndValidate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionArtifactOptions
        {
            MaxBytesPerEvaluation = EvolutionCollectionLimits.MaximumArtifactBytesPerEvaluation,
            MaxPendingCandidates = 17
        }.SnapshotAndValidate());
    }
}
