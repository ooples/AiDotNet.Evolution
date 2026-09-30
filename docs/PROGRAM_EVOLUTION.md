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

`IProgramExecutionEngine` is the execution boundary. `ProcessProgramExecutionEngine` runs each
candidate in a separate process with the limits in `ProgramSandboxLimitOptions`: wall clock, memory,
CPU, source and output sizes, and concurrency. `ProgramSandboxMode` names the requested boundary. The
process runner implements only `OutOfProcessWorker` and refuses options naming another mode, so a
request for `Serving` (a caller-provisioned remote boundary) or `InProcessUnsafe` is never silently
weakened. A separate process does not isolate the filesystem, network or identity; run hostile
programs inside a container or VM you provision.

`WarmPythonExecutionEngine` removes most of the per-candidate process start for Python.
`ProgramSandboxMode.WarmForkWorker` (Linux and macOS) keeps one interpreter warm and forks a fresh child
for each candidate, so every candidate is still a separate process with its own memory and CPU-time
limits, reported as `MemoryLimitExceeded` and `CpuTimeLimitExceeded` like the process runner.
`ProgramSandboxMode.WarmReusedWorker` (any OS) runs candidates one after another in one interpreter, each
in a fresh namespace. They share a process, so it requires `AllowUnsafeInProcessExecution`, and the
worker is replaced after a timeout, after a failure and every `RecycleAfter` candidates. The
`sandbox-overhead` workflow measures what each mode adds per evaluation.

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
