# `signal_processing` with the real provider, and the fitness it actually optimised

A third example run end to end through the CLI against the real Claude Code provider, at upstream's pinned
ref `411fb59`. Raw rows: `evaluations.json`. Generated run file: `run.json`.

**Read the second table before quoting the first.** This run is not an improvement, and the reason is the
interesting part.

## What the run reported

4 completed evaluations, 3 proposals, 0 abandoned, 0 provider errors, 7,433 tokens. Two config keys
changed and only to route the model: `llm.provider` to `claude_code` and the two model names to
`sonnet`/`haiku`.

| evaluation | our fitness | the example's own `composite_score` |
| --- | ---: | ---: |
| 1 (seed) | 13.7279 | **0.4200** |
| 2 | 12.7456 | 0.4152 |
| 3 | **16.5108** <- our best | **0.3959** <- the worst of the four |
| 4 | 15.6073 | 0.4040 |

**Our fitness rose while the example's headline score fell.** The candidate we would report as the winner
is the one its author's own metric ranks last.

## Why, and why it is not a divergence from upstream

The evaluator reports nine metrics, among them `slope_changes` (66 -> 92) and `false_reversals`
(55.2 -> 73.6), where **higher is worse**. Fitness here is the mean of the non-feature metrics, so those
two dominate it and their rise outweighs the fall in `composite_score`.

That mean is also what **OpenEvolve** computes. `openevolve/utils/metrics_utils.py`'s `get_fitness_score`
prefers `combined_score` when present and otherwise averages the non-feature metrics; this evaluator names
its headline `composite_score`, which that rule does not recognise, and the config's
`feature_dimensions` are `complexity` and `diversity`, neither of which the evaluator emits. So nothing is
excluded, and upstream would optimise the same aggregate.

**So this is parity, not a bug in the import** - and it is worth recording precisely because a run that
looks healthy (no failures, four completed evaluations, a rising best quality) can still be moving the
example's own objective the wrong way.

## What this evidence is and is not

- **Is**: proof that upstream's `config.yaml`, `evaluator.py` and `initial_program.py` drive a real search
  through this CLI on Windows, with the cascade and island settings in that config accepted, and that the
  seed scores what the ledger records for it (`composite_score` 0.420).
- **Is not**: an improvement, a comparison against upstream's runner, or evidence that three proposals
  suffice for anything. The headline score fell.

## Reproducing

```powershell
aidotnet-evolve run --openevolve-config config.yaml initial_program.py evaluator.py `
  --iterations 3 --python <venv-python> --python-path <openevolve-checkout>
```

The interpreter needs `numpy` and `scipy`; `--python-path` is needed because the evaluator imports from the
upstream checkout.
