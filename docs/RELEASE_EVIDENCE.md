# Release evidence and the release claim gate (US-12)

Issue [#30](https://github.com/ooples/AiDotNet.Evolution/issues/30). The gate decides whether a
release may make a competitive claim. It uses the hash-frozen pre-registration and the
reproducible analysis on `main` ([#114](https://github.com/ooples/AiDotNet.Evolution/issues/114),
`benchmarks/analysis/campaign_registration.py`) and the custody rules of
[#121](https://github.com/ooples/AiDotNet.Evolution/issues/121). It does not define a second
statistical method.

The checked-in [`release/claim.json`](../release/claim.json) declares **no competitive claim**.
The decisive campaign ([#122](https://github.com/ooples/AiDotNet.Evolution/issues/122)) has not
run. Releases below 1.0 may ship as engineering releases. **No 1.x version can ship** until a
`v1-gate` claim passes.

## Where it runs

| Trigger | Job | Cost |
| --- | --- | --- |
| Every PR, merge group and push to `main` | `Release evidence / Release claim gate` assesses `release/claim.json` for the version in `.release-please-manifest.json`. A release-please PR to 1.0.0 fails here unless a passing `v1-gate` claim exists. | seconds, no network |
| Every PR (existing `Build and Test`) | `test_release_gate.py` runs with the other analysis contracts, next to the existing quality-harness, replay and budget checks | seconds |
| Weekly schedule or manual dispatch only | `Bounded CPU comparison` runs `eng/Run-BudgetedComparison.ps1`: core methods plus SciPy DE on the shared C# evaluator, capped at 2..16 seeds and 32..256 evaluations each | CPU only, no model calls |
| Release tag (`automated-release.yml`) | `validate` runs the gate from the workflow revision against the tag's `release/`. `pack` and `publish` need `validate`. The assessment and the full `release/` bundle are uploaded and attached to the GitHub release. | seconds |

No workflow here calls a model, reads a secret, or launches a competitor campaign. The paid
campaign runs on the campaign machine through `benchmarks/external/campaign_runner.py`, which
refuses to launch until the operator echoes back the digest of the projected budget.

## Claim kinds (`evolution-release-claim-v2`)

- `none`: only `schema`, `claim` and `reason`. Valid below 1.0. A tag without `release/claim.json`
  also makes no claim, so older tags can still be re-published.
- `supported-families`: names `families`, and each must be supported. Families that are not
  named, and families that lost, still appear in the assessment.
- `v1-gate`: every registered family is supported under both OpenEvolve configs (recommended and
  matched), and AlphaEvolve's published values are matched or beaten on a strict majority of
  the pre-declared problems. Any 1.x version requires this claim.

A non-`none` claim also names `benchmark`, `hardware`, `registration_commit`,
`registration_path` (under `release/registrations/`) and `bundle` (under `release/`).

## What the gate verifies

1. **Frozen before the run** (#121). The registration is read from `registration_commit`, which
   must be an ancestor of the release revision and must not be the release revision itself. Its
   self-hash must verify. It must be byte-identical at the release revision and in the bundle.
2. **Sealed test tasks**. Each `partitions/<family>.json` in the bundle must hash to the
   registration's `TestPartitionHashes`. Those bytes must not appear anywhere in the repository
   tree at the registration commit, so the test inputs were not visible when the campaign was
   frozen.
3. **Equal search cost**. Every raw row must report `ModelCalls` and `ModelTokens`, and neither
   may exceed the registered budget that all arms share.
4. **Decision** (#122). `report.json` must regenerate byte-for-byte from `raw.json` with
   `campaign_registration.analyze`. That analysis uses paired seeds, a one-sided bootstrap per
   family x config, and Holm across all of them. Superiority is strict: ties fail. Failed runs
   score the registered failure value. They are never dropped.
5. **Practical effect** (#30). For a `speedup` endpoint, the geometric-mean speedup of our
   programs over OpenEvolve's must be at least **1.10x** under every config. A failed run has no
   speedup, so it fails the floor.
6. **Complete bundle**. `manifest.json` (`evolution-release-bundle-v2`) lists the SHA-256 of
   every file, and nothing unlisted may be present. Required files: `registration.json`,
   `raw.json`, `report.json`, `pilot.json`, `power-analysis.json`, every family's partition, and
   `published.json` when the registration declares a published comparison. Duplicate JSON keys,
   NaN, path traversal and bundles over 32 MiB per file or 256 MiB in total are refused.

The assessment reports the benchmark, campaign, task set (sealed partition hashes), models,
budget, seed count, hardware and every artifact. It also reports every family's endpoints with
Holm-adjusted p-values, failure counts and geometric-mean speedups, whether or not that family
was claimed.

## Limits

These checks prove consistency, not honest measurement. They cannot prove that the seal stayed
with the runner, that the campaign machine was the declared hardware, or that `pilot.json` and
`power-analysis.json` are adequate. The schemas of those two files belong to #121; the gate
only pins their hashes. Per-task regression limits from the original US-12 text are not
enforced: raw rows are per family, so the registration would first need per-task fields.

```powershell
python -m unittest discover -s benchmarks/analysis -p test_release_gate.py -v
python benchmarks/analysis/release_gate.py --release release --version 0.4.0 --output new-assessment.json
./eng/Run-BudgetedComparison.ps1 -Python C:/path/to/isolated/python -Seeds 8 -Budget 128
```