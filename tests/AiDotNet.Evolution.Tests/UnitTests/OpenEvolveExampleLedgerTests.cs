#if NET8_0_OR_GREATER
using System.Text.RegularExpressions;
using AiDotNet.Evolution.Cli;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-56 (#169): every OpenEvolve 0.3.2 example either imports unmodified or has a tracked reason.
/// </summary>
/// <remarks>
/// <para>
/// The ledger in <c>docs/parity/OPENEVOLVE_EXAMPLES.md</c> is the claim, and these tests are what
/// stops it from drifting into a wish. The first two run everywhere: the table must carry one row
/// per example, reach the target, and give a reason for every row that does not import — so the
/// count cannot be improved by deleting an awkward row, and a "no" cannot be left unexplained.
/// </para>
/// <para>
/// The last one re-measures the table against the pinned checkout when
/// <c>EVOLUTION_OPENEVOLVE_CHECKOUT</c> points at one, which is how the ledger was written in the
/// first place. It needs the checkout, so it returns early where there is none; the tests above it
/// assert the row count and the target independently, so that early return cannot hide a shrinking
/// ledger.
/// </para>
/// </remarks>
public sealed class OpenEvolveExampleLedgerTests
{
    private const int ExpectedExamples = 21;
    private const int ImportTarget = 18;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex Row = new(
        @"^\| `(?<example>[^`]+)` \| (?<config>`[^`]+`|none) \| (?<imports>yes|no) \| (?<reason>.+) \|$",
        RegexOptions.None, RegexTimeout);

    /// <summary>The ledger carries one row per example and meets the target it claims.</summary>
    [Fact]
    public void The_ledger_covers_every_example_and_meets_the_target()
    {
        LedgerRow[] rows = Ledger();

        Assert.Equal(ExpectedExamples, rows.Length);
        Assert.Equal(ExpectedExamples, rows.Select(row => row.Example).Distinct(StringComparer.Ordinal).Count());

        int imported = rows.Count(row => row.Imports);
        Assert.True(imported >= ImportTarget,
            $"the ledger reports {imported} of {rows.Length} examples importing, below the target of {ImportTarget}");

        // The headline in the document must be the number the rows actually add up to; a stale
        // headline is the easiest way for a ledger like this to start overstating.
        Assert.Contains(
            $"**{imported} of {rows.Length} import.",
            File.ReadAllText(LedgerPath()),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The ledger's smoke headline matches the number of examples the smoke actually runs.
    /// </summary>
    /// <remarks>
    /// The import headline has been guarded since this file was written; the smoke headline was
    /// not, and it has already been wrong once in this PR's history - it said six while the test
    /// ran six and three of them failed. Tying it to <c>OpenEvolveExampleSmokeTests.Examples</c>
    /// means the document cannot claim coverage the suite does not have.
    /// </remarks>
    [Fact]
    public void The_smoke_headline_matches_the_examples_the_smoke_runs()
    {
        int smoked = OpenEvolveExampleSmokeTests.Examples.Count();
        string ledger = File.ReadAllText(LedgerPath());

        string[] words = { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten" };
        string expected = "**" + (smoked < words.Length ? words[smoked] : smoked.ToString(
            System.Globalization.CultureInfo.InvariantCulture)) + " of " + ExpectedExamples.ToString(
            System.Globalization.CultureInfo.InvariantCulture) + " examples are smoked.**";

        Assert.Contains(expected, ledger, StringComparison.Ordinal);
    }

    /// <summary>Every row that does not import carries a reason, and no reason is a placeholder.</summary>
    [Fact]
    public void Every_example_that_does_not_import_has_a_tracked_reason()
    {
        foreach (LedgerRow row in Ledger().Where(row => !row.Imports))
        {
            Assert.True(row.Reason.Length > 40, row.Example + ": the reason is too short to be one");
            Assert.DoesNotContain("TODO", row.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unknown", row.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("investigate", row.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// With the pinned checkout present, every row is re-measured: the named config exists and the
    /// importer's verdict is the one the ledger claims.
    /// </summary>
    [Fact]
    public void The_ledger_matches_the_pinned_checkout()
    {
        string? checkout = Environment.GetEnvironmentVariable("EVOLUTION_OPENEVOLVE_CHECKOUT");
        if (string.IsNullOrEmpty(checkout))
        {
            // No checkout, nothing to measure against. This returns rather than failing because
            // most jobs have no upstream clone; the upstream-parity job sets the variable, and the
            // two tests above assert the row count and the target wherever this one cannot run, so
            // a quiet return cannot hide a shrinking ledger.
            return;
        }

        string examples = Path.Combine(checkout, "examples");
        Assert.True(Directory.Exists(examples), "the checkout has no examples directory: " + examples);

        // The checkout's own example count is the denominator. If upstream adds an example, this is
        // what fails, rather than the ledger quietly covering 21 of 22.
        string[] present = Directory.EnumerateDirectories(examples)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        LedgerRow[] rows = Ledger();
        Assert.Equal(present, rows.Select(row => row.Example).OrderBy(name => name, StringComparer.Ordinal));

        var failures = new List<string>();
        foreach (LedgerRow row in rows)
        {
            if (row.Config is null)
            {
                string[] configs = Directory
                    .EnumerateFiles(Path.Combine(examples, row.Example), "*.yaml", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(Path.Combine(examples, row.Example), "*.yml", SearchOption.AllDirectories))
                    .ToArray();
                if (configs.Length > 0)
                    failures.Add(row.Example + ": the ledger says it ships no config, but the checkout has " + configs.Length);
                if (row.Imports) failures.Add(row.Example + ": the ledger cannot claim an import with no config");
                continue;
            }

            string config = Path.Combine(checkout, row.Config);
            if (!File.Exists(config))
            {
                failures.Add(row.Example + ": the ledger's config is not in the checkout: " + row.Config);
                continue;
            }

            bool imported;
            string detail;
            try
            {
                OpenEvolveImport import = OpenEvolveConfigImporter.Import(
                    config, Program(checkout, row.Example, config), Evaluator(checkout, row.Example, config),
                    iterations: 3, python: null);
                imported = true;
                detail = import.Notes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " notes";
            }
            catch (InvalidDataException exception)
            {
                imported = false;
                detail = exception.Message.Split('\n')[0];
            }

            if (imported != row.Imports)
            {
                failures.Add(row.Example + ": the ledger says imports=" + (row.Imports ? "yes" : "no")
                    + " but the importer said " + (imported ? "yes" : "no") + " (" + detail + ")");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static string Program(string checkout, string example, string config)
    {
        string[] candidates = Directory
            .EnumerateFiles(Path.Combine(checkout, "examples", example), "init*program*.*", SearchOption.AllDirectories)
            .ToArray();
        return candidates.FirstOrDefault() ?? config;
    }

    private static string Evaluator(string checkout, string example, string config)
    {
        string[] candidates = Directory
            .EnumerateFiles(Path.Combine(checkout, "examples", example), "eval*.py", SearchOption.AllDirectories)
            .ToArray();
        return candidates.FirstOrDefault() ?? config;
    }

    private static LedgerRow[] Ledger()
    {
        var rows = new List<LedgerRow>();
        foreach (string line in File.ReadAllLines(LedgerPath()))
        {
            Match match = Row.Match(line);
            if (!match.Success) continue;

            string config = match.Groups["config"].Value;
            rows.Add(new LedgerRow(
                match.Groups["example"].Value,
                config == "none" ? null : config.Trim('`'),
                match.Groups["imports"].Value == "yes",
                match.Groups["reason"].Value.Trim()));
        }

        return rows.ToArray();
    }

    private static string LedgerPath() =>
        Path.Combine(RepositoryRoot(), "docs", "parity", "OPENEVOLVE_EXAMPLES.md");

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "docs", "parity"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }

    private sealed record LedgerRow(string Example, string? Config, bool Imports, string Reason);
}
#endif
