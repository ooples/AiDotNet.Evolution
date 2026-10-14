# V1-54 (#167): the shipped judge under two rubrics

The merged #167 evidence left one number unreconciled. In-run, the shipped judge ranked a documented
lookup table **above** an honest-but-wrong attempt; a four-call probe of the judge's prompt *shape*
(`judge_rubric_probe.py`) did not reproduce that inversion. This measures the **shipped** judge, through
the CLI, under the default rubric and under one naming generality. Raw rows: `rubric-arms.json`.

## Result

Every arm's winner reached a perfect visible score by storing the examples, so the judge's own score is
recoverable from the blend: `judge = (blended - (1 - w) * visible) / w` at `w = 0.3`, `visible = 1.0`.

| seed | rubric | blended quality | **implied judge score for the gaming winner** |
| --- | --- | --- | --- |
| 0 | default (`Correctness`, `Efficiency`, `Readability`) | 0.7465 | **0.1550** |
| 0 | `Correctness`, `Generality`, `Readability` | 0.7201 | **0.0670** |
| 1 | default | 0.7465 | **0.1550** |
| 1 | `Correctness`, `Generality`, `Readability` | 0.7255 | **0.0850** |

Naming generality **roughly halves** the shipped judge's score for the gaming candidate, reproducibly
across both seeds, and the default rubric's score is identical to four decimal places on both — so the
difference is the rubric and not run-to-run noise.

This agrees in direction with the prompt-shape probe (which measured a 3.7x wider honest-minus-table gap)
and, unlike that probe, it is the judge the product actually ships.

**Still not reconciled:** the in-run inversion. Here the gaming candidate scores 0.155 under the default
rubric, not the 0.787-0.839 measured during the earlier ablation. The earlier figures came from runs whose
winners carried different source text, so candidate content - not the rubric - is the remaining variable.
That is recorded as open rather than explained away.

**The endpoint did not move.** Held-out accuracy stayed 0.0 in every arm (one 0.0182 accidental hit at
seed 1 under the default rubric). Halving the judge's score for a gamer is not the same as changing which
candidate wins: at `w = 0.3` the visible-score gap still dominates, which is exactly what the merged
evidence's flip-weight arithmetic predicts.

## A constraint found by running it

A criterion **doubles as a metric name**, so a descriptive one is refused:

```
exit 2: error: Judge descriptor names must be bounded and canonical. (Parameter 'MetricPrefix')
```

That cost a campaign run to diagnose, because the message named `MetricPrefix` - the one field the caller
had not set - rather than the criterion that overflowed the 64-character bound. The message now names the
offending descriptor, its length, and where to put an explanation instead; the prefix is validated
separately so a genuine prefix problem is still attributed to the prefix.

Keep each criterion a short token (`Generality`) and put the wording in `prompt.evaluatorSystemMessage`.

## Reproducing

```powershell
python benchmarks/external/feedback_ablation.py --cli <aidotnet-evolve> `
  --output rubric-arms.json --work scratch --seeds 2 --evaluations 5 --weights 0.3 `
  --rubric "default=" --rubric "general=Correctness;Generality;Readability" --model sonnet
```
