using System.Globalization;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class ParetoArchiveTests
{
    internal static EvolutionParetoDefinition Definition(int capacity = 16, int constraints = 0, double tolerance = 0) => new(new[]
    {
        new EvolutionObjectiveDefinition("time", EvolutionOptimizationDirection.Minimize, 0, 1, tolerance),
        new EvolutionObjectiveDefinition("accuracy", EvolutionOptimizationDirection.Maximize, 0, 1, tolerance)
    }, capacity, constraints);

    internal static EvolutionArchiveEntry<double> Entry(string id, double x, double y, double quality = 1, double[]? violations = null, long evaluationId = 0)
    {
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 1);
        var candidate = new EvolutionCandidate<double>(evaluationId, new EvolutionCanonicalGenome<double>(x, id), lineage);
        var evaluation = new EvolutionEvaluation(evaluationId, id, EvolutionEvaluationStatus.Completed, quality,
            EvolutionOptimizationDirection.Maximize, new Dictionary<string, double>(), new[] { x, y }, violations ?? Array.Empty<double>(),
            new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");
        return new(new EvolutionCellKey(new[] { 0 }), candidate, evaluation);
    }

    private static EvolutionArchiveInsertionResult Add(ParetoArchive<double> archive, EvolutionArchiveEntry<double> entry) => archive.TryAdd(entry.Candidate, entry.Evaluation);

    [Fact]
    public void ConflictingObjectivesRetainFrontNotScalarWinner()
    {
        var archive = new ParetoArchive<double>(Definition());
        Add(archive, Entry("fast", .1, .3, 1)); Add(archive, Entry("accurate", .8, .9, 99));
        Assert.Equal(EvolutionArchiveInsertionResult.NotImproved, Add(archive, Entry("dominated", .9, .2, 999)));
        Assert.Equal(2, archive.Count); Assert.Equal("accurate", archive.Best!.Evaluation.GenomeId);
        Assert.Equal("fast", archive.BestObjective("time")!.Evaluation.GenomeId);
        Assert.Equal("accurate", archive.BestObjective("accuracy")!.Evaluation.GenomeId);
        Assert.Equal(.39, archive.Hypervolume(), 12);
        Assert.Equal(EvolutionArchiveInsertionResult.Replaced, Add(archive, Entry("dominates-both", .05, .95, -1)));
        Assert.Single(archive.Entries); Assert.Equal(3, archive.Version);
    }

    [Theory]
    [InlineData(-.1, .5, 0)]
    [InlineData(.5, 1.1, 0)]
    [InlineData(.5, .5, 1)]
    public void DomainAndHardConstraintViolationsCannotWin(double x, double y, double violation)
    {
        var archive = new ParetoArchive<double>(Definition(constraints: 1));
        Assert.Equal(EvolutionArchiveInsertionResult.Rejected, Add(archive, Entry("invalid", x, y, 999, new[] { violation })));
        Assert.Empty(archive.Entries); Assert.Equal(0, archive.Version);
    }

    [Fact]
    public void MissingAndExtraConstraintsAndMismatchedCandidateAreRejected()
    {
        var archive = new ParetoArchive<double>(Definition(constraints: 1));
        Assert.Equal(EvolutionArchiveInsertionResult.Rejected, Add(archive, Entry("missing", .5, .5)));
        Assert.Equal(EvolutionArchiveInsertionResult.Rejected, Add(archive, Entry("extra", .5, .5, violations: new[] { 0.0, 0.0 })));
        var valid = Entry("valid", .5, .5, violations: new[] { 0.0 });
        Assert.Equal(EvolutionArchiveInsertionResult.Rejected, archive.TryAdd(Entry("other", .5, .5).Candidate, valid.Evaluation));
        Assert.Equal(EvolutionArchiveInsertionResult.Inserted, Add(archive, valid));
        Assert.Equal(EvolutionArchiveInsertionResult.NotImproved, Add(archive, valid));
    }

    [Fact]
    public void ToleranceIsTransitiveAndEquivalentBoxTieUsesIdentity()
    {
        var definition = Definition(tolerance: .125);
        var a = Entry("a", .25, .5); var b = Entry("b", .28, .49); var c = Entry("c", .30, .48);
        Assert.Equal(0, definition.Compare(a.Evaluation, b.Evaluation)); Assert.Equal(0, definition.Compare(b.Evaluation, c.Evaluation));
        foreach (var entries in new[] { new[] { a, b, c }, new[] { c, b, a } })
        {
            var archive = new ParetoArchive<double>(definition);
            foreach (var entry in entries) Add(archive, entry);
            Assert.Equal("a", Assert.Single(archive.Entries).Evaluation.GenomeId);
        }
        Assert.Equal(-1, definition.Compare(a.Evaluation, Entry("worse", .375, .5).Evaluation));
    }

    [Fact]
    public void CapacityPreservesExtremesAndReplayIsDeterministic()
    {
        var a = new ParetoArchive<double>(Definition(2)); var b = new ParetoArchive<double>(Definition(2));
        for (int i = 0; i <= 20; i++)
        {
            var entry = Entry(i.ToString("D2", CultureInfo.InvariantCulture), i / 20.0, i / 20.0);
            Assert.Equal(Add(a, entry), Add(b, entry)); Assert.InRange(a.Count, 1, 2);
        }
        Assert.Equal(a.Entries.Select(e => e.Evaluation.GenomeId), b.Entries.Select(e => e.Evaluation.GenomeId));
        Assert.Equal(new[] { "00", "20" }, a.Entries.Select(e => e.Evaluation.GenomeId).OrderBy(id => id));
    }

    [Fact]
    public void SnapshotCopiesFrontSemanticsAndRestoreIsAtomic()
    {
        var archive = new ParetoArchive<double>(Definition());
        Add(archive, Entry("a", .2, .3)); Add(archive, Entry("b", .7, .9));
        var snapshot = new EvolutionArchiveSnapshot<double>(archive);
        var copy = new EvolutionArchiveSnapshot<double>(snapshot);
        Add(archive, Entry("new", .1, .95));
        Assert.Equal(2, snapshot.ParetoFront().Count); Assert.Equal(snapshot.Hypervolume(), copy.Hypervolume());
        var restored = new ParetoArchive<double>(Definition());
        restored.Restore(copy.Entries, copy.Descriptors, copy.Version);
        Assert.Equal(copy.Entries, restored.Entries); Assert.Equal(copy.Version, restored.Version);
        Assert.Throws<InvalidOperationException>(() => restored.Restore(copy.Entries, copy.Descriptors, copy.Version));
        var bad = new ParetoArchive<double>(Definition());
        Assert.Throws<System.IO.InvalidDataException>(() => bad.Restore(new[] { copy.Entries[0], copy.Entries[0] }, copy.Descriptors, 2));
        Assert.Empty(bad.Entries); Assert.Equal(0, bad.Version);
        Assert.Throws<System.IO.InvalidDataException>(() => bad.Restore(new[] { Entry("forged-key", .1, .9) }, copy.Descriptors, 1));
        Assert.Throws<System.IO.InvalidDataException>(() => bad.Restore(copy.Entries, copy.Descriptors, -1));
    }

    [Fact]
    public void HypervolumeHasIndependentAnalyticAndDuplicateChecks()
    {
        var definition = Definition();
        var points = new[] { Entry("a", .2, .6).Evaluation, Entry("b", .5, .8).Evaluation };
        Assert.Equal(.58, EvolutionParetoQuery.Hypervolume(definition, points), 12);
        Assert.Equal(.58, EvolutionParetoQuery.Hypervolume(definition, points.Concat(points).Concat(new[] { Entry("dominated", .7, .3).Evaluation })), 12);
        Assert.Equal(0, EvolutionParetoQuery.Hypervolume(definition, Array.Empty<EvolutionEvaluation>()));
        var three = new EvolutionParetoDefinition(new[] { new EvolutionObjectiveDefinition("x", EvolutionOptimizationDirection.Minimize, 0, 1),
            new EvolutionObjectiveDefinition("y", EvolutionOptimizationDirection.Minimize, 0, 1), new EvolutionObjectiveDefinition("z", EvolutionOptimizationDirection.Minimize, 0, 1) });
        var e = points[0];
        EvolutionEvaluation Three(params double[] objectives) => new(e.EvaluationId, e.GenomeId, e.Status, e.Quality, e.Direction, e.Descriptors,
            objectives, e.ConstraintViolations, e.Cost, e.Lineage, e.CacheStatus, e.Diagnostics, "task", "eval", "config");
        Assert.Equal(.125, EvolutionParetoQuery.Hypervolume(three, new[] { Three(.5, .5, .5) }), 12);
        Assert.Equal(.1875, EvolutionParetoQuery.Hypervolume(three, new[] { Three(.25, .5, .5), Three(.5, .5, .5) }), 12);
    }

    [Fact]
    public void SelectionAndMigrationUseEntireFrontWithoutScalarRanking()
    {
        var archive = new ParetoArchive<double>(Definition());
        Add(archive, Entry("a", 0, 0, -100)); Add(archive, Entry("b", .5, .5, 100)); Add(archive, Entry("c", 1, 1, -100));
        var policy = new ParetoEvolutionSelectionPolicy<double>();
        var random = StableRandom.CreateStream(5, 1); var seen = new HashSet<string>();
        for (int i = 0; i < 100; i++)
        {
            var selected = policy.Select(archive, random, 2)!; seen.Add(selected.Parent.Evaluation.GenomeId);
            Assert.Equal(2, selected.Inspirations.Count); Assert.DoesNotContain(selected.Parent, selected.Inspirations);
        }
        Assert.Equal(3, seen.Count);
        var empty = new ParetoArchive<double>(Definition());
        var moves = new ParetoMigrationPolicy<double>().CreateMigrations(new IEvolutionArchiveView<double>[] { new EvolutionArchiveSnapshot<double>(archive), empty }, 2, random);
        Assert.Equal(new[] { "a", "c" }, moves.Select(move => move.Entry.Evaluation.GenomeId));
    }

    [Theory]
    [InlineData(double.NaN, 1, 0)]
    [InlineData(0, double.PositiveInfinity, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(-1e308, 1e308, 0)]
    [InlineData(0, 1, -1)]
    [InlineData(0, 1, 1e-15)]
    public void InvalidAxesFailClosed(double minimum, double maximum, double tolerance) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new EvolutionObjectiveDefinition("x", EvolutionOptimizationDirection.Minimize, minimum, maximum, tolerance));

    [Fact]
    public void DefinitionChangesInvalidateCompatibility()
    {
        Assert.NotEqual(Definition().DefinitionHash, Definition(2).DefinitionHash);
        Assert.NotEqual(Definition().DefinitionHash, Definition(constraints: 1).DefinitionHash);
        Assert.NotEqual(Definition().DefinitionHash, Definition(tolerance: .1).DefinitionHash);
        Assert.Throws<ArgumentException>(() => new EvolutionParetoDefinition(new[] { Definition().Objectives[0] }));
        Assert.Throws<ArgumentException>(() => new EvolutionParetoDefinition(new[] { Definition().Objectives[0], Definition().Objectives[0] }));
    }

    [Fact]
    public void EveryInsertionMaintainsPairwiseNondominanceAndHardBound()
    {
        var definition = Definition(8); var archive = new ParetoArchive<double>(definition);
        var random = StableRandom.CreateStream(871, 3);
        for (int i = 0; i < 250; i++)
        {
            Add(archive, Entry(i.ToString(CultureInfo.InvariantCulture), random.NextDouble(), random.NextDouble()));
            Assert.InRange(archive.Count, 1, 8);
            foreach (var a in archive.Entries)
                foreach (var b in archive.Entries)
                    if (!ReferenceEquals(a, b)) Assert.Equal(0, definition.Compare(a.Evaluation, b.Evaluation));
            Assert.All(archive.Entries, entry => Assert.True(definition.IsFeasible(entry.Evaluation)));
        }
    }

    [Fact]
    public void ThreeDimensionalVolumeMatchesIndependentVoxelUnion()
    {
        var definition = new EvolutionParetoDefinition(Enumerable.Range(0, 3).Select(i =>
            new EvolutionObjectiveDefinition("axis" + i, EvolutionOptimizationDirection.Minimize, 0, 4)));
        var random = StableRandom.CreateStream(4, 9); var evaluations = new List<EvolutionEvaluation>();
        for (int i = 0; i < 20; i++)
        {
            var e = Entry("e", .5, .5).Evaluation;
            evaluations.Add(new EvolutionEvaluation(i, "g" + i, e.Status, e.Quality, e.Direction, e.Descriptors,
                new double[] { random.NextInt(0, 5), random.NextInt(0, 5), random.NextInt(0, 5) }, e.ConstraintViolations,
                e.Cost, e.Lineage, e.CacheStatus, e.Diagnostics, "task", "eval", "config"));
        }
        int covered = 0;
        for (int x = 0; x < 4; x++)
            for (int y = 0; y < 4; y++)
                for (int z = 0; z < 4; z++)
                    if (evaluations.Any(e => e.Objectives[0] <= x && e.Objectives[1] <= y && e.Objectives[2] <= z)) covered++;
        Assert.Equal(covered / 64.0, EvolutionParetoQuery.Hypervolume(definition, evaluations), 12);
    }
}
