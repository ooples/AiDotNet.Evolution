# US-02: real program comparison, 2026-09-15

**Real execution now exists; full empirical story acceptance is not complete.**
All new code is in AiDotNet.Evolution PR #46. The Evolution host references only
AiDotNet.Evolution; the old AiDotNet companion is not required by this workflow.

## Measured before/after

The controlled Evolution track produced these independently checked results:

| Development task | Original batch (ms) | Selected batch (ms) | Speedup |
| --- | ---: | ---: | ---: |
| Base64 encoding | 289.91 | 188.68 | 1.54× |
| SHA-256 hashing | 220.82 | 192.81 | 1.15× |
| Connected components | 326.60 | 167.43 | 1.95× |

Each number is the median of three fresh-process executions on fresh diagnostic
inputs after selection. It includes Python startup, imports, input decoding,
solving and output serialization. **It is not isolated kernel latency.** The
proposals removed framework/import overhead, used direct standard-library byte
operations, and replaced NetworkX graph construction/traversal with union-find.
The contributions of those changes were not separately measured.

### All controls retained

Speedup is relative to each track's own adjacent original-program measurement.
Different baseline timings show why these ratios must not be treated as a
statistically established ranking between optimizers.

| Mode | Method | Base64 | SHA-256 | Components |
| --- | --- | ---: | ---: | ---: |
| Controlled | Evolution | 1.537× | 1.145× | 1.951× |
| Controlled | OpenEvolve | 1.526× | 1.053× | 1.884× |
| Controlled | One-shot | 1.462× | 1.103× | 2.005× |
| Controlled | Single-parent | 1.577× | 1.086× | 2.008× |
| Native-bounded | Evolution | 1.425× | 1.131× | 2.052× |
| Native-bounded | OpenEvolve | 1.435× | 1.045× | 2.143× |

There is **no demonstrated population-search advantage** here. Cheap controls
match or outperform population search on some tasks. Native-bounded means the
disclosed fixed adapter presets, not equally tuned or exhaustive native setups.

## Actual work, not just allowances

All 18 tracks completed: 33 model calls, 252 isolated timing executions, zero
unknown evaluator attempts, zero original-program fallbacks. End-to-end pilot:
705.20 seconds; model subprocess time: 380.57 seconds; search evaluation wall
time: 161.18 seconds; diagnostic evaluation wall time: 120.43 seconds. These are
different accounting quantities; evaluator time is not solve-only CPU time.

The model reported 324,142 input tokens (210,304 cached, already included) and
7,224 output tokens: **331,366 total input-plus-output tokens**. Existing ChatGPT
authentication was used; API-key calls were zero. No dollar value or exact
resolved provider snapshot is asserted.

| Task | Mode / method | Actual model tokens | Search evaluator wall seconds |
| --- | --- | ---: | ---: |
| Base64 | Controlled / Evolution | 18,877 | 6.61 |
| Base64 | Controlled / OpenEvolve | 19,844 | 10.16 |
| Base64 | Controlled / one-shot | 9,921 | 6.57 |
| Base64 | Controlled / single-parent | 18,824 | 9.85 |
| Base64 | Native-bounded / Evolution | 19,930 | 6.97 |
| Base64 | Native-bounded / OpenEvolve | 22,450 | 9.88 |
| SHA-256 | Controlled / Evolution | 18,771 | 6.79 |
| SHA-256 | Controlled / OpenEvolve | 19,757 | 9.43 |
| SHA-256 | Controlled / one-shot | 9,855 | 6.49 |
| SHA-256 | Controlled / single-parent | 18,749 | 9.41 |
| SHA-256 | Native-bounded / Evolution | 19,848 | 10.42 |
| SHA-256 | Native-bounded / OpenEvolve | 22,198 | 9.99 |
| Components | Controlled / Evolution | 19,945 | 10.13 |
| Components | Controlled / OpenEvolve | 20,349 | 10.30 |
| Components | Controlled / one-shot | 10,129 | 7.28 |
| Components | Controlled / single-parent | 19,804 | 10.57 |
| Components | Native-bounded / Evolution | 20,158 | 10.04 |
| Components | Native-bounded / OpenEvolve | 21,957 | 10.27 |

Identical model-call/evaluation ceilings did not force identical work: some
proposals repeated source identities and Evolution skipped duplicate evaluation.
One-shot intentionally used one call. Shared oracle setup is recorded once per
task in substance, though metadata repeats in each pair; do not sum duplicates.

## Adversarial review and verification

- Outside correctness oracles and Docker daemon timing prevent a candidate from
  awarding itself validity or fabricated nanosecond timing. SHA-256's host oracle
  can share a library/backend with a candidate; known-answer tests supplement it,
  but this is not a formally independent cryptographic implementation proof.
- Final isolation/protocol gate: **11 passed**, including nonroot/read-only/no
  network/no capabilities, 512 MiB memory, 32 PIDs, timeout, bounded output flood,
  fabricated timing, deeply nested invalid JSON and a global infrastructure-failure
  latch. The immutable local image is recorded in every attempt.
- Post-selection adversarial audit: **18/18 selected programs passed**, with 71
  byte cases per byte task and eight graph cases: **900 solver-output checks** in
  18 additional containers. This covers short/padding and large byte boundaries,
  dense/cyclic/disconnected graphs, self-loops, isolates and duplicate edges. No
  model calls or search feedback were allowed. Three audit regression tests pass.
- Full solution Release build: zero warnings/errors; 648 net10.0, 608 net8.0 and
  608 net471 core tests pass. Formatting passed. The proposal-budget repair used
  one targeted host rebuild, not another full dependency build/test cycle.
- Broker/controls: 11 tests; subscription transport: seven; real OpenEvolve:
  one Unicode regression. Six-track contract and real-task scripted rehearsal
  both passed. Scripted rehearsal results are not counted as model efficacy.

Failed preflights are retained: output-flood pipe deadlock, an initial-seed
proposal-budget off-by-one, and Windows cp1252 rejection of unchanged Unicode
upstream source. Repairs were validated before the live pilot. The later failure
latch/deep-JSON hardening and post-selection audit did not change the pilot's
already recorded timings; their separate source identities and tests are retained.

## Provenance and remaining acceptance

Pilot source: `d5d2aa2`; initial implementation: `f5bc75f`. The plan binds actual
host DLL and Python script hashes before dispatch. OpenEvolve:
`411fb59c886c18704caaffb611e17cf9e7d824d2`. AlgoTune:
`dff9914c10800c7a031c9e8c3d4d1c8cd1b38906`, with each original task blob verified.
Codex CLI: `0.154.0`; requested model: `gpt-6-astra`; resolved snapshot: unreported.
Image: `sha256:b31e2b5f83085c1a084d354903dbfb598a8b45f566d2f3bfd745c80540a73743`.

This is one independent search run per task/track with two proposals (one for
one-shot), three development tasks and no tuning. It establishes real candidate
improvement on the disclosed workload, **not** held-out-family generalization,
equal-cost superiority, statistical significance or the benefit of newer story
features. See [remaining Given/When/Then acceptance](../../benchmarks/US02_EMPIRICAL_ACCEPTANCE.md).
Issue #20 remains open; no automatic closing keyword, merge or release is used.

Raw records are preserved losslessly in `real-program-verification.tar.xz` using
the content-addressed format documented in
[PROGRAM_EXECUTION.md](../../../benchmarks/external/PROGRAM_EXECUTION.md).
The index maps original relative paths to full byte blobs; no failed preflight,
raw candidate, input, output, timing, model receipt or final TRX is omitted.

The archive contains 2,732 original file mappings and 965 unique byte blobs.
Repacking the verified ZIP into a solid XZ-compressed tar retains the same index
and payload hashes while avoiding repeated large JSON-output fragments. Both
archive forms were verified against their indexed SHA-256 payloads.

Final archive: **5,504,992 bytes**, SHA-256
`1eb1c33d5abfaf8958c06b28f3bc6088faad797155e91e32cb2c1f9adea3fe3c`.
Pilot report SHA-256:
`8e76d685193ac4eb91440940171f30ee3d1c473659593d924f38da2145fa31dc`.
Post-selection audit report SHA-256:
`b973a5a252f6c31552de4a069485250564865db271c823e99febce837070b4b0`.
