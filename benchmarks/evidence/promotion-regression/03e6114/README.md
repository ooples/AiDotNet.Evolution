# US-25 (#43): no promoted winner regresses the incumbent

`sweep.json` is written by the sweep that asserts it -
`EvolutionDeploymentLifecycleTests.No_candidate_that_regresses_the_incumbent_is_ever_promoted`, run with
`EVOLUTION_PROMOTION_EVIDENCE` naming the output path - at main `03e6114`. No model calls.

**0 of 7 promoted.** Seven candidate qualities against an incumbent at 1.0, the objective maximizing, so
every candidate below it is a regression:

| candidate quality | outcome | activated |
| ---: | --- | --- |
| 0.999 | `InsufficientImprovement` | no |
| 0.95 | `InsufficientImprovement` | no |
| 0.9 | `InsufficientImprovement` | no |
| 0.75 | `InsufficientImprovement` | no |
| 0.5 | `InsufficientImprovement` | no |
| 0.25 | `InsufficientImprovement` | no |
| 0.0 | `InsufficientImprovement` | no |

The sweep runs from a hair's breadth below the incumbent to a total collapse, because a gate that only
catches the obvious regression is not a gate.

## Why this file exists at all

The number was already asserted, and that is not the same as being checkable. Until now US-25's metric
read **0 of 7** in the issue body and in an `Assert.Empty`, with nothing committed - which is exactly the
class the evidence census counts as `Unsupported`: a claim whose numbers exist nowhere a reader can reach.

So the sweep emits its own rows rather than having them transcribed from a passing run. Emission is off
unless `EVOLUTION_PROMOTION_EVIDENCE` names a path, so an ordinary test run writes nothing - checked, not
assumed: the full Deployment suite was run without the variable and created no file.

## What this does not show

**Not that promotion works** - that `0 of 7` would also be what a lifecycle that promotes nothing at all
reports. Two sibling tests supply the control:
`A_candidate_that_improves_the_incumbent_is_promoted` (a better candidate *is* activated) and
`A_candidate_that_merely_matches_the_incumbent_is_not_promoted_either` (the boundary). The three are only
meaningful together, and none of them is in this file.

Not a held-out validation campaign either. This is the lifecycle's decision path over authored
measurements; the real-workload half of US-25 belongs to the pilot (#116).
