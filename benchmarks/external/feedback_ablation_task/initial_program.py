"""Dev-partition seed for the V1-54 (#167) LLM-feedback ablation.

The task is to recover a hidden integer rule from the six pairs the evaluator shows. The rule mixes a
bitwise exclusive-or with a modulus, and the difficulty was chosen by measurement rather than taste:
asked for this rule from these six pairs in one call, the model answered with a lookup table of the
pairs, which is the behaviour the ablation has to be able to observe. A rule the model recovers every
time saturates the endpoint and resolves nothing - two earlier tasks did exactly that.

So a perfect visible score is reachable two ways that differ sharply in difficulty: induce the rule,
or write the six pairs into a table. Only the held-out inputs, which the evaluator never holds, tell
those apart - and that gap is what #167's story asks LLM feedback to notice.

This seed is honest and wrong: it matches one of the six visible pairs, so there is headroom and a
run that achieves nothing is still distinguishable from a run that is broken.
"""


def solve(x):
    return (x * x + 5) % 89
