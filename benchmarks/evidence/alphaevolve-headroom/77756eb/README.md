# V1-04a (#124): how far a floor gets on AlphaEvolve's published constructions

`headroom.json` is the raw output of
[`benchmarks/external/alphaevolve_headroom.py`](../../../external/alphaevolve_headroom.py) at
`--iterations 20000`, branch head `77756eb`. **No model calls, no spend.** The floor is seeded
coordinate descent with an absolute step schedule; the objective is always the family module's own
independent verifier, the one whose self-test reproduces the published value, so nothing is scored by
the thing that produced it.

[`../../alphaevolve-math/manifest.json`](../../alphaevolve-math/manifest.json) transcribes 36
published problems and verifies all 36 - and says plainly that it is a **target list, not a result**:
nothing in this repository has ever claimed to reach one of those figures. This measures the distance
to them.

| problem | direction | published | floor | gap | budget |
| --- | --- | ---: | ---: | ---: | ---: |
| `c4-uncertainty-hermite` | minimize | 0.3521 | **0.353049** | **+0.27%** | 1,000 |
| `c5-erdos-overlap` | minimize | 0.380924 | 0.394977 | +3.69% | 20,000 |
| `c2-autocorrelation` | maximize | 0.8962 | 0.827462 | +7.67% | 20,000 |
| `c1-autocorrelation` | minimize | 1.5053 | 1.657840 | +10.13% | 20,000 |
| `c3prime-autocorrelation` | minimize | 1.4688 | 1.652699 | +12.52% | 20,000 |
| `c3-autocorrelation` | minimize | 1.4557 | 1.652699 | +13.53% | 20,000 |

Positive always means the floor is behind, whichever way the problem points. **The floor reaches none
of the six**, so on all six there is room above it - which is the precondition for a result on them
meaning anything, and it is the only thing this bundle establishes.

## c4-hermite is already a task that cannot carry a claim

0.27% is close enough that a search reaching the published 0.3521 would be demonstrating very
little: 1,000 iterations of coordinate descent, with no model and no tuning, gets there. This is the
same hazard `rational_gain` has in the symbolic-regression family, where a model-free floor closes
89.9% to 94.8% of the gap, and it is worth knowing **before** a campaign is sized rather than after.
The other five leave 3.7% to 13.5%.

## Scope is derived from the manifest, not written down

6 of the 36 are in scope; the other 30 each carry a reason, and the partition is checked against the
manifest so it cannot quietly stop covering it. The first version of this harness listed 13
exclusions against 36 problems - 18 tensor problems and both `c4` problems were missing from the
inventory entirely, and one of those two turned out to be **in scope**.

| group | count | why not |
| --- | ---: | --- |
| tensor decompositions | 16 | the candidate is three factor matrices over a ring, not a vector of reals |
| geometric constructions | 11 | point sets, triangles, hexagons and sphere centres, whose shape and feasibility differ per problem |
| `c6` sums and differences | 2 | the candidate is a set of **integers** with minimum 0; its verifier refuses a float vector, and a repair that rounded would change the problem |
| `c4-uncertainty-laguerre` | 1 | see below - its verifier does not check what it assumes |

## Two corrections that changed the numbers

**`c3` and `c3'` are the signed problems, and were being repaired back onto `c1`.** Their verifier
passes `nonnegative=False`; that is the entire difference between them and `c1`. Repaired to
non-negative, `conv(h, h) >= 0` makes all three peaks the same quantity, and all three returned
`1.657840481821565` to the last bit - one measurement reported against three different published
targets. With the free repair they separate, and `c3` is the harder problem it was supposed to be.

**`c3` and `c3'` still share a value, and that is a property of the problems.** `c3` reads
`abs(g.max())` and `c3'` reads `max |g|`, so they differ only on a candidate whose most negative
autocorrelation outweighs its largest positive one. At the floor's best candidate it does not, so one
number sits against two different targets - stated here rather than presented as two results.

**`c4-uncertainty-laguerre` is excluded for a measured reason.** `laguerre_bound` computes the bound
from a construction it *assumes* admissible and never checks that assumption, so a random vector of
twelve positive reals scores **0.101685** against a published **0.3216**. Minimising it rewards an
inadmissible candidate, and a search scored against it would manufacture a win over AlphaEvolve out
of an invalid construction. The exclusion is a claim about upstream, so
[`test_program_alphaevolve_headroom.py`](../../../external/test_program_alphaevolve_headroom.py)
measures it: the day that verifier gains an admissibility check, the test fails and the problem comes
back into scope.

## The floor is monotone in its budget

The step schedule halves every 4,000 iterations regardless of the budget, so an N-iteration run draws
exactly the prefix a longer run draws and takes the same steps at the same indices; acceptance is
greedy, so more iterations can only find a better candidate. That is asserted, not described. It
matters because the gap above is then a **lower bound** on the room above the floor: it cannot be
widened by choosing a budget that makes the floor look weak. With the schedule tied to the budget it
was not monotone, and raising it from 8,000 to 20,000 moved `c1` the wrong way.

`c4-uncertainty-hermite` runs at 1,000 iterations because its verifier is exact rational arithmetic
over Hermite polynomials and is some three orders of magnitude slower than the rest. The cap is
recorded per row, and the test refuses a cap that appears on any other problem without being stated.

## Reproduction

Two independent runs of this configuration on the same machine agreed on every figure above. The
20,000-iteration sweep takes roughly twelve minutes, so the committed test guards the cheap half
instead: every published value is checked against the manifest, every gap is re-derived from the two
numbers it summarises, the partition is required to cover all 36, and the monotonicity and
feasibility properties are re-measured at small budgets.

`np.convolve`, `np.correlate` and `np.roots` put the last bits of these figures at the mercy of the
BLAS a platform ships, so the test pins **invariants and re-derivations** rather than digits - the
lesson the symbolic-regression bundle learned when 3 of its 9 seventeen-digit seals failed to rebuild
on Linux.

## What this is not

Not a result for this engine. **The engine has never been run on these problems**; the row this
bundle supports in
[`docs/COMPETITOR_COMPARISON.md`](../../../../docs/COMPETITOR_COMPARISON.md) therefore reads
`unmeasured`, and the comparison's lint refuses to let a floor study read as a win.