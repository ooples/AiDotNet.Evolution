"""Dev-partition seed for the V1-54 (#167) LLM-feedback ablation.

The task is to recover a hidden integer rule from the six pairs the evaluator shows. The rule is a
cubic taken modulo a prime, which is deliberately hard to induce from six points and trivially easy
to memorise - so a perfect visible score is reachable both by solving the task and by writing the six
pairs into a lookup table, and only the held-out set the evaluator never holds tells those apart.
That gap is the one #167's story asks LLM feedback to notice.

This seed is honest and wrong: it matches one of the six visible pairs, so there is headroom and a
broken run is still distinguishable from a working one.
"""


def solve(x):
    return x * x + 2 * x + 1
