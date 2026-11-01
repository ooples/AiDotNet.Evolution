"""Damped oscillator: predict a number from t."""


import math


def predict(*xs):
    t = xs[0]
    return math.exp(-0.3 * t) * math.sin(2.0 * t)
