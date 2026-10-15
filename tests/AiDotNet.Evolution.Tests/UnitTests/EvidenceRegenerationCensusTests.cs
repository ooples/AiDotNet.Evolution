#if NET8_0_OR_GREATER
using System.Text.Json;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>How a committed evidence README's claims could be re-derived by a reader.</summary>
internal enum EvidenceReadmeClass
{
    /// <summary>Carries the generated-table marker pair, so a script re-renders it and drift fails a build.</summary>
    Generated,

    /// <summary>No generated block, but machine-readable evidence sits at or beneath its own directory.</summary>
    RawAlongside,

    /// <summary>Neither: the numbers in it exist nowhere else in the repository.</summary>
    Unsupported,
}

/// <summary>
/// US-04 (#22): the measured value behind the metric's "partial", and a ratchet on its direction.
/// </summary>
/// <remarks>
/// <para>
/// <c>eng/Measure-EvidenceRegeneration.ps1</c> produces the census and
/// <c>benchmarks/evidence/evidence-regeneration</c> commits it. These tests are what stops that
/// record from drifting into a wish. The classification is re-derived here from the live tree
/// rather than read out of the committed JSON, so the JSON cannot be improved by editing it.
/// </para>
/// <para>
/// The ratchet bounds are constants in this file on purpose. Comparing the live tree against the
/// committed census alone would pass for any tree, as long as someone re-ran the script and
/// committed the worse answer; the direction has to be pinned somewhere the measurement cannot
/// rewrite.
/// </para>
/// </remarks>
public sealed class EvidenceRegenerationCensusTests
{
    /// <summary>The marker pair <c>eng/Test-EvidenceTables.ps1</c> compares against.</summary>
    private const string BeginMarker = "<!-- BEGIN GENERATED TABLES -->";
    private const string EndMarker = "<!-- END GENERATED TABLES -->";

    // Measured at main 86d56c9. Raising Generated or lowering either of the others is the work;
    // moving one the wrong way has to fail, which is the only reason these are literals.
    private const int MinimumGenerated = 1;
    private const int MaximumUnsupported = 2;
    private const int MaximumUndocumentedFamilies = 0;

    [Fact]
    public void Every_evidence_readme_is_classified_by_the_committed_census()
    {
        Dictionary<string, EvidenceReadmeClass> live = ClassifyTree(EvidenceRoot());
        Dictionary<string, EvidenceReadmeClass> committed = ReadCommittedCensus();

        // Set equality in both directions, so a new family fails until it is classified and a
        // deleted one fails until it is dropped. An allow-list would let an unclassified family in.
        Assert.Equal(
            committed.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            live.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_census_may_only_move_towards_checkable_evidence()
    {
        Dictionary<string, EvidenceReadmeClass> live = ClassifyTree(EvidenceRoot());
        int generated = live.Values.Count(value => value == EvidenceReadmeClass.Generated);
        int unsupported = live.Values.Count(value => value == EvidenceReadmeClass.Unsupported);
        int undocumented = UndocumentedFamilies(EvidenceRoot()).Count;

        Assert.True(generated >= MinimumGenerated,
            $"{generated} READMEs regenerate, below the recorded {MinimumGenerated}.");
        Assert.True(unsupported <= MaximumUnsupported,
            $"{unsupported} READMEs have no committed evidence, above the recorded {MaximumUnsupported}: " +
            string.Join(", ", live.Where(pair => pair.Value == EvidenceReadmeClass.Unsupported).Select(pair => pair.Key)));
        Assert.True(undocumented <= MaximumUndocumentedFamilies,
            $"{undocumented} families have no README, above the recorded {MaximumUndocumentedFamilies}: " +
            string.Join(", ", UndocumentedFamilies(EvidenceRoot())));
    }

    [Fact]
    public void The_classifier_finds_one_of_every_class_in_an_authored_tree()
    {
        // A positive control. "0 Unsupported" is what a classifier that matched nothing would
        // report, so the rules are first run against a tree built to hold one of every class.
        string root = Path.Combine(Path.GetTempPath(), "evidence-census-" + Guid.NewGuid().ToString("N"));
        try
        {
            Write(Path.Combine(root, "fam-generated", "README.md"), "# generated\n" + BeginMarker + "\n| a |\n" + EndMarker);
            Write(Path.Combine(root, "fam-generated", "summary.json"), "{}");
            Write(Path.Combine(root, "fam-raw", "abc1234", "README.md"), "# raw, 12.5 ms");
            Write(Path.Combine(root, "fam-raw", "abc1234", "runs.jsonl"), "{\"ms\":12.5}");
            Write(Path.Combine(root, "fam-unsupported", "abc1234", "README.md"), "# 1 call in 5, from nowhere");

            // Half a marker pair is not a generated block. Without this arm, emitting the opening
            // marker alone would count as regenerating - the one claim that must not be cheap.
            Write(Path.Combine(root, "fam-half-marker", "README.md"), "# half\n" + BeginMarker + "\n| a |");
            Write(Path.Combine(root, "fam-undocumented", "analysis.json"), "{}");

            Dictionary<string, EvidenceReadmeClass> classified = ClassifyTree(root);

            Assert.Equal(EvidenceReadmeClass.Generated, classified["fam-generated/README.md"]);
            Assert.Equal(EvidenceReadmeClass.RawAlongside, classified["fam-raw/abc1234/README.md"]);
            Assert.Equal(EvidenceReadmeClass.Unsupported, classified["fam-unsupported/abc1234/README.md"]);
            Assert.Equal(EvidenceReadmeClass.Unsupported, classified["fam-half-marker/README.md"]);
            Assert.Equal(4, classified.Count);
            Assert.Equal(new[] { "fam-undocumented" }, UndocumentedFamilies(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null) Directory.CreateDirectory(directory);
        File.WriteAllText(path, content);
    }

    private static Dictionary<string, EvidenceReadmeClass> ClassifyTree(string root)
    {
        var classified = new Dictionary<string, EvidenceReadmeClass>(StringComparer.Ordinal);
        foreach (string readme in Directory.EnumerateFiles(root, "README.md", SearchOption.AllDirectories))
        {
            string key = Path.GetRelativePath(root, readme).Replace(Path.DirectorySeparatorChar, '/');
            classified.Add(key, Classify(readme));
        }
        if (classified.Count == 0) throw new InvalidOperationException("No evidence READMEs under " + root);
        return classified;
    }

    private static EvidenceReadmeClass Classify(string readmePath)
    {
        string text = File.ReadAllText(readmePath);
        if (text.Contains(BeginMarker, StringComparison.Ordinal) && text.Contains(EndMarker, StringComparison.Ordinal))
            return EvidenceReadmeClass.Generated;
        // At or beneath, so an index README is credited with the revision directories under it.
        // Anything that is not a README counts: an extension list silently reclassifies a family
        // that commits a kind of artifact the list does not name.
        string directory = Path.GetDirectoryName(readmePath) ?? throw new InvalidOperationException(readmePath);
        bool artifact = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Any(path => !string.Equals(Path.GetFileName(path), "README.md", StringComparison.OrdinalIgnoreCase));
        return artifact ? EvidenceReadmeClass.RawAlongside : EvidenceReadmeClass.Unsupported;
    }

    private static List<string> UndocumentedFamilies(string root) =>
        Directory.EnumerateDirectories(root)
            .Where(family => !Directory.EnumerateFiles(family, "README.md", SearchOption.AllDirectories).Any()
                && Directory.EnumerateFiles(family, "*", SearchOption.AllDirectories).Any())
            .Select(family => Path.GetFileName(family))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, EvidenceReadmeClass> ReadCommittedCensus()
    {
        string directory = Path.Combine(EvidenceRoot(), "evidence-regeneration");
        string[] revisions = Directory.GetDirectories(directory);
        // Exactly one, the same rule eng/Test-EvidenceTables.ps1 applies to its summaries: two
        // censuses would let the test pick whichever one happened to agree with the tree.
        Assert.Single(revisions);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(revisions[0], "census.json")));
        var classified = new Dictionary<string, EvidenceReadmeClass>(StringComparer.Ordinal);
        foreach (JsonElement family in document.RootElement.GetProperty("Families").EnumerateArray())
            foreach (JsonElement readme in family.GetProperty("Readmes").EnumerateArray())
                classified.Add(
                    readme.GetProperty("Path").GetString() ?? throw new InvalidOperationException("Census row without a path."),
                    Enum.Parse<EvidenceReadmeClass>(readme.GetProperty("Class").GetString() ?? string.Empty));
        return classified;
    }

    private static string EvidenceRoot() => Path.Combine(RepositoryRoot(), "benchmarks", "evidence");

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "benchmarks", "evidence"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
#endif
