#if NET8_0_OR_GREATER
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const string CRLF = "\r\n";
    private const string LF = "\n";

    // Measured at main 86d56c9. Raising Generated or lowering either of the others is the work;
    // moving one the wrong way has to fail, which is the only reason these are literals.
    private const int MinimumGenerated = 4;
    private const int MaximumUnsupported = 1;
    private const int MaximumUndocumentedFamilies = 0;

    [Fact]
    public void The_committed_census_agrees_with_the_live_tree_where_they_overlap_and_never_flatters_it()
    {
        Dictionary<string, EvidenceReadmeClass> live = ClassifyTree(EvidenceRoot());
        Dictionary<string, EvidenceReadmeClass> committed = ReadCommittedCensus();

        // Every README the census does classify must still classify the same way. A reading that has
        // gone stale by being incomplete is a dated reading; one that disagrees is simply wrong.
        foreach ((string path, EvidenceReadmeClass recorded) in committed.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (live.TryGetValue(path, out EvidenceReadmeClass current))
                Assert.Equal(recorded, current);

        // And it may not report a better tree than the one that exists. This is what set equality
        // was really protecting, without the part that made an unrelated merge turn main red: two
        // PRs that never touch the same line - one committing the census, one adding a documented
        // evidence family - left the census short by four READMEs and the test failing on main.
        int liveGenerated = live.Values.Count(value => value == EvidenceReadmeClass.Generated);
        int liveUnsupported = live.Values.Count(value => value == EvidenceReadmeClass.Unsupported);
        int recordedGenerated = committed.Values.Count(value => value == EvidenceReadmeClass.Generated);
        int recordedUnsupported = committed.Values.Count(value => value == EvidenceReadmeClass.Unsupported);

        Assert.True(recordedGenerated <= liveGenerated,
            $"The census claims {recordedGenerated} regenerating READMEs; the tree has {liveGenerated}.");
        Assert.True(recordedUnsupported >= liveUnsupported,
            $"The census admits {recordedUnsupported} unsupported READMEs; the tree has {liveUnsupported}: " +
            string.Join(", ", live.Where(pair => pair.Value == EvidenceReadmeClass.Unsupported).Select(pair => pair.Key)));
        Assert.True(committed.Count <= live.Count,
            $"The census classifies {committed.Count} READMEs; only {live.Count} exist.");
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
    public void The_censuss_own_published_block_regenerates_from_the_committed_census()
    {
        // The census family is Generated, so it owes the same property the performance family owes:
        // the block it publishes must come out of census.json and not out of someone's edit. This
        // renders independently of eng/Measure-EvidenceRegeneration.ps1 -RenderTables, so the two
        // disagreeing is a failure here rather than a silently regenerated README.
        string readme = Path.Combine(CensusDirectory(), "README.md");
        string text = File.ReadAllText(readme).Replace("\r\n", "\n");
        int begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        int end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "The census README has no generated-table block.");

        string published = text.Substring(begin + BeginMarker.Length, end - begin - BeginMarker.Length).Trim();
        Assert.Equal(RenderCensusTables(), published);
    }

    private static string RenderCensusTables()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CensusDirectory(), "census.json")));
        JsonElement totals = document.RootElement.GetProperty("Totals");
        var rows = new (string Label, string Field)[]
        {
            ("`Generated` READMEs", "Generated"),
            ("`RawAlongside` READMEs", "RawAlongside"),
            ("`Unsupported` READMEs", "Unsupported"),
            ("READMEs in total", "Readmes"),
            ("families", "Families"),
            ("families with no README", "Undocumented"),
        };
        var lines = new List<string> { "| measure | count |", "| --- | ---: |" };
        foreach ((string label, string field) in rows)
            lines.Add($"| {label} | {totals.GetProperty(field).GetInt32()} |");
        return string.Join("\n", lines);
    }

    [Fact]
    public void Every_card_target_block_regenerates_from_the_card_it_names()
    {
        // Each of these READMEs names the card its block came from, in the command it tells a reader
        // to run. The card path is taken from that line rather than from a list here, so a README
        // that documents the wrong card fails instead of being checked against the right one.
        var pattern = new Regex(@"-CardPath ([^\s`]+)", RegexOptions.None, TimeSpan.FromSeconds(1));
        int checked_ = 0;
        foreach (string readme in Directory.EnumerateFiles(EvidenceRoot(), "README.md", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(readme).Replace(CRLF, LF);
            Match match = pattern.Match(text);
            if (!match.Success) continue;

            int begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
            int end = text.IndexOf(EndMarker, StringComparison.Ordinal);
            Assert.True(begin >= 0 && end > begin, readme + " names a card but has no generated block.");

            string directory = Path.GetDirectoryName(readme) ?? throw new InvalidOperationException(readme);
            string card = Path.Combine(directory, match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(card), "The card " + card + " named by " + readme + " does not exist.");

            string published = text.Substring(begin + BeginMarker.Length, end - begin - BeginMarker.Length).Trim();
            Assert.Equal(RenderCardTargets(card), published);
            checked_++;
        }

        // Without this the test passes on a tree where every block was deleted.
        Assert.True(checked_ >= 2, $"Expected at least two card-target blocks, found {checked_}.");
    }

    private static string RenderCardTargets(string cardPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(cardPath));
        JsonElement targets = document.RootElement.GetProperty("targets");
        var lines = new List<string> { "| field | target | measured | met |", "| --- | --- | ---: | --- |" };
        foreach (JsonProperty target in targets.EnumerateObject())
        {
            Assert.True(document.RootElement.TryGetProperty(target.Name, out JsonElement measured),
                cardPath + " declares a target for " + target.Name + " but carries no such field.");
            bool met = Satisfies(target.Value, measured);
            lines.Add($"| `{target.Name}` | {Text(target.Value)} | {Text(measured)} | {(met ? "yes" : "**no**")} |");
        }
        return string.Join(LF, lines);
    }

    private static bool Satisfies(JsonElement target, JsonElement measured)
    {
        if (target.ValueKind != JsonValueKind.String) return measured.GetDouble() == target.GetDouble();
        string text = target.GetString() ?? string.Empty;
        var bound = new Regex(@"^\s*(<=|<)\s*(-?[0-9]+(\.[0-9]+)?)\s*$", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Match(text);
        Assert.True(bound.Success, "Cannot interpret the target '" + text + "'.");
        double limit = double.Parse(bound.Groups[2].Value, CultureInfo.InvariantCulture);
        return bound.Groups[1].Value == "<" ? measured.GetDouble() < limit : measured.GetDouble() <= limit;
    }

    // 'R' round-trips a double to the text the JSON held, which is how the PowerShell renderer
    // formats it, so the two can be compared without a tolerance.
    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.TryGetInt64(out long whole)
            ? whole.ToString(CultureInfo.InvariantCulture)
            : value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
    };

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

            // A hidden artifact still counts. This side enumerates with Directory.EnumerateFiles,
            // which includes hidden files, while the script's Get-ChildItem skips them without
            // -Force; the two disagreed on exactly this directory until the script gained it.
            string keep = Path.Combine(root, "fam-hidden-artifact", ".gitkeep");
            Write(Path.Combine(root, "fam-hidden-artifact", "README.md"), "# 42 ms");
            Write(keep, string.Empty);
            if (OperatingSystem.IsWindows()) File.SetAttributes(keep, FileAttributes.Hidden);

            Dictionary<string, EvidenceReadmeClass> classified = ClassifyTree(root);

            Assert.Equal(EvidenceReadmeClass.Generated, classified["fam-generated/README.md"]);
            Assert.Equal(EvidenceReadmeClass.RawAlongside, classified["fam-raw/abc1234/README.md"]);
            Assert.Equal(EvidenceReadmeClass.Unsupported, classified["fam-unsupported/abc1234/README.md"]);
            Assert.Equal(EvidenceReadmeClass.Unsupported, classified["fam-half-marker/README.md"]);
            Assert.Equal(EvidenceReadmeClass.RawAlongside, classified["fam-hidden-artifact/README.md"]);
            Assert.Equal(5, classified.Count);
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
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CensusDirectory(), "census.json")));
        var classified = new Dictionary<string, EvidenceReadmeClass>(StringComparer.Ordinal);
        foreach (JsonElement family in document.RootElement.GetProperty("Families").EnumerateArray())
            foreach (JsonElement readme in family.GetProperty("Readmes").EnumerateArray())
                classified.Add(
                    readme.GetProperty("Path").GetString() ?? throw new InvalidOperationException("Census row without a path."),
                    Enum.Parse<EvidenceReadmeClass>(readme.GetProperty("Class").GetString() ?? string.Empty));
        return classified;
    }

    private static string EvidenceRoot() => Path.Combine(RepositoryRoot(), "benchmarks", "evidence");

    private static string CensusDirectory()
    {
        string[] revisions = Directory.GetDirectories(Path.Combine(EvidenceRoot(), "evidence-regeneration"));
        // Exactly one, the same rule eng/Test-EvidenceTables.ps1 applies to its summaries: two
        // censuses would let a test pick whichever one happened to agree with the tree.
        Assert.Single(revisions);
        return revisions[0];
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "benchmarks", "evidence"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
#endif
