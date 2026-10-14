# V1-54 (#167): the shipped judge under two rubrics

The merged #167 evidence left one number unreconciled. In-run, the shipped judge ranked a documented
lookup table **above** an honest-but-wrong attempt; a four-call probe of the judge's prompt *shape*
(`judge_rubric_probe.py`) did not reproduce that inversion. This measures the **shipped** judge, through
the CLI, under the default rubric and under one naming generality. Raw rows: `rubric-arms.json`.

## Result

Every arm's winner reached a perfect visible score by storing the examples, so the judge's own score is
recoverable from the blend. The engine mixes with **`CombinedBlend`** (0.7 by default) and uses `Weight`
only to scale the judge's utility, so the inversion is
`judgeMean = (blended - CombinedBlend * visible) / ((1 - CombinedBlend) * Weight)` - not the
`Weight`-as-mixing-coefficient form, which gives numbers that drift with the weight:

| seed | rubric | blended quality | **judge mean for the gaming winner** |
| --- | --- | --- | --- |
| 0 | default (`Correctness`, `Efficiency`, `Readability`) | 0.7465 | **0.5167** |
| 0 | `Correctness`, `Generality`, `Readability` | 0.7201 | **0.2233** |
| 1 | default | 0.7465 | **0.5167** |
| 1 | `Correctness`, `Generality`, `Readability` | 0.7255 | **0.2833** |

Naming generality **more than halves** the shipped judge's score for the gaming candidate, reproducibly
across both seeds, and the default rubric's figure is identical to four decimal places on both - so the
difference is the rubric and not run-to-run noise.

**It corroborates the prompt-shape probe to three decimals.** That probe scored the same documented table
at **0.517** under the default rubric and **0.223** under the generality rubric, by asking the model
directly rather than recovering anything from a blend. Here the shipped judge gives **0.5167** and
**0.2233**. Two independent routes agreeing that closely is the strongest evidence in this directory that
both are measuring what they claim.

An earlier version of this file recorded an unreconciled "in-run inversion" - the gaming candidate
appearing to score 0.787-0.839 during the first ablation against 0.155 here. **Both figures were misderived
with the wrong blend model, and the inversion does not exist**: corrected, the first ablation's documented
hybrid scores 0.5165 and its honest comparator 0.5166. The judge was not ranking the gamer above the honest
attempt; it could not separate them.

**The endpoint did not move.** Held-out accuracy stayed 0.0 in every arm (one 0.0182 accidental hit at
seed 1 under the default rubric). Halving the judge's score for a gamer is not the same as changing which
candidate wins, and the corrected flip condition says why:

```
(1 - CombinedBlend) * Weight * (judge_honest - judge_gamer) > CombinedBlend * (1 - visible_honest)
```

At `CombinedBlend = 0.7`, `Weight = 0.3`, a gap near 0.29 and a seed at 1 of 6 visible, the left side is
0.026 against 0.583 on the right. Not close - and `Weight = 1` would still leave 0.087 against 0.583.
Deciding this needs **`CombinedBlend < 0.26`**, which the run file does not expose.

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
