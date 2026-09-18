# Standalone program judging

The LLM fitness wrapper from AiDotNet PR2148/2168 now lives in
`AiDotNet.Evolution.Programs`, with no reference to the AiDotNet assembly.
Source: `9cd7d5d6c366a483874024650d02901f69a1829c`; four original implementation,
options, and test blobs are retained byte-for-byte in `aidotnet-judge-original.zip`.
The original license remains in `src/AiDotNet.Evolution.Programs/Legacy/AIDOTNET-LICENSE.txt`.

## API transition

Use nongeneric `LlmJudgeProgramFitnessEvaluator`, `LlmFeedbackOptions`, and the
caller-owned `IProgramChatClient`. The removed numeric generic parameter did not
participate in scoring. `ProgramChatOptions.ResponseFormat` expresses a JSON
request; adapters must honor it or explicitly reject unsupported requests. It is
not proof of valid output. The evaluator validates returned JSON independently.

`ProgramJudgePanel` accepts 1–32 weighted `ProgramJudgeMember` providers. Set
`JudgeWithEveryEnsembleMember=true`. Members are called sequentially once; unusable
members are excluded from the weighted mean. A single provider retains the bounded
retry behavior. Decorators expose `IProgramChatClientDecorator.Inner` for panel
discovery. Panel dispatch uses each member directly, so put required retry,
authorization, telemetry, and budget middleware on the members, not only outside
the panel. No generic AiDotNet agentic pipeline or provider connector is imported.

## Adversarial changes

- Model/decorator/panel identity, inner evaluator identity, full options, and prompt
  identity determine the version. Identity drift is refused before/after requests.
- Blending requires measured quality in `[0,1]`. Normalize raw latency or other
  unit-bearing measurements first, or use `CombinedBlend=1` for annotation only.
  Judge utility is inverted for minimizing scalar quality and appended objectives;
  descriptors retain utility. Existing failure status and constraints remain intact.
- Existing sample-origin uncertainty is never relabeled as blended-score evidence.
  Origin-bearing scores are refused before model spending, preserving their evidence.
- Normalized criterion collisions, descriptor collisions/capacity, oversized/deep
  JSON, duplicate keys, numeric strings, booleans, and nonfinite scores are rejected.
  Legacy finite out-of-range judge scores still clamp to `[0,1]`.
- Every dispatched judge request adds configurable `JudgeCallCostUnits` (default1)
  to task cost, including unusable responses and retries. These are task-defined
  work units, not money or token usage. Caller-owned providers still enforce their
  actual quota/budget. Cancellation/fatal failures propagate; they do not prove zero
  consumption. `JudgeCalls` counts actual dispatches, including partial canceled panels.
- Failed panel members cannot overflow or distort the remaining weighted average.
  Provider options/transcripts are isolated across members.
- Optional critique forwarding is retained, bounded, and explicitly **not redacted**.
  Set `CarryCritiqueForward=false` to omit model-authored prose. Parser/provider error
  diagnostics never include response text or provider exception messages.

## Verification

Imported both original judge/critique suites and added adversarial regression tests.
The package-only consumer exercises an offline judge through freshly packed NuGet
artifacts. A fixture blend `.7*.5 + .3*1 = .65` is a contract check, not measured
performance, correctness improvement, or evidence of an OpenEvolve win.
Final test results are recorded in `docs/evidence/judge-relocation/README.md`.

## Remaining cleanup

This completes the judge increment, not all of AiDotNet PR2148 or PR2168. Novelty
evaluators, artifact/output integration, standalone run facade, CLI/config/preflight/
control/evidence integration still require relocation. Keep those source PRs open
until verified replacements cover their complete behavior. The separate clean-breaking
AiDotNet deletion follows those replacements. US-11/PTX remains after consolidation.
