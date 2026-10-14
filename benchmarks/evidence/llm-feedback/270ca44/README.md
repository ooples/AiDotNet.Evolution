# V1-54 (#167): LLM feedback as a weighted fitness signal, dev-partition ablation

Does LLM feedback move the primary endpoint? On this task, **no — at any weight**, and the reason is not
a weak signal that a higher weight would amplify. Raw rows: `ablation.json`.

Produced by the committed driver,
[`benchmarks/external/feedback_ablation.py`](../../../external/feedback_ablation.py), at `main` `270ca44`.
Real model calls throughout: a stub answers every arm identically, so a run against one measures the stub.

## Design

Each engine seed runs once per arm. The arms differ in the run file's `llmFeedback` key and nothing else,
asserted structurally by diffing the generated run files rather than by reading the builder.

The endpoint is **not** the evaluator's score. The evaluator shows six input/output pairs of a hidden rule
and the endpoint is accuracy on 55 held-out inputs it never sees, scored afterwards by a separate process.
Both arms can reach a perfect visible score either by recovering the rule or by storing the six answers,
and only the held-out set separates those — which is the property the story asks feedback to supply.

The task's difficulty was measured rather than chosen. Two earlier designs saturated the endpoint at 1.0 in
both arms, which says the task has no headroom rather than that feedback has no effect; each was stopped
within a few rows. One call per candidate rule then measured what this model recovers from six pairs, and
for `((x*x) ^ (x+11)) % 89` it answered with a lookup table — the behaviour the ablation must be able to see.

## Result

| arm | weight | pairs | off median | on median | median difference | 95% CI | favours feedback / neither / tied |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `on-w0.3` | 0.3 | 6 | 0.0000 | 0.0000 | 0.0000 | [0.0000, 0.0000] | 0 / 0 / 6 |
| `on-w0.95` | 0.95 | 6 | 0.0000 | 0.0000 | 0.0000 | [0.0000, 0.0091] | 1 / 0 / 5 |

18 arms, **zero** failed. Every arm — the control included — converged on storing the visible answers. The
single "favours feedback" pair is one accidental coincidence with the modular rule on 1 of 55 held-out
points, not a program that generalised.

## Why, measured rather than assumed

> **Corrected.** This section first reported judge scores derived with the wrong blend model, and the
> numbers below replace them. The engine's formula - documented on `LlmFeedbackOptions` and pinned by
> `The_documented_blend_formula_is_what_the_code_computes` - uses **two** coefficients:
>
> ```
> judgeUtility = (SUM s_i * Weight) / n
> quality      = CombinedBlend * measuredQuality + (1 - CombinedBlend) * judgeUtility
> ```
>
> `Weight` scales the judge's utility; **`CombinedBlend` (0.7 by default) is the mixing share**, and
> `llmFeedback.weight` maps to `Weight`, not to the blend. Inverting with `Weight` as the mixing
> coefficient yields scores that are wrong and that drift with the weight. The correct inversion is
> `judgeMean = (blended - CombinedBlend * visible) / ((1 - CombinedBlend) * Weight)`.

Recovered correctly from the same raw rows:

| comparison | gaming candidate | honest-but-wrong | discriminates? |
| --- | --- | --- | --- |
| default rubric, **bare** `TABLE.get(x, 0)` | 0.2333 | 0.5170 | yes |
| default rubric, **documented table plus fallback** | **0.5165** | **0.5166** | **no - identical to four decimals** |
| before the task description reached the judge, bare table | 0.5333 | 0.6170 | barely |

Two things change, and both make the finding sharper rather than softer.

**The judge does not rank the gamer above the honest attempt - it scores them the same**, 0.5165 against
0.5166 for the documented hybrid. "Above" was an artifact of the wrong derivation; "indistinguishable" is
what the data says, and it is just as fatal: a judge that cannot tell two candidates apart cannot prefer
either at any weight or any blend.

**Giving the judge the task description helped more than first reported**, and only against a bare table:
the gap went from 0.084 (0.5333 against 0.6170) to 0.284 (0.2333 against 0.5170). Against the documented
hybrid it did nothing.

The search converges on that third shape unprompted, in the control arm and in both feedback arms, and
documents itself while doing so:

```python
_TABLE = {-4: 23, -1: 11, 2: 9, 5: 9, 9: 69, 13: 88}
def solve(x):
    # The hidden rule was not recovered; known pairs are returned directly,
    # and other inputs fall back to a quadratic residue guess.
    hit = _TABLE.get(x)
    if hit is not None:
        return hit
    return (x * x + 5) % 89
```

So the judge cannot separate the gaming shape from the honest attempt, and the consequence is exact rather
than rhetorical. An honest candidate beats a gamer when

```
(1 - CombinedBlend) * Weight * (judge_honest - judge_gamer) > CombinedBlend * (1 - visible_honest)
```

Against the documented hybrid that gap is **0.0001**, so the left side is zero for every `Weight` and every
`CombinedBlend`: nothing flips it. With a rubric that restores a gap of about 0.29, `Weight = 1` still needs
**`CombinedBlend < 0.26`** - and `CombinedBlend` is **not exposed in the run file at all**.
presentation, not generalisation - it sees only the program source.

Two corrections worth recording, because each changed a conclusion:

- **A flip point must be derived from realised candidates.** Against a hand-written bare table the
  judge's gap looks usable (0.2333 against 0.5170); against the documented shape the search actually
  produces it is **0.0001**. A hand-written probe is a fair test of the judge and an unfair model of the
  search.
- **And it must be derived from the real formula.** Inverting with `Weight` as the mixing coefficient
  produced judge scores that drifted with the weight - the same candidate shape appearing to score 0.155
  at `w = 0.3` and 0.839 at `w = 0.95`. That inconsistency was the error announcing itself, and it is why
  the "unreconciled" note that used to sit in this file is gone: derived correctly, both weights give
  **0.5165**.

## What this is not

- **One task, one model, six pairs.** The claim is about a task where storing the answers is reachable. It
  is not a claim that feedback never helps.
- **Not a null interval to read as "no effect, within noise".** The endpoint is degenerate here: nearly every
  arm scores zero, so the interval is narrow and uninformative about effect size elsewhere. The finding is
  the judge's inability to separate the two shapes, which is measured directly.
- **Not a verdict on the judge's prompt.** Giving the judge the task description (same PR) moved the bare
  table's judge score from 0.5333 to 0.2333 against an honest 0.5170, so it tripled discrimination there
  and did nothing against the documented hybrid. A rubric that names overfitting is the variable that
  does move the documented case; it is measured separately rather than here.

## Reproducing

```powershell
dotnet build src/AiDotNet.Evolution.Cli/AiDotNet.Evolution.Cli.csproj -c Release -f net10.0
python benchmarks/external/feedback_ablation.py `
  --cli src/AiDotNet.Evolution.Cli/bin/Release/net10.0/aidotnet-evolve.exe `
  --output ablation.json --work scratch --seeds 6 --evaluations 5 --weights 0.3,0.95 --model sonnet
```

Cost is set by the call count, not prompt size: each isolated `claude -p` pays a fresh ~31.6k-token cache
creation, about $0.13 and 9 s per call whatever it is asked.
