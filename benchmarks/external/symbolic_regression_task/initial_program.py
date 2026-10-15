"""Seed for the symbolic-regression pilot task: predict a constant.

Deliberately the weakest honest candidate. A seed that already fits would leave the arms nothing to
differ on, and a seed that crashes would make every arm's first evaluation a failure rather than a
measurement. `predict` is the only contract: it takes the task's variables and returns a number.
"""


def predict(*xs):
    return 0.0
