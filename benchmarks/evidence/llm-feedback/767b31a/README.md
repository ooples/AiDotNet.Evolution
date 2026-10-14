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

**The endpoint did not move.** Held-out accuracy is 0.0 in **every** row of `rubric-arms.json` - both
rubrics, both weights, both seeds. The only non-zero figures anywhere are in `levered.json`, and both
belong to the **control** arm: 0.0182 at seed 1 and 0.0364 at seed 2, each a single accidental agreement
with the modular rule on 1 or 2 of 55 held-out points. No arm with feedback enabled ever scored above
zero, which is why every paired difference below is at or under zero. Halving the judge's score for a gamer is not the same as changing which
candidate wins, and the corrected flip condition says why:

```
(1 - CombinedBlend) * Weight * (judge_honest - judge_gamer) > CombinedBlend * (1 - visible_honest)
```

At `CombinedBlend = 0.7`, `Weight = 0.3`, a gap near 0.29 and a seed at 1 of 6 visible, the left side is
0.026 against 0.583 on the right. Not close - and `Weight = 1` would still leave 0.087 against 0.583.
Deciding this needs **`CombinedBlend < 0.26`**, which this PR adds to the run file as
`llmFeedback.combinedBlend` - it was unreachable when the measurement was taken, and that unreachability
was the finding.

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


## The decisive arm: give the judge the leverage and it still does not move

Every measurement above runs the judge at `combinedBlend = 0.7`, where the flip condition cannot be met at
any weight. With `combinedBlend` exposed (#253) that objection can be removed: this arm gives the judge
**80% of fitness** (`combinedBlend = 0.2`), `weight = 1.0`, and the rubric that names generality. Raw rows:
`levered.json`.

| seed | arm | blended | recovered judge mean | held-out |
| --- | --- | --- | --- | --- |
| 0 | control | 1.000 | - | 0.0000 |
| 0 | judged | 0.440 | 0.3000 | 0.0000 |
| 1 | control | 1.000 | - | 0.0182 |
| 1 | judged | 0.400 | 0.2500 | 0.0000 |
| 2 | control | 1.000 | - | 0.0364 |
| 2 | judged | 0.400 | 0.2500 | 0.0000 |

Paired differences: **0.0000, -0.0182, -0.0364**. Every pair is at or below zero, and the judge's recovered
scores (0.25-0.30) agree with the 0.22-0.28 measured at the default blend - so the judge is working, is
being listened to, and the endpoint still does not move.

**The reason is that feedback selects and does not create.** The judged arm's winner, with the judge holding
four fifths of fitness and a rubric that explicitly penalises storing the shown answers:

```python
_KNOWN = {-4: 23, -1: 11, 2: 9, 5: 9, 9: 69, 13: 88}


def solve(x):
    if x in _KNOWN:
        return _KNOWN[x]
    return (x * x + 5) % 89
```

A lookup table with a fallback - the same shape as every other arm's winner. The proposer never wrote a
generalising program, so there was none for the judge to prefer. Scoring memorisers harshly changes **which
table wins**, not whether a correct program exists to win.

## What #167's criterion 4 actually answers to

On this task, LLM feedback does not move the primary endpoint at any setting. Not because the judge cannot
tell the candidates apart - with a generality criterion it scores a memoriser 0.22-0.30 against about 0.52.
Not because the weight is too low - it was given 80% of fitness. But because a selection mechanism cannot
promote a program that was never proposed.

Four defects were found and fixed on the way to that sentence, and each one was real: the judge was never
told the task, it was defeated by a documented fallback, its rubric had no axis for memorisation, and the
coefficient that decides whether it can act was unreachable. None of them was the binding constraint. The
binding constraint is upstream, in what the proposer produces - which is where #166's meta-prompt work and
the prompt itself matter, and where this measurement says to look next.
