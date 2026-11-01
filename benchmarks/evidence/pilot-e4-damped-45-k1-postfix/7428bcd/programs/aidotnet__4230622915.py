"""Damped oscillator model for the symbolic-regression pilot task.

Predicts exp(-0.3 * t) * sin(2 * t), an exponentially decaying sinusoid
(decay rate 0.3, angular frequency 2.0, unit amplitude, zero phase).
`predict` is the only contract: it takes the task's variables and returns a number.
"""


import math


def predict(*xs):
    t = float(xs[0])
    return math.exp(-0.3 * t) * math.sin(2.0 * t)
