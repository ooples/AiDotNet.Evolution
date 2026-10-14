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

The judge's own score is recoverable by inverting the blend,
`judge = (blended - (1 - w) * visible) / w`, using `visible_correct` from the trace metrics:

| candidate | judge score |
| --- | --- |
| bare `TABLE.get(x, 0)` of the six visible pairs | **0.070** |
| honest-but-wrong formula | 0.155 - 0.269 |
| **table plus a principled-looking fallback, commented** | **0.787 - 0.839** |

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

So the judge scores the gaming shape **above** the honest attempt, and blended fitness for the gamer stays
0.839-0.984 against 0.177-0.269 across `w` from 0.1 to 1.0. **No weight in [0, 1] reverses the ranking**;
raising it makes matters worse, because at high `w` the judge's preference dominates. The judge is scoring
presentation, not generalisation — it sees only the program source.

A correction worth recording: using the bare-table probe alone (`j_g = 0.070`), the flip point computes to
`w > 0.907`, which reads as "the weight is merely too low". That is false for the candidates the search
produces. A flip point must be derived from realised candidates; a hand-written probe is a fair test of the
judge and an unfair model of the search.

## What this is not

- **One task, one model, six pairs.** The claim is about a task where storing the answers is reachable. It
  is not a claim that feedback never helps.
- **Not a null interval to read as "no effect, within noise".** The endpoint is degenerate here: nearly every
  arm scores zero, so the interval is narrow and uninformative about effect size elsewhere. The finding is
  the judge's ranking, which is measured directly.
- **Not a verdict on the judge's prompt.** Giving the judge the task description (same PR) moved the bare
  table from 0.160 to 0.070 and left the documented one untouched, so a rubric that names overfitting
  explicitly is untested and remains the obvious next experiment.

## Reproducing

```powershell
dotnet build src/AiDotNet.Evolution.Cli/AiDotNet.Evolution.Cli.csproj -c Release -f net10.0
python benchmarks/external/feedback_ablation.py `
  --cli src/AiDotNet.Evolution.Cli/bin/Release/net10.0/aidotnet-evolve.exe `
  --output ablation.json --work scratch --seeds 6 --evaluations 5 --weights 0.3,0.95 --model sonnet
```

Cost is set by the call count, not prompt size: each isolated `claude -p` pays a fresh ~31.6k-token cache
creation, about $0.13 and 9 s per call whatever it is asked.
