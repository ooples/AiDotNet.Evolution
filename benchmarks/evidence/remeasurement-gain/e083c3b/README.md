# US-06 (#24): re-measuring unchanged code must not manufacture a gain

Four files, one per arm, written by the sweep that asserts them -
`RemeasurementGainSuiteTests`, run with `EVOLUTION_REMEASUREMENT_EVIDENCE` naming this directory - at
main `e083c3b`. No model calls.

| `DeduplicationCapacity` | evaluation cache | improvements | evaluator calls | best quality |
| ---: | --- | ---: | ---: | ---: |
| **0** (shipped default) | off | **0** | 2 | 0.2 |
| **0** (shipped default) | on | **0** | 2 | 0.2 |
| **1** (bounded) | off | **8** | 10 | **1.0** |
| **1** (bounded) | on | **8** | 10 | **1.0** |

At the shipped default the seen set never forgets, so a proposal whose identity was already evaluated is
refused before the evaluator runs: the two distinct identities are measured exactly once each, and the
evaluator here reports a better number on *every* call, so a single extra sample would have shown up as a
gain. None does.

## The number the metric did not carry

`8 of 10` was in a code comment above an `improvements > 0` assertion, so the metric's own figure was not
reachable by a reader - the class the evidence census calls `Unsupported`. It is now emitted by the arms
that assert it.

And the arms carry one thing the comment did not say: **the best quality rises from 0.2 to 1.0**. The
bounded capacity does not merely record eight spurious improvements, it ends the run claiming a headline
five times better than the honest one.

**The cache cannot help, with it on or off.** The same eviction that lets a forgotten identity be
re-proposed has already discarded its cached measurement, which is why both capacity-1 rows are
identical. That locates the protection in deduplication, not in the cache.

## Whose decision this is

Whether the trade is acceptable belongs to the owner, not to this card. `DeduplicationCapacity` exists to
bound memory on a long run, and the alternative - retaining cache entries for genomes the run has
forgotten - defeats the bound it exists to provide. The arms pin the behaviour so the trade-off is
visible rather than discovered, and
[`EvolutionEngineOptions.DeduplicationCapacity`](../../../../src/AiDotNet.Evolution/Configuration/EvolutionEngineOptions.cs)
warns where the value is chosen.

## What this does not show

Not a claim about any real workload: the evaluator is authored to improve on every call, which is what
makes a manufactured gain visible at all. A real noisy evaluator would produce a smaller and
less regular effect, and the shipped default would still refuse the re-proposal.
