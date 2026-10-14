# Can the proposer solve the ablation task at all?

**Yes, about one call in five.** This is the validity check both LLM ablations need, because each one's
conclusion turns on it. Driver: [`benchmarks/external/proposer_ceiling_probe.py`](../../../external/proposer_ceiling_probe.py).

## Why it had to be measured

The V1-54 (#167) ablation settles on *feedback selects, it does not create*: no rubric and no blend promoted
a generalising program because the proposer never wrote one. That is only the interesting conclusion if the
proposer **could** have written one. If the task were beyond the model, both that ablation and the V1-53
(#166) meta-prompt ablation would have been measuring a floor, and neither finding would mean much.

## Result

Five independent single calls, each given the six visible pairs, the warning that inputs will fall far
outside that range, and an explicit instruction **not** to store the answers:

| attempt | answer | recovers the rule? |
| --- | --- | --- |
| 0 | `(x*x*3 - 7*x + 9) % 97` | no |
| 1 | `(x*x-7*x+19)%97` | no |
| 2 | (refused to answer in the requested form) | no |
| 3 | `(x*x - 7*x + 17) % 97 if x < 9 else ... + 6*x - 40` | no |
| 4 | **`(x*x^(x+11))%89`** | **yes - exactly the hidden rule** |

**1 of 5.** The task sits in the band where a single attempt usually fails and occasionally succeeds, which
is the band an ablation wants: there is headroom, and reaching it is not luck-free.

## What this does to the two findings

**It strengthens #167's conclusion rather than weakening it.** A generalising program *was* proposable, so
"the proposer never wrote one" is a statement about the proposals that run actually received, not about the
model's ceiling. The ablation's prompt told the proposer the program would be used outside the shown range
but never told it not to store answers; this probe adds exactly that sentence and the model solves it 20% of
the time. So the binding constraint is the **proposer's instruction**, which is a fixable thing rather than a
capability limit.

**It makes #166's result the more interesting of the two.** Its control arm, whose fixed instruction was
"Improve the program so that it matches the examples the evaluator reports", solved the task in 1 of 6 seeds
- close to this probe's unprompted rate - while its evolving arm solved 0 of 6. An instruction that
emphasises *matching the examples* is pointing the proposer at memorisation, and meta-prompt evolution is the
mechanism that could in principle discover the wording this probe supplies by hand. That it did not, at this
budget, is the measured result; that the wording exists and works is this file.

## What this is not

- **Not a capability benchmark.** Five calls on one task with one model. The useful content is the order of
  magnitude - roughly one in five, not one in a hundred and not four in five.
- **Not evidence that a better prompt fixes the endpoint.** The probe asks for an expression directly; the
  ablation asks for a program through an evaluator that rewards matching the visible pairs. Closing that gap
  is the next experiment, not a result here.
