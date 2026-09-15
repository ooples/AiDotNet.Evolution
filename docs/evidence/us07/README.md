# US-07 verification and adversarial review

Implementation: `0330815`. PR [#50](https://github.com/ooples/AiDotNet.Evolution/pull/50), stacked on US-06 #49. No library defaults, public APIs, consumer packages or protections changed.

[verification.zip](verification.zip): 8,757,536 bytes; SHA-256 `c65877e8d091a260fcfb1dda650b38967afd84842b354b247533f43ffb0d0d12`.
Contains frozen plan, consumption/nomination receipts, raw results/logs, scorecard, build/format logs, TRX and coverage. Plan hashes identify the measured binaries. Workspace paths in provenance are not portable execution instructions.

## Final validation

- Release build: 0 warnings/errors.
- Tests: **720 net10 / 658 net8 / 658 net471**, 0 failures/skips.
- Python: **45 tests**, including 12 receipt-corruption variants and a CRLF regression.
- Formatting: passed.
- Modern-target line/branch coverage: **91.03% / 76.40%** and **92.09% / 78.00%**, above unchanged 88.80% / 73.51% gates. The net471 collector was unavailable; no net471 coverage claim. An empty collector directory initially tripped the local enumeration helper; selecting actual reports fixed enumeration without rerunning tests.
- Hosted CI runs a separate two-seed full-matrix contract smoke and confirmation/report verification. Smoke results never revise the registered study's nominees.

## Results

| Partition | Runs | Evaluator calls | Failed runs |
| --- | ---: | ---: | ---: |
| Development: 19 configurations × 3 tasks × 32 seeds | 1,824 | 116,736 | 0 |
| Confirmation: baseline + 3 nominees × 3 different tasks × 32 fresh seeds | 384 | 24,576 | 0 |

Development observed 16,266 novelty rejections, 2,955 migration events and 737 runs whose descriptor bounds changed. These prove mechanisms ran, not that they improved outcomes. Every run consumed its 64-call ceiling; initial identities were paired, total elite capacity stayed 64 and cost receipts reconciled.

| Family | Frozen nominee | Mean confirmation gain | Simultaneous lower gain | Decision |
| --- | --- | ---: | ---: | --- |
| Numeric | Ratio | 0.064125 | -0.441737 | Retain baseline |
| Bounded symbolic program | All except islands/migration | 0.043341 | -0.462522 | Retain baseline |
| CPU kernel | Archive growth | 0.068574 | -0.437288 | Retain baseline |

Endpoint and 0.02 promotion threshold were fixed before execution. **Inconclusive is not equivalent**; no feature/default superiority follows. `scorecard.json` includes all 57 development rows, descriptive interactions, confirmation bounds and machine-readable scoped presets.

Program search interprets 2,048 inputs per evaluator call, but is not unrestricted model-generated code. Kernels perform real blocked multiplication/distance calculations, with one warmup, three timings and exact checks on every invocation. Search-time kernel bests are selection-biased, not independently confirmed incumbent speedups. One development and one different confirmation task per family provide local evidence, not coverage of all production workloads or a competitor ranking. See [design and limitations](../../../benchmarks/analysis/ABLATIONS.md).

## Adversarial review

1. **Vacuous Double comparison:** old numeric mutation ignored inspirations. New common operator consumes them; selector tests verify use.
2. **Artificial diversity:** quantized parameters are canonicalized, islands share a total elite cap and all results use the same reference grid. Calibration uses available seed coordinates, not free objective calls.
3. **Migration confounding:** migration is measured within islands; all-no-islands is explicitly a joint islands/migration removal.
4. **Selection leakage:** disjoint seeds/tasks, frozen nominees, multiplicity-adjusted bounds and one-use claims. Interrupted registrations cannot be retried by changing directories.
5. **Fake successes:** reject missing/duplicate rows, unpaired hashes, unreconciled costs, false success, corrupted timings and nonfinite quality; failed runs retain zero-credit denominator entries.
6. **Windows request hash:** development completed but postprocessing reconstructed LF JSON against frozen CRLF bytes, raising `Request hash mismatch`. Verification now hashes the actual file; new requests use LF. Regression passed. No development evaluations repeated, no criteria changed, and confirmation had not started before the correction.

Independent approval and merges remain the user's responsibility. Presets must not be promoted beyond measured scope.
