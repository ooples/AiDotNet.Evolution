extern alias AiDotNetConsumer;
using AiDotNet.Evolution;
using AiDotNet.Evolution.AutoML;
using AiDotNet.Evolution.Deployment;
using AiDotNet.Evolution.Programs;
using AiDotNet.Tensors.LinearAlgebra;

string hash = new('a', 64);
var envelope = new EvolutionDeploymentEnvelope(hash, hash, hash, hash, hash, hash);
var artifact = EvolutionDeployableArtifact.FromProgram(new ProgramGenome("return 1;"), envelope);
if (artifact.ReadProgram().Source != "return 1;") throw new InvalidOperationException("Package program roundtrip failed.");
using var search = new MapElitesAutoML<double, Matrix<double>, Vector<double>>();
if (search.GetType().Assembly.GetName().Name != "AiDotNet.Evolution.Deployment")
    throw new InvalidOperationException("Wrong AutoML owner.");
if (typeof(AiDotNetConsumer::AiDotNet.Regression.MultipleRegression<double>).Assembly == search.GetType().Assembly)
    throw new InvalidOperationException("Consumer primitives must remain separate.");
var taskOptions = new ProgramTaskOptions { Language = ProgramLanguage.Python, EnforceEvolveBlocks = true };
int evaluations = 0;
var fitness = new DelegateProgramFitnessEvaluator(genome =>
{
    evaluations++;
    return genome.Source.Contains("value = 2", StringComparison.Ordinal) ? 2 : 1;
});
var task = new ProgramEvolutionTask(fitness, new ProgramDescriptorSet(new ProgramLengthDescriptor()), taskOptions);
var engine = new EvolutionEngine<ProgramGenome>(task, new FixtureEdit(),
    _ => new MapElitesArchive<ProgramGenome>(new[] { new EvolutionDescriptorDefinition("length", 0, 1000, 1) }),
    new EvolutionEngineOptions { MaxProposals = 2, MaxEvaluationAttempts = 2, MaxGenerations = 1, ProposalBatchSize = 1, MaxDegreeOfParallelism = 1 });
const string seed = "# EVOLVE-BLOCK-START\nvalue = 1\n# EVOLVE-BLOCK-END\n";
var result = await engine.RunAsync(new[] { new ProgramGenome(seed, ProgramLanguage.Python) });
if (evaluations != 2 || result.Best?.Evaluation.Quality != 2)
    throw new InvalidOperationException("Packaged task/edit/descriptor engine integration failed.");
if (typeof(ProgramEvolutionTask).Assembly.GetReferencedAssemblies().Any(reference => reference.Name == "AiDotNet"))
    throw new InvalidOperationException("Program foundation must not depend on AiDotNet.");
var modelRuntime = new LlmProgramVariationOperator(new FixtureChat(),
    new ProgramProposalOptions { Language = ProgramLanguage.Python });
if (modelRuntime.PromptBuilder.Build(new AiDotNet.Evolution.Prompts.ProgramPromptContext(new ProgramGenome("x = 1", ProgramLanguage.Python))).Messages.Count == 0)
    throw new InvalidOperationException("Packaged model runtime produced no prompt.");
var processOptions = new ProgramSandboxOptions { RuntimeVersion = "package-fixture-shell-v1" };
processOptions.SetInterpreter(ProgramLanguage.Python, new ProgramInterpreterSpecification(
    OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/cat",
    OperatingSystem.IsWindows() ? "/c type {source}" : "{source}"));
using var processRunner = new ProcessProgramExecutionEngine(processOptions);
var executionFitness = new SandboxedProgramFitnessEvaluator(processRunner,
    new[] { new ProgramInputOutputExample { ExpectedOutput = "package-execution-proof" } });
var executionResult = await executionFitness.EvaluateAsync(new ProgramGenome("package-execution-proof", ProgramLanguage.Python), new(0, 1, 1, 1));
if (executionResult.Quality != 1 || executionResult.CostUnits != 1)
    throw new InvalidOperationException("Packaged process evaluator failed.");
var scriptFitness = new ScriptProgramFitnessEvaluator(processRunner,
    "{\"metrics\":{\"speed\":4,\"accuracy\":2}}", new ScriptProgramEvaluationOptions { RequireEntryPoint = false },
    metricAggregator: new AiDotNet.Evolution.Programs.Metrics.ProgramMetricAggregator());
var scriptResult = await scriptFitness.EvaluateAsync(new ProgramGenome("candidate", ProgramLanguage.Python), new(0, 1, 1, 1));
if (scriptResult.Quality != 3 || scriptResult.Metrics["speed"] != 4 || scriptResult.CostUnits != 1)
    throw new InvalidOperationException("Packaged script metrics evaluation failed.");
var judge = new LlmJudgeProgramFitnessEvaluator(new FixtureJudge(), new DelegateProgramFitnessEvaluator(_ => .5),
    options: new LlmFeedbackOptions { Criteria = new[] { "score" } });
var judged = await judge.EvaluateAsync(new ProgramGenome("candidate", ProgramLanguage.Python), new(0, 1, 1, 1));
if (Math.Abs(judged.Quality!.Value - .65) > 1e-10 || judged.CostUnits != 1 || judged.Descriptors["llm_average"] != 1)
    throw new InvalidOperationException("Packaged judge evaluation failed.");
Console.WriteLine("PASS: packaged deployment/program runtime, script metrics and judge; no project references or live model calls.");

sealed class FixtureEdit : IVariationOperator<ProgramGenome>
{
    public string Id => "package-fixture-edit";
    public string VersionHash => "v1";
    public ValueTask<ProgramGenome> ProposeAsync(EvolutionVariationContext<ProgramGenome> context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edit = ProgramDiff.Apply(context.Parent.Candidate.CanonicalGenome.Genome.Source, new[] { new ProgramDiffBlock("value = 1", "value = 2") },
            new ProgramTaskOptions { Language = ProgramLanguage.Python, EnforceEvolveBlocks = true });
        if (!edit.IsSuccess) throw new InvalidOperationException("Package edit failed.");
        return new(new ProgramGenome(edit.ModifiedSource, ProgramLanguage.Python));
    }
}

sealed class FixtureChat : IProgramChatClient
{
    public string ModelId => "package-fixture";
    public Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages,
        ProgramChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProgramChatResponse(ProgramChatMessage.Assistant("fixture")));
}

sealed class FixtureJudge : IProgramChatClient
{
    public string ModelId => "package-judge-fixture";
    public Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages,
        ProgramChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProgramChatResponse(ProgramChatMessage.Assistant("{\"score\":1}")));
}
