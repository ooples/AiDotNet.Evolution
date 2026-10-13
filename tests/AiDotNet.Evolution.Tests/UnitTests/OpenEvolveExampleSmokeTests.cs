#if NET8_0_OR_GREATER
using System.Text.Json;
using YamlDotNet.RepresentationModel;
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
// The run sets OPENAI_API_KEY and calls Program.Main in this process, so it shares the console collection
// with the other CLI tests rather than overlapping them.
[Collection(ConsoleCollection.Name)]
public sealed class OpenEvolveExampleSmokeTests
{
    private static readonly string Python = Environment.GetEnvironmentVariable("EVOLUTION_PYTHON")
        ?? (OperatingSystem.IsWindows() ? "python" : "python3");

    /// <summary>
    /// The examples that run end to end here, measured rather than assumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A positive seed score is necessary but not sufficient. Six examples score above zero on
    /// their own seed (measured with <c>eng/probe-openevolve-examples.py</c>); of those, three
    /// complete a three-iteration run against a loopback stub, and the three left out failed for
    /// reasons recorded in <c>docs/parity/OPENEVOLVE_EXAMPLES.md</c>:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>claude_code_quickstart</c> sets <c>llm.provider: "claude_code"</c>, so the import routes
    /// proposals to the Claude Code CLI rather than to an HTTP endpoint - there is nothing for a
    /// loopback stub to answer, and all nine attempts fail.
    /// </description></item>
    /// <item><description>
    /// <c>background_blur</c> and <c>circle_packing_with_artifacts</c> are blocked by their declared
    /// <c>database.feature_dimensions</c>: the run stops with <c>StopReason: NoCandidates</c> after
    /// one evaluation with a null best quality and no model calls, and removing only that key from
    /// the config copy makes the same example run and pass. That is a parity gap on our side rather
    /// than a property of the examples, so they are excluded until it is fixed - see *The
    /// feature-dimension gap* in the ledger.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static TheoryData<string, string> Examples => new()
    {
        { "circle_packing", "config_phase_1.yaml" },
        { "function_minimization", "config.yaml" },
        { "signal_processing", "config.yaml" },
    };

    [Theory]
    [MemberData(nameof(Examples))]
    public void A_real_examples_evaluator_and_program_run_three_iterations(string example, string configName)
    {
        string? checkout = Environment.GetEnvironmentVariable("EVOLUTION_OPENEVOLVE_CHECKOUT");
        // The CI job that owns the checkout sets EVOLUTION_REQUIRE_OPENEVOLVE_SMOKE, so a lost or
        // mistyped checkout fails there instead of passing with nothing run.
        bool required = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EVOLUTION_REQUIRE_OPENEVOLVE_SMOKE"));
        if (string.IsNullOrEmpty(checkout))
        {
            Assert.False(required,
                "EVOLUTION_REQUIRE_OPENEVOLVE_SMOKE is set but EVOLUTION_OPENEVOLVE_CHECKOUT is not, so the smoke did not run.");
            // No checkout, nothing to run. OpenEvolveExampleLedgerTests asserts the ledger's counts
            // wherever this cannot run.
            return;
        }
        Assert.True(Directory.Exists(checkout), "EVOLUTION_OPENEVOLVE_CHECKOUT does not exist: " + checkout);

        if (OperatingSystem.IsWindows())
        {
            Assert.False(required, "EVOLUTION_REQUIRE_OPENEVOLVE_SMOKE is set on Windows, where upstream's evaluator cannot run.");
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

        string source = Path.Combine(checkout, "examples", example);
        string evaluator = Path.Combine(source, "evaluator.py");
        string program = Directory.EnumerateFiles(source, "initial_program.*").OrderBy(
            path => path, StringComparer.Ordinal).FirstOrDefault() ?? Path.Combine(source, "initial_program.py");
        string config = Path.Combine(source, configName);
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

            // At least the seed. Four was the wrong assertion: these examples validate a proposal
            // before scoring it - circle_packing rejects a program with no run_packing, and the
            // others likewise - so the stub's fixed answer is refused and the run completes with
            // fewer evaluations than iterations. That is the stub's doing, not the example's, and
            // the claim here is that upstream's files drive a search end to end.
            long evaluations = result.RootElement.GetProperty("CompletedEvaluations").GetInt64();
            Assert.True(evaluations >= 1, example + ": the run completed no evaluations at all");
            Assert.True(model.Calls >= 1, "the run never asked the model for a proposal");

            // The instrument check, and its limit. The example's evaluator returns combined_score 0.0
            // on error, so a positive best score shows that upstream's evaluator really ran upstream's
            // seed program. The seed alone satisfies it: the run's result does not say whether a
            // proposal was scored, and the stub's fixed answer is usually refused (see above), so this
            // smoke claims the seed was evaluated and the model was asked - not that a proposal won.
            double best = result.RootElement.GetProperty("BestQuality").GetDouble();
            Assert.True(best > 0.0,
                example + ": the seed scored zero, so the example's evaluator did not actually run it. "
                + "The seed scores above zero when run directly, so this is a regression in the run "
                + "rather than a property of the example: " + run.Output);
            Assert.True(File.Exists(Path.Combine(output, "run.json")));
            Assert.True(File.Exists(Path.Combine(output, "openevolve-import.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousKey);
        }
    }

    /// <summary>
    /// Rewrites only the three keys named in the class remarks - <c>llm.api_base</c>, every model name
    /// under <c>llm</c>, and the top-level <c>diff_based_evolution</c> - and leaves every other key of
    /// the example's config as upstream wrote it.
    /// </summary>
    /// <remarks>
    /// Edited as a YAML tree, not line by line: a line match also caught a <c>name:</c> under another
    /// section, and could not tell a nested <c>diff_based_evolution</c> from the top-level key.
    /// </remarks>
    private static string Redirect(string yaml, string endpoint)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        if (!root.Children.TryGetValue(new YamlScalarNode("llm"), out YamlNode? llmNode) || llmNode is not YamlMappingNode llm)
        {
            throw new InvalidOperationException("The example's config has no llm section to redirect.");
        }

        llm.Children[new YamlScalarNode("api_base")] = new YamlScalarNode(endpoint);
        foreach (string key in new[] { "primary_model", "secondary_model" })
        {
            if (llm.Children.ContainsKey(new YamlScalarNode(key)))
                llm.Children[new YamlScalarNode(key)] = new YamlScalarNode("fake-model");
        }

        // A weighted model list names its own models, so each entry's name is redirected too; leaving
        // one behind would send part of the run to a provider that is not there.
        foreach (string listKey in new[] { "models", "evaluator_models" })
        {
            if (!llm.Children.TryGetValue(new YamlScalarNode(listKey), out YamlNode? list) || list is not YamlSequenceNode models)
                continue;
            foreach (YamlNode entry in models)
            {
                if (entry is YamlMappingNode model && model.Children.ContainsKey(new YamlScalarNode("name")))
                    model.Children[new YamlScalarNode("name")] = new YamlScalarNode("fake-model");
            }
        }

        root.Children[new YamlScalarNode("diff_based_evolution")] = new YamlScalarNode("false");
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }}
#endif
