# Standalone program novelty

The novelty pipeline from AiDotNet PR2148/2168 now lives in
`AiDotNet.Evolution.Programs.Novelty`, without an AiDotNet assembly dependency.
The migration preserves token-set and bounded line-edit metrics, optional batched
embeddings, optional model judging, explicit provider-failure policies, and a
pre-evaluation fitness gate. Twenty-one original source/test Git blobs at
`9cd7d5d6c366a483874024650d02901f69a1829c` are byte-verified in
`aidotnet-novelty-original.zip`. The original license remains in Programs/Legacy.

## Caller-owned contracts

Use `IProgramEmbeddingClient` with stable `ModelId` and caller-maintained
`VersionHash`; increment the version when model/configuration changes. The bounded
`EmbeddingBatch` and `EmbeddingVector` types own their collections. No provider
connector, API key, generic agentic runtime, or network fallback is imported.
The deterministic embedding client is retained as an offline test fixture only.

`IProgramNoveltyJudge` now requires `VersionHash`. The nongeneric
`LlmProgramNoveltyJudge` uses `IProgramChatClient`, pinned model identity, bounded
redacted prompts, a stable candidate-pair seed, and bounded responses. It accepts
only a leading explicit NOVEL/NOT_NOVEL token (including legacy negative spellings),
not verdict substrings inside prose such as `novelist`.

## Screening and evidence

1. Exact source-and-language duplicates are rejected without provider calls.
2. Structural distance may admit sufficiently different candidates for free.
3. Optional embedding and judge stages resolve remaining cases. Fail-open and
   fail-closed settings remain explicit; unavailable evidence is not a valid vector.

`structuralNoveltyThreshold: 0` disables the structural decision, as the original
options documentation promised; optional stages still run. With no optional stage,
non-identical candidates are admitted. The old `distance >= 0` test accidentally
short-circuited every configured optional stage.

Every decision reports actual embedding/judge requests. Already-primed comparisons
add zero requests. Each request adds one task-defined work unit to the gate result,
including rejected candidates; these are not tokens or money. Provider-owned
quotas remain the caller's responsibility. Cancellation/fatal failures propagate
rather than being presented as free completed work.

The evaluator serializes admission and evaluation so concurrent near-duplicates
cannot both pass an empty remembered set. Only completed inner results are retained;
failed attempts can be retried. Measured metrics, artifacts, diagnostics, direction,
constraints and sample origin are preserved when adding novelty cost.
`Remember`, `GetRememberedGenomes`, and `Reset` support explicit caller-managed state.
Mutation/reset during evaluation is rejected rather than racing with admission.

## Bounds and adversarial changes

- Embedding vectors: at most16,384 finite nonzero components; owned read-only views;
  scaled cosine arithmetic handles finite extreme magnitudes without NaN/overflow.
  Zero or mismatched dimensions cannot masquerade as evidence of novel behavior.
- Cache: at most8,192 vectors and1,048,576 total components. Priming batches contain
  at most257 genomes and1,048,576 source characters. Concurrent priming is serialized;
  clearing prevents an in-flight request from repopulating an invalidated cache.
- Embedding input uses exact source plus language, not display normalization.
  Provider/configuration drift invalidates reuse. Response order must match inputs;
  malformed counts/dimensions are failures, not cacheable results.
- Structural metrics are heuristics, not semantic-equivalence or correctness proofs.
  Token caches have both entry and source-character bounds. Line distance has a
 4,096-line ceiling and fingerprints truncated tails instead of declaring unseen
  changes identical. Custom structural distances must be finite and within `[0,1]`.
- Gate history is bounded by configured count (at most8,192) and16,777,216 source
  characters. Policies snapshot and validate caller-provided known sets.

`EmbeddingCosineGenomeDistance` deliberately no longer implements the pure
`IGenomeDistance<ProgramGenome>` engine contract: priming/cache eviction can change
its answer from structural fallback to cosine. Use its explicit async priming/sync
lookup API through the policy. Token and line metrics remain pure engine metrics.
No synchronous method performs provider I/O.

## Integration boundary

The gate owns its remembered history, not the engine's live archive. Seed/checkpoint
restoration is explicit; it does not automatically see migrants or restored elites.
The existing engine caches only completed evaluations, so novelty rejections are
not permanently reused as candidate fitness. A cache hit can reuse a completed
measurement without another gate/provider call. Live-archive structural screening
remains available through the engine's existing distance contract.

This is the novelty relocation increment, not completion of the source PRs.
Artifact/output integration, standalone facade, CLI/config/preflight/control/evidence,
and then the separate breaking AiDotNet removal still remain. Do not close #2148 or
#2168 until those replacements are verified. No claim of competitor superiority or
GPU speedup follows from these offline contract tests.
