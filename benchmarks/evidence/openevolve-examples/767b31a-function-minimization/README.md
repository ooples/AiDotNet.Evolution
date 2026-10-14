# `function_minimization` run with the real provider

One of the four examples V1-57 (#170) names for a full real run with committed evidence. Raw rows:
`evaluations.json`. Generated run file: `run.json`.

## The run

```text
aidotnet-evolve run --openevolve-config config.yaml initial_program.py evaluator.py \
  --iterations 3 --python <venv-python> --python-path <openevolve-checkout>
```

| evaluation | `combined_score` |
| --- | --- |
| seed | 1.4221 |
| 1 | 1.3520 |
| 2 | **1.4988** |
| 3 | 1.4973 |

4 completed evaluations, 3 proposals, 0 abandoned, 0 failures. Best quality **1.4988** against a seed of
1.4221.

**Two config keys changed, and only to route the model:** `llm.provider` to `claude_code` and the two model
names to `sonnet`/`haiku`. The task, the evaluator, the prompt and every search setting are exactly as
upstream ships them - including `num_islands: 3` and its own `system_message`.

## What it shows and what it does not

**Shows:** upstream's own `config.yaml`, `evaluator.py` and `initial_program.py` drive a search through our
CLI against a real model, the cascade and island settings in that config are accepted, and the best
candidate beats the seed.

**Does not show** a converged optimisation. Three proposals is a smoke, not a study: the second evaluation
scored *below* the seed (1.3520), which is ordinary for a search this short and is left in the table rather
than dropped. Nor is it a comparison against upstream's own runner - that would need the same budget and
model on both sides.

## Why this example and not `circle_packing`

`circle_packing` is one of the four evaluators whose wrapper script embeds the program's absolute path in a
single-quoted Python literal, so every candidate scores zero on Windows. `function_minimization` does not,
which is why it can be run here without a container - measured directly, see the ledger's note.
