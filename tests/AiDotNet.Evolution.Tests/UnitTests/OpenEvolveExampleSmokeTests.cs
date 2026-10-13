#if NET8_0_OR_GREATER
using System.Text.Json;
using AiDotNet.Evolution.Cli;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-56 (#169): a real OpenEvolve example's own evaluator and initial program, run end to end
/// through the CLI for three iterations.
/// </summary>
/// <remarks>
/// <para>
/// The ledger in <c>docs/parity/OPENEVOLVE_EXAMPLES.md</c> measures whether each example's config
/// <i>imports</i>. This measures the other half of the criterion for one example: that the files
/// upstream ships actually drive a search here.
/// </para>
/// <para>
/// <c>circle_packing</c> is the example chosen, and the reason is worth stating: its evaluator
/// imports only numpy and pickle, so it does not need the <c>openevolve</c> package the way
/// <c>function_minimization</c> does (<c>from openevolve.evaluation_result import EvaluationResult</c>),
/// and it needs neither a GPU, Apple MLX, a dataset download nor network access. That makes it the
/// one example whose smoke can be verified off a CI runner as well as on one.
/// </para>
/// <para>
/// <b>What is modified, and what is not.</b> The evaluator and the initial program are used exactly
/// as upstream ships them. Three keys in a <i>copy</i> of the config are changed, and all three are
/// about not spending money or reaching the network: <c>llm.api_base</c> points at a loopback stub,
/// the model names become that stub's, and <c>diff_based_evolution</c> is false so the stub's whole
/// program answer is usable. <c>benchmarks/external/test_openevolve_configs.py</c> treats an
/// endpoint rewrite the same way, exempting <c>llm.api_base/api_key</c> from its verbatim check.
/// </para>
/// <para>
/// The bar is that the run completes and counts evaluations. It is not that quality improves: the
/// stub answers with a fixed program rather than a good one, and a smoke that demanded improvement
/// would be measuring the stub.
/// </para>
/// </remarks>
public sealed class OpenEvolveExampleSmokeTests
{
    private const string Example = "circle_packing";

    private static readonly string Python = Environment.GetEnvironmentVariable("EVOLUTION_PYTHON")
        ?? (OperatingSystem.IsWindows() ? "python" : "python3");

    [Fact]
    public void A_real_examples_evaluator_and_program_run_three_iterations()
    {
        string? checkout = Environment.GetEnvironmentVariable("EVOLUTION_OPENEVOLVE_CHECKOUT");
        if (string.IsNullOrEmpty(checkout))
        {
            // No checkout, nothing to run. OpenEvolveExampleLedgerTests asserts the ledger's counts
            // wherever this cannot run, so an early return here cannot hide a lost claim.
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // Upstream's evaluator cannot run here, and the reason is upstream's: it writes a
            // wrapper script that embeds the program's absolute path into a single-quoted Python
            // literal, so a Windows path becomes an invalid escape -
            // "SyntaxError: (unicode error) 'unicodeescape' codec can't decode bytes ... truncated
            // \UXXXXXXXX escape" - and every candidate scores zero. Measured directly against
            // examples/circle_packing/evaluator.py, and the same shape defeats
            // online_judge_programming, k_module_problem and web_scraper_optillm. The smoke
            // therefore runs on the Linux job that owns the checkout.
            return;
        }

        string source = Path.Combine(checkout, "examples", Example);
        string evaluator = Path.Combine(source, "evaluator.py");
        string program = Path.Combine(source, "initial_program.py");
        string config = Path.Combine(source, "config_phase_1.yaml");
        Assert.True(File.Exists(evaluator), "the example's evaluator is missing: " + evaluator);
        Assert.True(File.Exists(program), "the example's initial program is missing: " + program);
        Assert.True(File.Exists(config), "the example's config is missing: " + config);

        using var model = new CliRunTests.FakeChatModel();
        using var directory = new TemporaryDirectory();
        string rewritten = Path.Combine(directory.Path, "config.yaml");
        File.WriteAllText(rewritten, Redirect(File.ReadAllText(config), model.Endpoint));

        string? previousKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "unused-by-the-stub");
        try
        {
            string output = Path.Combine(directory.Path, "out");
            (int Code, string Output, string Error) run = CliRunTests.Run(
                "run", "--openevolve-config", rewritten, program, evaluator,
                "--iterations", "3", "--output", output, "--python", Python);

            Assert.True(run.Code == 0, run.Error);
            using JsonDocument result = JsonDocument.Parse(run.Output);

            // Four evaluations: the seed plus three iterations. The run having reached the end is the
            // claim; what the stub proposed is not.
            Assert.Equal(4, result.RootElement.GetProperty("CompletedEvaluations").GetInt64());
            Assert.True(model.Calls >= 1, "the run never asked the model for a proposal");

            // The instrument check. Four evaluations would also be counted if the example's
            // evaluator had failed on every candidate, because it returns combined_score 0.0 on
            // error - so the seed's own score has to be positive for this to be evidence that
            // upstream's evaluator really ran upstream's program.
            double best = result.RootElement.GetProperty("BestQuality").GetDouble();
            Assert.True(best > 0.0,
                "every candidate scored zero, so the example's evaluator did not actually run: " + run.Output);
            Assert.True(File.Exists(Path.Combine(output, "run.json")));
            Assert.True(File.Exists(Path.Combine(output, "openevolve-import.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousKey);
        }
    }

    /// <summary>
    /// Rewrites only the three keys named in the class remarks, leaving every other line of the
    /// example's config as upstream wrote it.
    /// </summary>
    private static string Redirect(string yaml, string endpoint)
    {
        var rewritten = new List<string>();
        foreach (string line in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("api_base:", StringComparison.Ordinal))
            {
                rewritten.Add("  api_base: \"" + endpoint + "\"");
                continue;
            }

            if (trimmed.StartsWith("primary_model:", StringComparison.Ordinal))
            {
                rewritten.Add("  primary_model: \"fake-model\"");
                continue;
            }

            if (trimmed.StartsWith("secondary_model:", StringComparison.Ordinal))
            {
                rewritten.Add("  secondary_model: \"fake-model\"");
                continue;
            }

            if (trimmed.StartsWith("diff_based_evolution:", StringComparison.Ordinal)) continue;

            rewritten.Add(line);
        }

        rewritten.Add("diff_based_evolution: false");
        return string.Join("\n", rewritten);
    }
}
#endif
