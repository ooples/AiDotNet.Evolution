# Standalone consumer relocation verification

Local Release solution build: zero warnings/errors. Core tests: 1,165 net10.0,
1,081 net8.0, 1,081 net471. Program/compiler tests: 203 on each of net10.0/net8.0.
Total: **3,733 passing test executions**. Full solution formatting verification passed.

Fresh consumer study: 18 runs, 5,768 independent fits, 1,680 sorting invocations,
6,608 correctness checks, and 14,056 reconciled calls. Conservative ridge screening
accepted on all six roots; aggressive ridge/timing presets rejected on all twelve.
All six deliberately corrupt reports were rejected. Existing-output and invalid-
argument checks passed. No provider/model calls or competitor superiority claim.

`verification.zip` preserves raw observations, final TRX receipts, and build/test logs.
Extract it to a new directory and pass `consumer-noise.json` to
`benchmarks/EvolutionNoise/Verify-Study.ps1` and `Verify-Study.Tests.ps1`.
The raw report hashes the tested Programs/core/tensor assemblies; AiDotNet model
primitives are pinned to package 0.231.0 in the benchmark project.

Historical evidence remains separate in `docs/migration/aidotnet-pr-2210-original.zip`
and `aidotnet-pr-2212-original.zip`. Every entry was verified against the original
Git blob. Windows `git archive` initially converted Markdown LF to CRLF; regeneration
with `-c core.autocrlf=false -c core.eol=lf` preserved the source bytes.
