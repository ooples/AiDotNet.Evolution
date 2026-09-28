using System;
using System.Collections.Generic;
using System.IO;
using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Prompts;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

/// <summary>
/// Each prompt option is set both ways against the same context, and the rendered prompt must differ in the
/// way the option promises. A test that only constructs the option would pass with the option ignored.
/// </summary>
public sealed class PromptOptionBehaviourTests
{
    private const string ParentSource = "def solve(n):\n    return sum(range(n))\n";
    private const string InspirationBody = "return sum(i for i in range(n))";
    private const string InspirationChanges = "switched to a generator";

    [Fact]
    public void IncludeInspirations_controls_whether_the_inspiration_program_is_shown()
    {
        Assert.Contains(InspirationBody, User(new ProgramEvolutionPromptOptions()));
        Assert.DoesNotContain(InspirationBody, User(new ProgramEvolutionPromptOptions { IncludeInspirations = false }));
    }

    [Fact]
    public void IncludePreviousAttempts_and_NumPreviousAttempts_bound_the_attempt_history()
    {
        Assert.Contains("alpha change", User(new ProgramEvolutionPromptOptions()));
        Assert.Contains("beta change", User(new ProgramEvolutionPromptOptions()));

        string one = User(new ProgramEvolutionPromptOptions { NumPreviousAttempts = 1 });
        Assert.True(one.Contains("alpha change") ^ one.Contains("beta change"), one);

        string none = User(new ProgramEvolutionPromptOptions { NumPreviousAttempts = 0 });
        Assert.DoesNotContain("alpha change", none);
        Assert.DoesNotContain("beta change", none);

        string off = User(new ProgramEvolutionPromptOptions { IncludePreviousAttempts = false });
        Assert.DoesNotContain("alpha change", off);
        Assert.DoesNotContain("beta change", off);
    }

    [Fact]
    public void FitnessStableBand_decides_whether_a_change_reads_as_unchanged()
    {
        // 0.5 -> 0.625 is a change of 0.125: outside the default band, inside a band of 0.2.
        string strict = User(new ProgramEvolutionPromptOptions());
        Assert.Contains("Fitness improved:", strict);
        Assert.DoesNotContain("Fitness unchanged", strict);

        string loose = User(new ProgramEvolutionPromptOptions { FitnessStableBand = 0.2 });
        Assert.Contains("Fitness unchanged at", loose);
        Assert.DoesNotContain("Fitness improved:", loose);
    }

    [Fact]
    public void ScoreDecimals_sets_the_precision_scores_are_rendered_at()
    {
        Assert.Contains("Fitness improved: 0.5000 -> 0.6250", User(new ProgramEvolutionPromptOptions()));

        string coarse = User(new ProgramEvolutionPromptOptions { ScoreDecimals = 1 });
        Assert.Contains("Fitness improved: 0.5 -> 0.6", coarse);
        Assert.DoesNotContain("0.6250", coarse);
    }

    [Fact]
    public void SuggestSimplificationAfterChars_suggests_simplifying_only_past_the_threshold()
    {
        const string Suggestion = "Consider simplifying";
        Assert.True(ParentSource.Length < 500);
        Assert.DoesNotContain(Suggestion, User(new ProgramEvolutionPromptOptions()));
        Assert.DoesNotContain(Suggestion, User(new ProgramEvolutionPromptOptions { SuggestSimplificationAfterChars = null }));
        Assert.Contains(
            "longer than 10 characters",
            User(new ProgramEvolutionPromptOptions { SuggestSimplificationAfterChars = 10 }));
    }

    [Fact]
    public void IncludeChangesUnderChars_quotes_only_short_change_descriptions()
    {
        Assert.Contains(InspirationChanges, User(new ProgramEvolutionPromptOptions()));
        Assert.DoesNotContain(InspirationChanges, User(new ProgramEvolutionPromptOptions { IncludeChangesUnderChars = 5 }));
        Assert.DoesNotContain(InspirationChanges, User(new ProgramEvolutionPromptOptions { IncludeChangesUnderChars = null }));
    }

    [Fact]
    public void ArtifactSecurityFilter_redacts_secrets_in_artifacts_unless_turned_off()
    {
        const string Secret = "password=hunter2hunter2";
        ProgramPromptContext context = Context();
        context.Artifacts = new List<ProgramPromptArtifact> { new("stdout", "connecting with " + Secret) };

        string filtered = User(new ProgramEvolutionPromptOptions(), context);
        Assert.Contains("connecting with", filtered);
        Assert.DoesNotContain("hunter2hunter2", filtered);

        string raw = User(new ProgramEvolutionPromptOptions { ArtifactSecurityFilter = false }, context);
        Assert.Contains(Secret, raw);
    }

    [Fact]
    public void IncludeDiagnostics_controls_whether_evaluation_diagnostics_are_shown()
    {
        ProgramPromptContext context = Context();
        context.Diagnostics = new List<EvolutionDiagnostic> { new("CS1002", "semicolon expected") };

        Assert.Contains("CS1002", User(new ProgramEvolutionPromptOptions(), context));
        Assert.DoesNotContain("CS1002", User(new ProgramEvolutionPromptOptions { IncludeDiagnostics = false }, context));
    }

    [Fact]
    public void FragmentOverrides_replace_a_single_phrase()
    {
        var options = new ProgramEvolutionPromptOptions
        {
            FragmentOverrides = new Dictionary<ProgramPromptFragmentKey, string>
            {
                [ProgramPromptFragmentKey.FitnessImproved] = "UP {previous} => {current}"
            }
        };

        string user = User(options);
        Assert.Contains("UP 0.5000 => 0.6250", user);
        Assert.DoesNotContain("Fitness improved:", user);
    }

    [Fact]
    public void TemplateDirectory_layers_its_fragments_file_over_the_defaults()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aidotnet-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, ProgramPromptTemplateSet.FragmentsFileName),
                "{\"fitness_improved\": \"FROM DIRECTORY {previous} ~ {current}\"}");

            string user = User(new ProgramEvolutionPromptOptions { TemplateDirectory = directory });
            Assert.Contains("FROM DIRECTORY 0.5000 ~ 0.6250", user);
            Assert.DoesNotContain("FROM DIRECTORY", User(new ProgramEvolutionPromptOptions()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string User(ProgramEvolutionPromptOptions options, ProgramPromptContext? context = null) =>
        new ProgramPromptBuilder(options).Build(context ?? Context(), new StableRandom(7UL)).UserText;

    private static ProgramPromptContext Context() =>
        new(new ProgramGenome(ParentSource, ProgramLanguage.Python))
        {
            ParentQuality = 0.625,
            PreviousQuality = 0.5,
            ParentMetrics = new Dictionary<string, double> { ["accuracy"] = 0.625 },
            Inspirations = new List<ProgramPromptExample>
            {
                new(new ProgramGenome("def solve(n):\n    " + InspirationBody + "\n"),
                    ProgramPromptExampleKind.Migrant,
                    0.55,
                    new Dictionary<string, double> { ["complexity"] = 0.95 },
                    InspirationChanges)
            },
            PreviousAttempts = new List<ProgramPromptAttempt>
            {
                new(1, "alpha change",
                    new Dictionary<string, double> { ["accuracy"] = 0.5 },
                    new Dictionary<string, double> { ["accuracy"] = 0.4 }),
                new(2, "beta change",
                    new Dictionary<string, double> { ["accuracy"] = 0.625 },
                    new Dictionary<string, double> { ["accuracy"] = 0.5 })
            }
        };
}
