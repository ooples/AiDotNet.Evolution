"""Damped oscillator model: exp(-k t) * sin(w t)."""


import math


def predict(*xs):
    t = xs[0]
    # Damped oscillator: A * exp(-k t) * cos(w t + phi)
    A, k, w, phi = 1.0, 0.30, 2.0, -math.pi / 2
    return A * math.exp(-k * t) * math.cos(w * t + phi)
