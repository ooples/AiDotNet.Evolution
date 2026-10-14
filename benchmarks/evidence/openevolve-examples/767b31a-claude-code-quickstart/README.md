# `claude_code_quickstart` runs unmodified, with the real provider

The V1-56 ledger records this example as not smoked, because it sets `llm.provider: "claude_code"` and a
loopback stub has nothing to answer. That reason is about the **stub**, not about the example — and with the
real provider it runs, and the search improves the program.

Raw rows: `evaluations.json`. The generated run file: `run.json`.

## The run

```text
aidotnet-evolve run --openevolve-config config.yaml initial_program.py evaluator.py \
  --iterations 2 --python <venv-python> --python-path <openevolve-checkout>
```

| evaluation | combined_score |
| --- | --- |
| seed | 1.0567 |
| 1 | 1.4837 |
| 2 | **1.4956** |

3 completed evaluations, 2 proposals, 0 abandoned, 0 failures. Unmodified `config.yaml`, `evaluator.py` and
`initial_program.py` from the pinned checkout - **no key was rewritten**, which no other smoked example can
say: the three stub-driven ones need `llm.api_base` and the model names redirected to avoid spending money.

## Two things it needed, and both are capabilities added this cycle

**`--python-path`** (#245). The evaluator returns an `EvaluationResult`, so the shim imports
`openevolve.evaluation_result`; the sandbox clears the child environment and does not inherit `PYTHONPATH`,
so without the option every evaluation failed with `program_script_script_failed`. This is that option's
motivating case, now measured rather than argued.

**The `ClaudeCode` provider.** `provider: "claude_code"` maps to it, so proposals route to the CLI and its
login session instead of an HTTP endpoint.

## Why the ledger's "smoked" column still says no

That column tracks `OpenEvolveExampleSmokeTests.Examples`, which is the **stub-driven** list: three keys in a
copy of each config are rewritten so CI spends nothing and reaches no network. A CLI-routed provider cannot
be served by a loopback stub, so this example cannot join that list without CI making real model calls.

So the count of stub-smoked examples is unchanged at five. What changed is the **reason** recorded against
this row, which claimed the example fails - it does not; it needs a real model, and here is the run.

## What this is not

- **Not a CI-runnable smoke.** It spends real quota and needs an authenticated CLI, so it is evidence of a
  run rather than a gate. Reproducing it needs a login session, not a secret.
- **Not a quality claim.** Two proposals on one seed. The useful content is that unmodified upstream files
  drove a search to a better program through our CLI, with nothing rewritten.
- **Not Linux-only.** Measured on Windows: this evaluator does not embed the program path in a quoted Python
  literal, so it is unaffected by the failure that defeats `circle_packing` and three others.
