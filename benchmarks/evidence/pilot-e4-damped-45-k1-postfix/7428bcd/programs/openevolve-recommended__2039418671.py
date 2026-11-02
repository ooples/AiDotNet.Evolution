"""Seed for the symbolic-regression pilot task: predict a constant.

Deliberately the weakest honest candidate. A seed that already fits would leave the arms nothing to
differ on, and a seed that crashes would make every arm's first evaluation a failure rather than a
measurement. `predict` is the only contract: it takes the task's variables and returns a number.
"""


import math


def predict(*xs):
    t = xs[0]
    # Damped oscillator: A * exp(-gamma * t) * cos(omega * t + phi)
    A = 1.0
    gamma = 0.3
    omega = 2.0
    phi = -math.pi / 2
    return A * math.exp(-gamma * t) * math.cos(omega * t + phi)
