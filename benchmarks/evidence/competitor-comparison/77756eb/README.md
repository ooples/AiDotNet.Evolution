# V1-19 (#124): the competitor comparison, and the lint that keeps it honest

`comparison.json` is the raw output of
[`benchmarks/analysis/competitor_report.py`](../../../analysis/competitor_report.py) at branch head
`77756eb`, and [`docs/COMPETITOR_COMPARISON.md`](../../../../docs/COMPETITOR_COMPARISON.md) is the
document it renders. Regenerate both with

```text
python benchmarks/analysis/competitor_report.py \
  --census benchmarks/evidence/competitor-comparison/77756eb/openevolve-config-census.json \
  --headroom benchmarks/evidence/alphaevolve-headroom/77756eb/headroom.json \
  --output comparison.json --document docs/COMPETITOR_COMPARISON.md
```

`openevolve-config-census.json` is the other half of the parity claim and the only piece that cannot
be re-derived offline, because it needs an OpenEvolve checkout. It is the derived key sets
themselves - 102 upstream options and 103 catalog keys, listed, not counted - taken from `v0.3.2`
(`411fb59`), with the SHA-256 of the `config.py` they were parsed out of. Re-take it with
`python eng/census-openevolve-options.py <checkout> --output <file>`.

## What it says

| verdict | rows |
| --- | ---: |
| ahead | 4 |
| parity | 3 |
| measured, no competitor figure | 12 |
| inconclusive | 1 |
| unmeasured | 1 |
| behind | 0 |

Ahead on overhead per evaluation (48.3 us against 26,917 us, ratio CI95 `[0.0016, 0.0021]`), on
overhead as the population grows (0.9985x across 100 to 50,000 against 13.35x across 100 to 5,000,
where 5,000 is the largest they completed), on keeping a concurrency budget busy (1.040x ideal wall
time at 96.2% in-flight utilisation against 2.401x at 41.6%), and on the eight archive and scheduling
defect classes that reproduce on both 0.3.2 and 0.4.0 and pass here. The overhead card's own caveat
is carried into the row: a C# controller against a Python one is expected to favour us, the ratio
narrows from 0.0018 at one worker to 0.0049 at sixteen, and it is not offered as a headline claim.

Two of the twelve rows with no competitor figure measured something **unflattering**, and say so: the
per-lease worker overhead more than doubles over a run (p50 18.7 ms in the first tenth against 44.0 ms
in the last) and the working set grows from 98 MB to 137 MB across a 200,000-evaluation soak. A
comparison that only reported the dimensions we win would not be worth generating.

**Parity on features, and it is derived rather than asserted**: 0 of their 102 config options are
unclassified, 4 of their example evaluators run through our CLI against the upstream evaluator
process, and all 36 AlphaEvolve constructions are transcribed with verifiers that reproduce the
published value.

**Search quality is the one row v1 turns on, and it is inconclusive.** One head-to-head campaign has
run. Neither endpoint reached superiority - `recommended` +0.3327 CI95 `[-0.1020, +0.7216]` at Holm
0.178, `matched` +0.0260 `[-0.1813, +0.2278]` at 0.412, both on n=6 - and the task it ran on is one a
model-free floor closes 89.9% to 94.8% of. That is not a loss; it is an absence, and it is what the
pre-registered gate campaign exists to settle.

## Why the lint is the point

A comparison report is the easiest document in a repository to write dishonestly, because every
number in it can be true while the selection does the lying. So no verdict in this bundle is a
sentence somebody wrote:

- a row must name the file its numbers came from, and that file must be in the tree;
- `ahead` and `behind` need a competitor figure to be ahead or behind **of**, and either an interval
  that excludes parity or two measured ranges that do not overlap - one lucky run cannot become a
  win;
- a **floor study** can never read `ahead`. It measures how far a model-free search gets, which is a
  statement about the task; reading it the other way is how *"the floor is 10% behind AlphaEvolve"*
  becomes *"we are 10% behind AlphaEvolve"*;
- the **search-quality** row cannot read `ahead` unless an endpoint in the campaign it cites is
  marked superior. Today none is. That single check is what stands between two inconclusive pilot
  endpoints and a v1 claim of beating OpenEvolve on quality;
- a row recorded as having no competitor figure may not quote one, and the headline counts must equal
  the rows beneath them.

Each of those refusals is exercised against the real report in
[`benchmarks/analysis/test_competitor_report.py`](../../../analysis/test_competitor_report.py) by
changing exactly one thing and requiring the lint to catch it - and the first test requires the
unmodified report to pass, because a lint that refused everything would satisfy all the others. The
document is held the same way: the test re-renders it and compares the bytes, so it cannot be edited
into saying something the bundles do not.

## What this is not

Not the release evidence report (#123), which reports one campaign from its own bundle. Not the gate
campaign (#122): until that runs, the quality row stays `inconclusive` by construction, and the
honest reading of this bundle is **feature parity reached, performance ahead by one to three orders
of magnitude on the dimensions measured, search quality undecided**.