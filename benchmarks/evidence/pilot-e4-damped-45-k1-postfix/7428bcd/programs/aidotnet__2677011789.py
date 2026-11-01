"""Damped oscillator model: exp(-0.3 t) * sin(2 t).

`predict` is the only contract: it takes the task's variables and returns a number.
"""


import math


def predict(*xs):
    t = float(xs[0])
    decay = 0.3  # amplitude decay rate
    omega = 2.0  # angular frequency
    return float(math.exp(-decay * t) * math.sin(omega * t))
