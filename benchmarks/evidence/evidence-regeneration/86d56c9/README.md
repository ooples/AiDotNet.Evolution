# US-04 (#22): how much of the committed evidence can be checked against raw data

Measured by [`eng/Measure-EvidenceRegeneration.ps1`](../../../../eng/Measure-EvidenceRegeneration.ps1)
at main `86d56c9`. `census.json` is the script's output, unedited.

US-04's metric is *"report regeneration diff: 0 bytes"*, and it was filled with **0 bytes - partial**.
The 0 is real: [`eng/Test-EvidenceTables.ps1`](../../../../eng/Test-EvidenceTables.ps1) regenerates the
published performance tables from `b6303d6-summary.json` and CI fails on any drift. *Partial* was the
honest qualifier and the useless one, because nothing said how much was left out. This is that number.

| class | READMEs | what a reader can do |
| --- | ---: | --- |
| `Generated` | **1 of 46** | a script re-renders the tables from a committed summary, and drift fails a build |
| `RawAlongside` | 43 | check the claims by hand against evidence committed at or beneath the README |
| `Unsupported` | **2** | nothing: the numbers exist nowhere else in the repository |

Families with **no README anywhere beneath them: 0 of 32.** The first run of this census found **7**;
each now has one, written from its own committed JSON, and every figure in those seven was checked back
against the data rather than transcribed.

## The two unsupported READMEs

- [`checkpoint/README.md`](../../checkpoint/README.md) - cites a 250 ms p95 write bound and an elite count
  at which checkpointing fails, from a `benchmarks/EvolutionCheckpoint` run whose output was never
  committed.
- [`proposer-ceiling/767b31a/README.md`](../../proposer-ceiling/767b31a/README.md) - tabulates five model
  answers and concludes *"about one call in five"*. The driver
  (`benchmarks/external/proposer_ceiling_probe.py`) is committed; its five replies are not, so the row that
  decides the conclusion cannot be re-read.

Both are summaries of real runs. Neither is re-checkable, which is the distinction US-04 exists to make,
and both were written by this project rather than inherited.

## The seven that were undocumented

`algotune-family`, `alphaevolve-math`, `campaign-registration`, `claude-transport`, `kernel-family`,
`openevolve-shim`, `upstream-evaluator-worker` - between them 168 AlgoTune mutants at 0 false accepts, 36
transcribed AlphaEvolve problems, a registration and report pair linked by hash, a 0.00827 transport
overhead fraction at 1.0 receipt coverage, a 6.461x kernel headroom median, a 0.818 ms shim p95 with 0
unrecorded prompts, and an unmodified upstream evaluator accepted on both Linux and Windows. All of it was
already committed; none of it said what it was.

## What this measurement is not

It does not regenerate anything, and it does not read a single number out of a README. It classifies by
**what a reader could do**, which is a property of the files that exist, not of whether the numbers in them
happen to be right. A `RawAlongside` README can still misquote its own JSON - four such defects were found
by hand in `openevolve-examples` on the day this was written - so 36 is a count of claims that *can* be
checked, never of claims that *have been*.

It also does not tick US-04's box. 1 of 46 is a measured value for the metric, and a metric reading 1 of 46
is not a met target.

## Reproducing

```powershell
./eng/Measure-EvidenceRegeneration.ps1 -SelfTest          # positive control, builds its own tree
./eng/Measure-EvidenceRegeneration.ps1 -OutputFile census.json
```

The census includes itself: this directory is one of the 32 families and one of the 43 `RawAlongside`
READMEs, so the output is a fixed point rather than a reading taken from outside the tree.
