# Model-driven runtime and PR2203 relocation

This increment transfers the complete three-file contribution of AiDotNet PR2203
into `benchmarks/ProgramEvolutionComparison`, backed by real standalone Programs APIs.
It depends on Evolution PR90, whose hosted checks are all green.
The existing `benchmarks/EvolutionComparison` track is not replaced or rewritten.

## Disposition

| Source | Evolution replacement |
|---|---|
| PR2203 Program.cs | Standalone seven-argument broker host, controlled/native-bounded modes, engine task, descriptors, actual LLM variation, evaluation and artifact receipts |
| PR2203 project | Programs-only project reference, included in the Evolution solution |
| PR2203 README | Invocation, ownership, broker security, verification and evidence guidance in the new host directory |
| Combined foundation prompt files | Public prompt/context/template/redaction APIs under Programs/Prompts |
| LLM variation and options | Non-generic `LlmProgramVariationOperator`, `ProgramProposalOptions`, prompt/variation options, checkpointed attempts and usage |
| Provenance records, sinks and readers | Programs/Provenance; bounded JSONL persistence, redaction and lineage reconstruction |
| AiDotNet text chat dependency | Caller-owned `IProgramChatClient` and immutable text/response/usage contracts; no network connector or AiDotNet reference |
| Five source test suites and chat fixture | CSharp.Tests/ModelRuntime, compiled against the transferred runtime |

The original three PR2203 blobs at `9343d45907342eabd1f24cffa4724e7817855bf4`
are preserved in `aidotnet-pr-2203-original.zip`. Thirty-eight supporting original
source/test/configuration blobs at `9cd7d5d6c366a483874024650d02901f69a1829c`
are in `aidotnet-model-runtime-original.zip`. Every entry was verified against its
original Git blob hash. Original BSL licensing is retained in `src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt`.

Text-only chat is intentional: this variation path consumes text proposals, never model
tool requests. General-purpose AiDotNet chat/agent APIs are not duplicated or removed.
`ProgramProposalOptions` extends standalone task/edit bounds with prompt settings;
it is not a forwarding API for the old AiModelBuilder facade.

## Adversarial corrections

- Model/provider identity now participates in compatibility hashing and is checked
  before and after calls; changing a provider mid-run cannot silently change semantics.
- Providers receive a read-only conversation snapshot. Retry context is bounded before
  another dispatch, rather than allowing feedback to exceed the initial prompt ceiling.
- Oversized responses are rejected before fence/diff parsing. Deserialized attempt state
  has a size/depth bound, and attempt parent identifiers are bounded.
- Token totals avoid Int32 overflow; absent provider usage remains unknown, not free work.
- The comparison host emits a failed report and nonzero exit after evaluator failures
  or unreconciled work. Previously the engine could retain a failure while the host
  still emitted a success-shaped report and returned zero.
- Host tests preserve exact UTF-8 source bytes on Windows. Default text writes changed
  LF to CRLF, correctly triggering the broker's initial-source hash check; newline
  translation was disabled instead of weakening identity verification.

## Verification

- Release solution build: zero warnings/errors; formatting applied and verified.
- Programs/compiler tests: 508 net8.0 + 508 net10.0.
- Deployment regression tests: 41 on each framework; workflow contract: four tests.
- **1,102 scoped .NET tests passed, zero skipped.**
- Four Python host contracts cover ten subprocess runs: both modes; the real shared
  broker; corrupt hashes, unknown work, invalid status, negative work, output refusal,
  invalid endpoint and missing capability.
- Real shared broker fixture: two model requests, three evaluations, fourteen scripted
  token units, three scripted work units, five completed receipts. These are authored
  functional measurements, not live model quality or hardware-speed results.
- Unique-version package consumer loads the relocated prompt/variation APIs without
  project references. Existing deployment and program-engine integration remains checked.

Evidence is retained in `docs/evidence/model-runtime-relocation/verification.zip`.
No paid calls, releases, merges, branch deletion, or candidate execution occurred.

## What still requires transfer

PR2203 can be closed as relocated once this replacement is published. This does not
close PR2148 or PR2168: execution/sandbox/evaluator/output/facade orchestration and the
actual CLI/configuration/preflight/control/evidence integrations still need verified
standalone replacements. Their original option files in the archive are not proof
those features have been ported. The separate breaking AiDotNet removal follows those
transfers; no obsolete forwarding APIs are planned.
