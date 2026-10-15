# `background_blur` with the real provider: a real optimisation, checked against upstream's gaming suite

The fourth example run end to end through the CLI against the real Claude Code provider, at upstream's
pinned ref `411fb59`. Raw rows: `evaluations.json`. Winner: `best.py`. Generated run file: `run.json`.

## The run

4 completed evaluations, 3 proposals, 0 abandoned, 0 provider errors, 6,857 tokens.

| evaluation | `combined_score` | `ssim` | ms/frame |
| --- | ---: | ---: | ---: |
| 1 (seed) | 1.0192 | 1.000 | 48.5 |
| 2 | 23.2082 | 1.000 | 2.1 |
| 3 | **23.3174** | **1.000** | **2.1** |
| 4 | 12.7365 | 1.000 | 3.8 |

**22.9x faster at unchanged output quality** - 48.5 ms/frame to 2.1, with mean and worst-frame SSIM both
1.000.

Unlike `signal_processing` beside it, this evaluator reports `combined_score`, so upstream's own fitness
rule (`metrics_utils.get_fitness_score`) uses it directly: the number being optimised is the one the
example's author chose.

## It is not gaming, and that is checked rather than asserted

A 23x speedup at SSIM 1.000 is exactly the shape a gamed result takes, so the winner was run through
**upstream's own** `examples/background_blur/test_gaming.py`, which exists for this purpose. **5 of 5
pass**, and its output names what it rejects and what it accepts:

```text
[rejected] do-nothing (returns input):        mean SSIM 0.6625 (needs >= 0.98)
[rejected] blurs everything (ignores mask):   mean SSIM 0.7519
[rejected] under-blurs (kernel far too small):mean SSIM 0.6985
[rejected] reuses frame 0's background:       mean SSIM 0.9871, worst-frame 0.9806
[accepted] separable convolution:             9.77x speedup, SSIM 1.0000
```

The winner is a **separable Gaussian convolution** - its own docstring says so, and the code is a 1D
truncated 3-sigma kernel applied along each axis with reflect padding. That is the strategy upstream's
suite names as the honest optimisation.

## What the numbers are not

- **22.9x is not "better than upstream's 9.77x".** That figure comes from upstream's own test fixture with
  its own frame count and timing method; ours is this machine, this fixture, four evaluations. The two are
  not comparable, and the only claim being made is that the winner is the honest strategy their suite
  accepts.
- **Three proposals is a smoke, not a study.** The fourth evaluation regressed to 12.74, which is ordinary
  for a search this short and is left in the table rather than dropped.
- Not a comparison against upstream's runner, which would need the same budget and model on both sides.

## Reproducing, and one trap worth knowing

```powershell
aidotnet-evolve run --openevolve-config config.yaml initial_program.py evaluator.py `
  --iterations 3 --python <venv-python> --python-path <openevolve-checkout>
```

**One `--python-path`, for the checkout only.** The evaluator also imports `fixtures` from its own
directory, and the shim already inserts that directory into `sys.path` - *"OpenEvolve evaluators import
helpers that sit beside them"*. The run above was taken with a second, redundant `--python-path` for the
example directory; a later run with only the checkout completed its evaluations the same way, seed
`combined_score` 1.0162 then 24.2125, so the second entry was never doing anything. The interpreter needs
`numpy`.

**This example declares its model as an `llm.models` list, not `primary_model`/`secondary_model`.** Routing
only the latter leaves the member name untouched, so the original model reaches the Claude Code provider,
which does not have it - and with that config's `timeout: 600` and `retries: 6`, the run stalls for over an
hour with no diagnostic rather than failing. Measured: 1.7 s of CPU across 20 minutes, one completed
evaluation, no child processes.
