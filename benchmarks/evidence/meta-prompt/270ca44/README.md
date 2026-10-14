# V1-53 (#166): meta-prompt evolution, paired dev-partition ablation

Does evolving the system instruction move the primary endpoint? **No effect at the median, and the one
decisive pair went against it.** Raw rows: `ablation.json`. Driver:
[`benchmarks/EvolutionMetaPrompt`](../../../EvolutionMetaPrompt/README.md), at `main` `270ca44`.

Real model calls throughout. 12 arms over 6 paired seeds, **zero failed**.

## Result

| arm | solved the task | median endpoint |
| --- | --- | --- |
| `Fixed` (seed instruction pinned) | **1 of 6** | 0.0000 |
| `Evolving` (instructions evolve) | **0 of 6** | 0.0000 |

| statistic | value |
| --- | --- |
| median paired difference | **0.0000** |
| 95% CI (4,000-draw seeded bootstrap) | **[-0.4909, 0.0091]** |
| mean paired difference | -0.1606 |
| favours evolving / favours fixed / tied | 1 / 1 / 4 |

Read it carefully, because the median and the mean say different things and both are true. Four of six
pairs are exact ties at zero: neither arm generalised, so they carry no information about the feature. Of
the two pairs that moved, one is an **accidental** coincidence with the modular rule on 1 of 55 held-out
points (+0.0182, not a working program), and the other is the only genuine solve in the whole run - which
the **control** produced. That single pair is what drags the mean to -0.16 and stretches the interval's
lower bound to -0.49.

So the defensible claim is narrow and worth stating exactly: **on this task, at this budget, evolving the
instruction did not help, and the only arm that ever solved the task was the one with a fixed instruction.**
Six pairs with four ties cannot separate "no effect" from "harmful"; the direction of the one informative
pair is unfavourable, and that is not the same as a demonstrated harm.

## Cost, which the issue also asks about

The issue targets a token overhead of <= 10% of run tokens for meta-proposals. Measured by call share
rather than token receipts: the evolving arm spends **2 of its 6 model calls (33%)** on proposing
instructions. Because each isolated `claude -p` call pays a fixed ~31.6k-token cache creation regardless of
prompt size, call share is a good proxy for token share here - so the overhead is roughly **33%, over three
times the target**. Wall clock agrees: a median of 80 s per evolving arm against 44 s per fixed arm.

Stated as an estimate rather than a receipt, because the driver records cost units and calls, not tokens.

## What this is not

- **Not a verdict on meta-prompt evolution.** One task, one model, six pairs, five evaluations per arm.
  AlphaEvolve credits the component in ablations on far larger budgets; nothing here contradicts that.
- **Not a saturated endpoint.** This is the distinction from the #167 feedback ablation, where every arm
  scored zero: here a control arm reached 1.0, so the task demonstrably had headroom and the comparison
  could have shown a gain.
- **Not a measurement of the instruction's content.** The driver records how many instructions were
  proposed and credited, not which wording won. An archive dump would be the next thing to add.

## Two findings that preceded the measurement

- **Meta-prompt evolution is unreachable from the CLI.** `RunCommand` has no run-file field for it, which
  is why this is a C# driver.
- **Nothing shipped credits the instruction source.** `IProgramInstructionSource`'s own documentation says
  "whoever owns the run tells the source what the programs achieved", and no shipped type is that owner;
  `MetaPromptCredit` in the driver is.
