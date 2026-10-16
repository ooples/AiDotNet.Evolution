# V1-43 (#123): the published evidence report, worked on the pilot bundle

`report.json` here is `benchmarks/analysis/release_report.py` run against the committed pilot bundle
(`benchmarks/evidence/pilot-v1-07/aafd609`). The gate campaign has not run, so this is the generator
exercised end to end on real evidence rather than the v1 release report - **the story stays open
until #122 produces that one**.

## What the report names

Benchmark (`rational_gain`), tasks, models (`sonnet`), arms, the per-cell budget (10 model calls /
600,000 tokens / 1,800 s), and every file in the bundle - which is what "publishes the full bundle"
means operationally.

**Hardware is recorded as "not recorded by this bundle"**, because the pilot runner does not capture
it. That is the one input the criterion asks for that this bundle cannot supply, and the report says
so rather than inventing a value; a test asserts the absence reads as an absence.

## Losses and inconclusive results stay visible

Both pilot endpoints are reported **inconclusive** - mean +0.3327 on `recommended` with a 95%
interval of [-0.1020, +0.7216], and +0.0260 on `matched` with [-0.1813, +0.2278] - in the same shape
a superior endpoint would use, so the format alone cannot tell a reader which way an endpoint went.
The per-config loss lists from `ranked-losses.json` are claims in their own right: `matched` lost 3
of 6 comparable cells (worst margin +0.3565), `recommended` 2 of 6 (worst +0.4857).

## The metric: claims without linked evidence = 0

Every claim carries the **relative path of the file its number came from**, and `lint` refuses the
report unless each path exists in the bundle and each claim carries a reading. Four refusals are
tested, not just the happy path:

| refused | why it matters |
| --- | --- |
| a claim with no evidence named | the metric's literal failure |
| evidence that is not in the bundle | a link that cannot be followed is not evidence |
| an inconclusive endpoint marked superior | the failure every other check here would miss |
| a recorded `Unsupported` count that disagrees with the lint | stops the number being edited |

Measured on this report: **0**.

## Reproducing

```text
python benchmarks/analysis/release_report.py benchmarks/evidence/pilot-v1-07/aafd609
```

`benchmarks/analysis/test_release_report.py` regenerates this file byte for byte from the pilot
bundle and exercises each refusal above.
