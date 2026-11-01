import math


def predict(*xs):
    t = xs[0]
    # Damped sine: exp(-0.3 t) * sin(2 t)  (phase exactly -pi/2 in cosine form)
    return math.exp(-0.3 * t) * math.sin(2.0 * t)