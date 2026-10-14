using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.MetaPromptAblation;

/// <summary>What one arm of one seed produced.</summary>
internal sealed record ArmResult(
    double? BestVisibleQuality,
    double HeldOutAccuracy,
    long CompletedEvaluations,
    int InstructionProposals,
    int CreditedOutcomes,
    decimal MetaCostUnits,
    double Seconds,
    string? Failure,
    string? EndpointDetail)
{
    internal object ToRow(int seed, MetaPromptArm arm) => new
    {
        Seed = seed,
        Arm = arm.ToString(),
        BestVisibleQuality,
        HeldOutAccuracy,
        CompletedEvaluations,
        InstructionProposals,
        CreditedOutcomes,
        MetaCostUnits,
        Seconds = Math.Round(Seconds, 2),
        Failure,
        EndpointDetail
    };
}

/// <summary>Runs one arm: the engine, then the held-out endpoint scored outside it.</summary>
internal static class Arm
{
    /// <summary>The instruction both arms start from; only the evolving arm may replace it.</summary>
    private const string SeedInstruction =
        "You are an expert Python programmer. Improve the program so that it matches the examples the " +
        "evaluator reports, and reply with the complete program in one fenced block.";

    internal static async Task<ArmResult> RunAsync(MetaPromptArm arm, int seed, int evaluations,
        string evaluatorSource, string seedProgram, string scorerPath, string python, string claude, string model)
    {
        var clock = Stopwatch.StartNew();
        string workspace = Path.Combine(Path.GetTempPath(), "aidotnet-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var sandbox = new ProgramSandboxOptions { RuntimeVersion = "meta-prompt-ablation-v1" };
            sandbox.Limits.TimeLimitSeconds = 60;
            sandbox.Limits.MaxConcurrentExecutions = 1;
            sandbox.SetInterpreter(ProgramLanguage.Python, new ProgramInterpreterSpecification(python, "{source}"));
            using var execution = new ProcessProgramExecutionEngine(sandbox);

            var fitness = new ScriptProgramFitnessEvaluator(execution, evaluatorSource,
                new ScriptProgramEvaluationOptions
                {
                    EvaluatorScriptLanguage = ProgramLanguage.Python,
                    Direction = EvolutionOptimizationDirection.Maximize,
                    RetainArtifactText = true
                });

            var programOptions = new ProgramProposalOptions
            {
                Language = ProgramLanguage.Python,
                TaskDescription = "Implement solve(x) for integers x. The evaluator shows only a few example "
                    + "pairs; the program will be used on inputs well outside the range it is shown.",
                MaxProgramChars = 8_000
            };
            programOptions.Prompt.IncludeArtifacts = true;

            var clientOptions = new ClaudeCodeChatClientOptions { Executable = claude, Model = model, Isolate = true };
            var proposer = new ClaudeCodeChatClient(clientOptions);

            ProgramMetaPromptEvolver? evolver = null;
            MetaPromptCredit? credit = null;
            var variationOptions = new LlmProgramVariationOptions
            {
                Mode = ProgramEvolutionMode.FullRewrite,
                MaxProposalRetries = 1,
                Seed = seed
            };
            if (arm == MetaPromptArm.Evolving)
            {
                evolver = new ProgramMetaPromptEvolver(new ClaudeCodeChatClient(clientOptions), SeedInstruction,
                    new ProgramMetaPromptOptions { Enabled = true, ProposeEveryGenerations = 2, MaxArchiveSize = 4 });
                credit = new MetaPromptCredit(evolver, null);
                variationOptions.InstructionSource = credit;
            }
            else
            {
                programOptions.Prompt.SystemMessage = SeedInstruction;
                programOptions.Prompt.SystemMessageMode = ProgramPromptSystemMessageMode.Literal;
            }

            var variation = new LlmProgramVariationOperator(proposer, programOptions, variationOptions);
            var task = new ProgramEvolutionTask(fitness,
                new ProgramDescriptorSet(new[] { new ProgramLengthDescriptor() }), programOptions);
            var descriptors = new[] { new EvolutionDescriptorDefinition("length", 0, programOptions.MaxProgramChars, 64) };
            var options = new EvolutionEngineOptions
            {
                RunId = "meta-prompt-" + arm.ToString().ToLowerInvariant() + "-s" + seed.ToString(CultureInfo.InvariantCulture),
                Seed = (ulong)seed,
                MaxEvaluationAttempts = evaluations,
                MaxProposals = evaluations,
                MaxGenerations = evaluations,
                MaxDegreeOfParallelism = 1,
                ProposalBatchSize = 1,
                IslandCount = 1
            };

            var engine = new EvolutionEngine<ProgramGenome>(task, variation,
                _ => new MapElitesArchive<ProgramGenome>(descriptors), options, observer: credit);
            EvolutionRunResult<ProgramGenome> result = await engine.RunAsync(
                new[] { new ProgramGenome(seedProgram, ProgramLanguage.Python) }).ConfigureAwait(false);

            string best = result.Best?.Candidate.CanonicalGenome.Genome.Source ?? seedProgram;
            (double endpoint, string? detail) = Endpoint(best, scorerPath, python, workspace);
            return new ArmResult(result.Best?.Evaluation.Quality, endpoint, result.Counters.CompletedEvaluations,
                credit?.Proposals ?? 0, credit?.Credited ?? 0, evolver?.ProposalCostUnits ?? 0m,
                clock.Elapsed.TotalSeconds, null, detail);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            // Returned rather than thrown: one arm that cannot start must not discard every row already paid for.
            return new ArmResult(null, 0.0, 0, 0, 0, 0m, clock.Elapsed.TotalSeconds,
                exception.GetType().Name + ": " + exception.Message, null);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Accuracy on inputs the evaluator never holds, scored in a separate process.</summary>
    private static (double Accuracy, string? Detail) Endpoint(string program, string scorerPath, string python, string workspace)
    {
        string candidate = Path.Combine(workspace, "best.py");
        File.WriteAllText(candidate, program);
        using var process = Process.Start(new ProcessStartInfo(python)
        {
            ArgumentList = { scorerPath, candidate },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new IOException("the held-out scorer did not start");
        // Both pipes are drained concurrently, and that is load-bearing rather than tidy. Reading stdout to
        // the end while stderr is redirected and never read lets a candidate that writes more than the
        // stderr pipe buffer block on its own write - and the block happens HERE, before WaitForExit is
        // reached, so the 120 s bound never applies and the campaign hangs instead of recording a timeout.
        // Measured: a candidate writing 200 KB to stderr never exits and the stdout read never returns.
        Task<string> pendingOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> pendingError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            return (0.0, "the scorer did not finish within 120 s");
        }

        // The no-argument overload after a successful timed wait: it is what guarantees the redirected
        // streams have been drained before the text is read.
        process.WaitForExit();
        string output = pendingOutput.GetAwaiter().GetResult();
        pendingError.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
            return (0.0, "the scorer exited " + process.ExitCode.ToString(CultureInfo.InvariantCulture));

        // The last line: the scorer prints one JSON object last, and a candidate that printed during import
        // would otherwise make this unparsable after the arm had already been paid for.
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return (0.0, "the scorer printed nothing");
        using JsonDocument parsed = JsonDocument.Parse(lines[^1]);
        return (parsed.RootElement.GetProperty("heldout_accuracy").GetDouble(), null);
    }
}
