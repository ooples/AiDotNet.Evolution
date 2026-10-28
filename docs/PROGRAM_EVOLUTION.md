# Evolving programs

`AiDotNet.Evolution.Programs` evolves source code: a model proposes edits, a sandbox runs the
candidates, and the engine keeps the best of each behaviour. This guide walks through the pieces in
the order a run uses them. For C# programs improved by a compiler rather than a runtime, see
[Compiler-guided programs](COMPILER_GUIDED_PROGRAMS.md).

## The moving parts

A program search plugs three interfaces into the engine:

- `IProgramVariationOperator` proposes a child from a parent, usually by asking a model. It reports
  its model usage through `GetUsage()` as a `ProgramEvolutionLlmUsage`: proposals, chat calls,
  retries, abandoned proposals, provider errors and token counts. `LlmProgramVariationOperator` is
  the shipped implementation, configured by `LlmProgramVariationOptions`. Those options cover the
  `ProgramEvolutionMode` (`Diff` or `FullRewrite`), retries and samples per attempt, inspirations,
  the prompt's program length, the system message and sampling (temperature, top-p, output tokens,
  and a `ProgramReasoningEffort` of `Low`, `Medium` or `High` for models that support it).
- `IProgramFitnessEvaluator` scores a candidate and returns an evolution task result.
  `DelegateProgramFitnessEvaluator` wraps your own function as one.
- `IProgramDescriptor` computes one named, finite behaviour descriptor from a program's text, used as
  an archive coordinate. `IVersionedProgramDescriptor` also publishes a hash of its configuration,
  so changing a descriptor's settings changes the run's identity. `IRebasableProgramDescriptor`
  measures against reference programs and can be pointed at a new set with `Rebase`.
  Three descriptors ship. `ProgramLengthDescriptor` measures the normalised source length.
  `ProgramTokenComplexityDescriptor` counts lexical tokens. `ProgramDiversityDescriptor` measures
  distance from a reference set of programs.

`ProgramGenomeCodec` serialises `ProgramGenome` values for portable checkpoints.

## Talking to a model

A proposal is a conversation of `ProgramChatMessage` values, each with a `ProgramChatRole`
(`System`, `User` or `Assistant`). `ProgramChatOptions.ResponseFormat` (`ProgramChatResponseFormat`:
`Text` or `Json`) requests a format, which the provider may or may not guarantee. The reply is a
`ProgramChatResponse`, with the text and, when the provider reports it, a `ProgramChatUsage` of token
counts. Usage is what the provider said, not a charge or a budget admission.

Four clients ship:

- `ClaudeCodeChatClient` uses the Claude Code CLI (`claude -p`) and its login session, configured by
  `ClaudeCodeChatClientOptions`.
- `ManualProgramChatClient` writes each prompt to a queue directory for a person to answer, which is
  useful for inspecting prompts or evolving by hand.
- `WeightedEnsembleChatClient` sends each request to one member of a weighted set of
  `WeightedChatModel`s, like OpenEvolve's `llm.models`. `GetMemberStatistics` reports calls and
  failures per member.
- `OpenAiCompatibleChatClient` talks to any OpenAI-compatible endpoint. `OpenAiCompatibleChatClientOptions`
mirror OpenEvolve's per-model settings: the endpoint, model and API key, plus the request timeout,
retry count and delay, and a response size limit.

The variation operator records each request-and-answer round as a `ProgramProposalAttempt`, available
through `GetRecentAttempts()`. Its `ProgramProposalOutcome` says what happened: `Accepted`,
`EmptyResponse`, `ParseFailed`, `Unchanged`, `TooLong`, `ProviderError` or `Exhausted`.

## Building prompts

`ProgramPromptBuilder` turns a `ProgramPromptContext` (the parent, its scores, earlier attempts,
examples and evidence) into a `ProgramPromptResult`: the system and user text plus a record of how
they were assembled.

- Other programs appear as `ProgramPromptExample` values. `ProgramPromptExampleKind` says why each
  one was chosen (`TopProgram`, `Diverse`, `Inspiration`, `Migrant` or `Random`), which decides its
  label.
- Earlier attempts appear as `ProgramPromptAttempt` summaries: what changed, what was measured, and
  how it compared.
- Evaluation output appears as `ProgramPromptArtifact` values. It is untrusted, so it is redacted
  and bounded first.
- `ProgramPromptEvolutionMode` chooses what is asked for: `Diff` edits, a `FullRewrite`, or
  `AutoBySize`, which decides from the parent's length.

Every text is a `ProgramPromptTemplate` with named `{placeholder}` slots, and a run's texts form a
validated `ProgramPromptTemplateSet`. `ProgramPromptTemplateKey` names the whole templates and
`ProgramPromptFragmentKey` the short phrases they are built from. Both can be overridden in
`ProgramEvolutionPromptOptions`, per key or from a folder of files. `ProgramPromptSystemMessageMode`
says whether a configured system message names a template (`TemplateKey`) or is literal text
(`Literal`).

`PromptTextRedactor` strips terminal control sequences and credential-shaped text before anything
reaches a prompt or a log.

## Reading the model's answer

In diff mode, the answer is a set of SEARCH/REPLACE blocks. `ProgramDiffOptions` sets the three
markers, whether carriage returns and near-miss whitespace are accepted, whether a reply that changes
nothing is rejected, and the block limit. `ProgramDiff.Parse` returns a
`ProgramDiffParseResult`: the `ProgramDiffBlock` edits it recovered, plus a `ProgramDiffFailure` for
each block it rejected. Each failure has a `ProgramDiffFailureReason`, for example `SearchTextNotFound`,
`OutsideEvolveBlock`, `AmbiguousTarget` or `ResultUnchanged`. `ProgramDiff.SplitByTarget` returns a
`ProgramDiffTargetSplit`, which routes each block to the program or to the changes description when
a reply edits both.

Editable regions are marked with EVOLVE-BLOCK comments. `EvolveBlock.Extract` returns an
`EvolveBlockExtractionResult`: an `EvolveBlockRegion` per block (the text before, the body and the
text after), plus an `EvolveBlockStatus` (`NotPresent`, `Complete`, `UnmatchedEnd`,
`RestartedBlock` or `Unterminated`). `EvolveBlockMarkers` holds a language's start and end markers.

In rewrite mode, `FencedCodeExtractor.Extract` pulls the program out of fenced code blocks and returns
a `FencedCodeExtractionResult`. It keeps every `FencedCodeBlock` it considered, and a
`FencedCodeSelectionSource` recording which fallback supplied the code: `LanguageLabeledFence`,
`UnlabeledFence`, `OtherLabeledFence`, `RawResponse` or `None`.

## Running candidates

`IProgramExecutionEngine` is the execution boundary, and `ProgramSandboxMode` names the one a
configuration asks for. Every engine applies the limits in `ProgramSandboxLimitOptions`: wall clock,
memory, CPU, source and output sizes, and concurrency. An engine refuses options naming a mode it does
not implement, so a request is never silently weakened.

`ContainerExecutionEngine` (`ProgramSandboxMode.Container`) runs each candidate inside a Docker
container configured by `ContainerExecutionOptions`: no network, a read-only root with an in-memory
`/work` as the only writable path, a non-root user, no capabilities, `no-new-privileges`, the default
seccomp profile, and memory, CPU and process caps. It checks all of that before using a container, from
`docker inspect` and from inside the container. `ContainerLifecycle.Warm` reuses a container: a
supervisor inside it runs each candidate as a fresh process, then kills every process it left and
empties `/work` before the next one. `ContainerLifecycle.Cold` starts and removes a container per
candidate. `ContainerPythonLaunch` chooses whether a Python candidate is forked from the supervisor
(the default, no interpreter start) or starts a fresh interpreter.

`ProcessProgramExecutionEngine` (`OutOfProcessWorker`) runs each candidate in a separate host process.
`WarmPythonExecutionEngine` removes most of the per-candidate process start for Python.
`ProgramSandboxMode.WarmForkWorker` (Linux and macOS) keeps one interpreter warm and forks a fresh child
for each candidate, so every candidate is still a separate process with its own memory and CPU-time
limits, reported as `MemoryLimitExceeded` and `CpuTimeLimitExceeded` like the process runner.
`ProgramSandboxMode.WarmReusedWorker` (any OS) runs candidates one after another in one interpreter, each
in a fresh namespace. They share a process, so it requires `AllowUnsafeInProcessExecution`, and the
worker is replaced after a timeout, after a failure and every `RecycleAfter` candidates. The
`sandbox-overhead` workflow measures what each mode adds per evaluation.

### Choosing an execution mode

| Mode | Engine | Isolates the host's files, network and system calls | Opt-in | Added latency (Linux p50) |
|---|---|---|---|---|
| `Container`, warm | `ContainerExecutionEngine` | Yes | None | 25.35 ms (target 30 ms; one run, see `benchmarks/evidence/sandbox-overhead/bc07231-e2-container`) |
| `Container`, cold | `ContainerExecutionEngine` | Yes | None | 285.52 ms |
| `WarmForkWorker` | `WarmPythonExecutionEngine` | No | `AllowUnsandboxedExecution` | 2.785 ms |
| `WarmReusedWorker` | `WarmPythonExecutionEngine` | No, and candidates share one interpreter | `AllowUnsandboxedExecution` and `AllowUnsafeInProcessExecution` | Under 3 ms |
| `OutOfProcessWorker` | `ProcessProgramExecutionEngine` | No | `AllowUnsandboxedExecution` | 45 to 62 ms |

Use a `Container` mode whenever a language model writes the code. Choose warm for search, where the
per-candidate cost decides how many candidates a budget buys, and cold when even state the kernel
keeps for a user between processes must not carry from one candidate to the next.

**The risk of the unsandboxed modes.** `OutOfProcessWorker`, `WarmForkWorker` and `WarmReusedWorker`
bound a candidate's time, output and (best effort) memory, and start it with an empty environment, but
it still runs as your user on your machine. Code a model wrote can read any file you can, including SSH
keys, cloud credentials and tokens stored on disk; reach the network, including your intranet and
cloud metadata endpoints; delete or rewrite your files; and start processes that outlive the
evaluation. A model does not need to be malicious for this to matter: a confidently wrong cleanup step
or a prompt-injected instruction in a task description is enough. So the engines for these modes refuse
to start unless `ProgramSandboxOptions.AllowUnsandboxedExecution` is `true`, and the refusal names the
setting and the container alternative. Set it only for code you trust, or when the whole process
already runs inside a VM or container provisioned for the purpose. The CLI asks for the same decision
in the run file (`allowUnsandboxedExecution`, or a `container` section); see [CLI.md](CLI.md).

The container boundary is the Docker daemon and the host kernel. A container shares the kernel, so an
adversary able to exploit a kernel bug reachable through the seccomp profile is outside what it stops;
keep the host patched, point the Docker client only at a daemon you trust, and add a VM boundary for
that threat. Candidates still return output, an exit code and timing to the caller, as they must.

A run is a `ProgramExecuteRequest` (language, source, standard input, allowed languages) and returns
a `ProgramExecuteResponse`: success, exit code, captured output with truncation flags, and on failure
a `ProgramExecuteErrorCode` such as `TimeoutOrCanceled`, `CompilationFailed` or `ExecutionFailed`.
A compile-only request reports compiler messages as `CompilationDiagnostic` values, each with a
`CompilationDiagnosticSeverity`. An engine that implements `IProgramExecutionTelemetrySource` reports
how many executions are queued and active.

`InputOutputProgramFitnessEvaluator` scores a program by running it on input/output examples through
an execution engine. `ProgramOutputComparison` decides how output matches the expected output:
`Ordinal`, `TrimmedOrdinal` (the default), `TrimmedOrdinalIgnoreCase` or `NormalizedWhitespace`.

## Turning metrics into a score

Evaluator scripts report named metrics. Each is a `ProgramMetricValue` whose `ProgramMetricValueKind`
is `Number`, `Flag` or `Text`. `ProgramMetricAggregator` combines them into one score, as set by
`ProgramMetricAggregationOptions`. `ProgramMetricAggregationStrategy` is `CombinedScoreOrMean`
(OpenEvolve's rule: a combined score when one is reported, otherwise the mean), `Mean`, `Weighted` or
`Tchebycheff`. The result is a `ProgramMetricAggregationResult`: the score, the metrics that
contributed, and a `ProgramMetricIssue` for each value left out. Its `ProgramMetricIssueReason` says
why, for example `NonNumericText`, `NotFinite`, `ExcludedFeatureDimension` or `NoWeightDeclared`.

## Noisy evaluators

When one measurement is not enough, `ProgramNoiseEvaluationSession` screens candidates cheaply,
replicates the promising ones, and audits a sample of the rejects at full fidelity.
`ProgramNoiseEvaluationOptions` declares the sample counts and thresholds up front.
`ScreenAndAuditAsync` returns a `ProgramNoiseScreenReport`, with one `ProgramNoiseScreenEntry` per
candidate (its fresh measurement and cost) and the rejection audit kept separately. See
[Screening](SCREENING.md) and [Replicated evaluation](REPLICATED_EVALUATION.md).

## Rejecting near-duplicates

`NoveltyGatingProgramFitnessEvaluator` wraps an evaluator and checks each candidate against the
programs it has already accepted. `ProgramNoveltyEnforcement` decides whether a candidate judged not
novel is rejected (`Reject`) or evaluated and flagged (`Advise`).

`ProgramNoveltyPolicy` decides in up to three stages, cheapest first, configured by
`EmbeddingNoveltyOptions`:

1. **Structural.** A structural distance at or above the threshold means novel. The default is
   `ProgramTokenSetDistance` (Jaccard distance over tokens), and `ProgramLineEditDistance` (line-level
   Levenshtein) is an alternative.
2. **Embedding.** When an `IProgramEmbeddingClient` is set, the nearest neighbours are embedded, and a
   cosine similarity below the threshold means novel.
3. **Judge.** When an `IProgramNoveltyJudge` is set (for example `LlmProgramNoveltyJudge`), it
   compares the candidate with its most similar neighbour and answers with a `ProgramNoveltyVerdict`.

Each decision is a `ProgramNoveltyDecision`: the verdict, the `ProgramNoveltyStage` that decided, the
reason, the nearest program, and how many embedding and judge requests it cost. Failures of the paid
stages fail open by default, which is configurable.

## Recording provenance

`IProposalProvenanceSink` receives one `ProposalProvenanceRecord` per model request: the proposal,
evaluation and parent ids, the attempt number, when it was sent, what came back and whether it was
truncated, and the resulting `ProgramProposalOutcome`. `ProposalProvenanceOptions`
decides how much of each prompt and answer is kept.

- `InMemoryProposalProvenanceSink` is bounded and suits tests and short runs.
- `JsonLinesProposalProvenanceSink` writes crash-safe, immutable JSON Lines segments to a directory.

`ProposalProvenanceReader.Read` reads a stream back as a `ProposalProvenanceReadResult`, reporting
what it could not use and whether the stream was complete. `BuildLineage` reconstructs a
`ProposalProvenanceLineage` for one program: the chain of `ProposalProvenanceLineageStep`s, each an
accepted edit with its parent, its child and the exchange that joined them.

## Compiler-guided improvement

For programs that must compile before they run, `ProgramImprovement.RunAsync` runs one bounded
proposal-and-repair session against an `IProgramCompiler`. It then does one sealed held-out
comparison and returns an `ImprovementResult`. Programs are `ProgramSnapshot` values. A plan's edits
address `EditTarget`s in the unchanged parent. A build returns a `ProgramBuild` holding a
`ProgramArtifact`, whose fingerprint covers the source, compiler, API surface and image.
`PythonProgramCompiler` provides this loop for Python, running a trusted helper in an isolated
interpreter.
