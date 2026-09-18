# Novelty relocation verification

2026-09-17, Windows, .NET8 and .NET10. No live model/provider calls.

- Release solution build: zero warnings/errors.
- Formatting verification (`--verify-no-changes`): exit0.
- Fresh unique-version NuGet-only consumer passes, including pre-evaluation duplicate
  rejection with zero provider calls and no project references.
- Programs/compiler tests: **858 passed per framework**, zero skipped.
- Deployment tests: **41 passed per framework**, zero skipped.
- PR workflow contracts: **4 passed** on net10.
- Total: **1,802 local test executions passed**.
- Twenty-one original source/test Git blobs byte-verified at
  `9cd7d5d6c366a483874024650d02901f69a1829c` and retained in the migration archive.

`verification.zip` contains final logs and five TRX reports. The earlier854-test
intermediate run is not the final evidence bundle.

Adversarial tests cover mutable-array exposure, finite extreme vector magnitudes,
zero/mismatched vectors, verdict-token ambiguity, threshold-zero bypass, exact duplicate
admission, cached request accounting, rejected work costs, failed-attempt retries,
concurrent duplicate admission, invalid custom distances, exact source identity,
cache bounds/clear races, concurrent priming, provider drift/cancellation, fatal errors,
malformed batches, tail-fingerprint spoofing, and measured metadata preservation.

The original structural timing fixture reports cached and uncached local timings
and proves zero provider/model calls. It is not a held-out search study, semantic
correctness proof, hardware benchmark, or evidence of an OpenEvolve win.

For200 decisions against64 known genomes, the same-session memo-off versus memo-on
fixture recorded980.3 versus438.3 microseconds/decision on net8 and1098.6 versus485.2
on net10. These are local cache-on/off measurements, not before/after migration
numbers or a randomized performance study. Full raw observations are in the TRX files.
