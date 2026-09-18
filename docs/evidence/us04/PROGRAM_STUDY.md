# US-04 completed reporting acceptance

US-04 / [issue #22](https://github.com/ooples/AiDotNet.Evolution/issues/22) /
[non-draft PR #47](https://github.com/ooples/AiDotNet.Evolution/pull/47).
All implementation is in **AiDotNet.Evolution**, stacked on US-02 #46, US-01 #45
and foundation #15. Reporting implementation and local acceptance are complete;
hosted CI, dependency readiness, independent review and user merging remain gates.

## What was actually run

The [protocol](../../benchmarks/US04_PROGRAM_STUDY_PROTOCOL.md) was frozen at
`3b4c005dcca86b6296f16c7797e5ffe7d02c8076`. Its registration SHA-256 was published
[before dispatch](https://github.com/ooples/AiDotNet.Evolution/issues/22#issuecomment-5688438053):
`5baece4a0147396944d0be242b6051f110102bc6742a02e30df4601cb2e8cee6`.

- Two calibration seeds (101,103), then two precommitted confirmation seeds
  (1009,1013); distinct search/diagnostic/audit instances in every block.
- Three AlgoTune-derived development task families; six tracks each: controlled
  Evolution/OpenEvolve/one-shot/single-parent and native-bounded Evolution/OpenEvolve.
- **72/72 scheduled cells completed; 72/72 selections passed independent audits**
  (3,600 exact-contract checks). All 72 trajectories reconciled independent counters.
- **132 ChatGPT-subscription model calls**, 1,330,863 reported input+output tokens;
  cached input tokens are already included. No API-key calls or fallback.
- **1,080 containers**: 1,008 timing attempts and 72 audit attempts. Known supervisor
  wall work: **1,258.534 seconds**. Zero unknown model/container attempts.
- Actual reuse of the consumed registration refused with `FileExistsError` before
  dispatch. No optional stopping, retries of the study, extra seeds or tuning.

This is a fixed-budget **estimation** study on development families, not a
powered release comparison, unseen-family test or official AlgoTune leaderboard score.
Timing repetitions are not independent searches. Requested model `gpt-6-astra`
and CLI 0.154.0 are pinned; the resolved provider snapshot is unreported.

## Before and after, without a superiority claim

Controlled Evolution; geometric means of the two confirmation runs' paired
median runtimes. Units are fresh-process batch milliseconds, including startup,
imports and serialization—not isolated kernel latency.

| Task | Original ms | Deployed ms | Original / deployed |
| --- | ---: | ---: | ---: |
| Base64 encoding | 283.500 | 200.467 | 1.414x |
| Connected components | 331.436 | 174.376 | 1.901x |
| SHA-256 hashing | 205.781 | 203.379 | 1.012x |

Candidate improvements do **not** establish optimizer superiority. The preregistered
endpoint is equal-task-weighted paired bounded utility, not a ratio of differently
noisy baseline speedups. Positive effects favor Evolution; actual costs differ.

| Confirmation comparison | Paired utility effect | Wins / ties / losses | Simultaneous interval |
| --- | ---: | --- | --- |
| Controlled vs OpenEvolve | -0.006339 | 1 / 0 / 5 | [-1,1] |
| Controlled vs one-shot | -0.005405 | 3 / 0 / 3 | [-1,1] |
| Controlled vs single-parent | -0.003944 | 2 / 0 / 4 | [-1,1] |
| Native-bounded vs OpenEvolve | -0.005988 | 2 / 0 / 4 | [-1,1] |

The intervals are inconclusive and conditional on independent stationary runs
within tasks. Shared host/provider drift remains a limitation. Observed losses
are retained, not replaced with favorable seeds. The search-only utility target
was reached in 31/36 calibration cells and 36/36 confirmation cells; this target
is not a claim of improvement over every original or competitor.

## Given / When / Then acceptance and adversarial review

- **Given** a stopped campaign, **when** reporting runs, **then** JSON, Markdown
  and escaped offline HTML/SVG retain every scheduled cell, failures/missing
  outcomes, effects, conditional intervals and explicit scorecard unknowns.
  Four resource axes and left-step observed-domain AUC are available. Unequal
  or censored AUC domains are not presented as fixed-budget efficacy rankings.
- **Given** calibration variance, **when** planning, **then** advisory and
  conservative counts remain distinct; the fixed confirmation count cannot be
  extended. The original one-sided advice was 12,715 runs. Adversarial review
  corrected the prospective adviser to match the two-sided reported interval:
  **13,648 runs per task/method**, not an affordable or minimum necessary count.
  Both exceed the precommitted two-run estimation budget. Original advice is
  preserved; no observations, effects, intervals or feasibility decision changed.
- **Given** nested tasks/search runs, **when** computing intervals, **then**
  comparisons remain paired within task/seed/mode and timing repetitions never
  become search seeds. Missing panels withhold intervals; failed selections
  retain the measured original fallback. Calibration is not another chance
  to select a winning confirmation claim.
- **Given** dropped trace records or missing outcomes, **when** reporting costs,
  **then** independently maintained counters take precedence over retained rows.
  Regression evidence preserves 29 tokens/9 work seconds despite an empty trace
  and stale 10/3 summary values, while suppressing the curve. Unknown attempts
  retain known subtotals but block complete-cost claims; legacy summaries are labeled.

Additional adversarial checks reject changed source/seed/audit/median bindings,
duplicate observations, impossible counters, altered registered estimates,
unsupported driver parameters, archive traversal/aliases/member collisions and
existing restore destinations. Restored duplicate blobs become separate files.

**Post-run reporting changes are explicit:** runtime/controller source remains
the frozen revision above. Final reporting adds scorecards, safe restoration,
independent summary costs and corrected prospective advice. Reanalysis verified
all 72 original outcomes and costs unchanged; the original study remains immutable.

## Verification and reproducibility

One final Release solution build and formatting verification passed with zero
build warnings/errors. Core tests passed: **654 net10.0, 608 net8.0, 608 net471**.
External Python tests: **37 passed**, including the actual pinned OpenEvolve
controller and Docker/oracle safety checks. The real six-track controller
fixture reconciled all independent counters. After reporting corrections,
the final analysis gate passed **85 tests**; no second full build was needed.

- [JSON scorecard](program-study-analysis/scorecard.json)
- [Markdown scorecard](program-study-analysis/scorecard.md)
- [Offline HTML/SVG](program-study-analysis/scorecard.html)
- [Lossless study archive](program-study.tar.xz): 22,394,776 bytes, 8,484 file
  mappings and 2,874 verified blobs; all original results, raw receipts, prompts,
  model outputs, plans and advice retained.
- [Verification archive](program-study-tests.zip): core TRX files, final analysis
  log, actual repeated-use refusal, Docker evidence and six-controller fixture.

SHA-256:

```text
program-study.tar.xz    0687681df8feb72cc5ccdc65d5dd149579c3018c1ffb81b2157dc40b3d8cba88
program-study-tests.zip b892a9526429a87eaf50bb2a122d188065d887aebbed180dd5f7ebbd63b30f3c
original study.json    9b38df2d6c98fd443673f5f178680e852ba75064fd8ee04274c15883faaa33bf
original sample-advice.json 46d84443e3260b3f8beddc5c2a185f9aa65255e86eee0d0b087b62a78d07b4cc
```

```powershell
python benchmarks/analysis/program_evidence.py --archive docs/evidence/us04/program-study.tar.xz --sha256 0687681df8feb72cc5ccdc65d5dd149579c3018c1ffb81b2157dc40b3d8cba88 --output <new-restored-directory>
python benchmarks/analysis/program_scorecard.py --root <new-restored-directory> --output <new-report-directory>
python -m unittest discover -s benchmarks/analysis -v
```

The archive was actually restored (1,345,475,360 bytes) after verifying every
blob. Regenerated JSON, Markdown and HTML were **byte-identical** to the local
outputs; after the repository's LF normalization, their Git blob identities also
exactly matched the checked-in reports. Offline regeneration requires no model/evaluator calls. Hash
consistency is not authentication; retain hashes independently and trust the
filesystem custody. Peak memory, separate kernel/startup timings, CPU/GPU time,
subscription cost allocation and cross-controller QD remain explicitly unmeasured
or inapplicable. These are not fabricated zero-valued measurements.

The agent's adversarial review does not replace independent PR approval. No
merge, publication, protection change or entire-roadmap completion is claimed.
