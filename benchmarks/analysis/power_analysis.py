"""V1-40 (#121): N per family x config for a target CI width, from the pilot's measured variance.

`design.plan_design` answers a different question - the fixed N for an *anticipated* effect at a
target power, chosen before anything has run. #121 asks the one only a pilot can answer: given the
variance the pilot actually measured, how many paired seeds does the gate campaign need for the CI
width it declared? That is also #116's second criterion ("per-endpoint variance and required N for
the target CI width are reported").

**Two estimators, reported side by side, because one number here is a decision nobody can check.**

- SCALED. A bootstrap interval of a mean narrows as 1/sqrt(n), so the pilot's own observed half
  width at its own N gives `n * (observed / target)^2`. It assumes only that scaling, not a
  distribution, and it inherits whatever the pilot's seeds actually did - including skew.
- NORMAL. The textbook `(z * sd / target)^2` from the paired differences' standard deviation. It
  assumes approximate normality of the mean, which six seeds cannot demonstrate.

`RequiredN` is the larger of the two, and `Agreement` is their ratio: close to 1 means the two
readings agree and the number can be used; far from 1 means the pilot's distribution is doing
something the normal estimator does not see, and the campaign should take the larger N and say so.
A pilot whose endpoint has no spread at all (every paired difference identical) reports
`RequiredN: 2` with `Degenerate: true` rather than 1, because a CI needs two observations and an
endpoint with no variance has not been measured so much as repeated.
"""
from __future__ import annotations

import math
import statistics

from analyze import require
from campaign_registration import OURS

MINIMUM_SEEDS = 2
MAXIMUM_SEEDS = 10_000


def paired_differences(registration, runs, family, config):
    """The per-seed paired differences `analyze` works from, oriented so positive favours ours."""
    endpoint = family["Endpoint"]
    sign = 1 if endpoint["Direction"] == "maximize" else -1
    cells = {(row["Family"], row["Arm"], row["Seed"]): row for row in runs}
    value = lambda row: endpoint["FailureValue"] if row["Status"] == "failed" else row["Value"]
    return [sign * (value(cells[(family["Family"], OURS, seed)])
                    - value(cells[(family["Family"], f"openevolve-{config}", seed)]))
            for seed in registration["Seeds"]]


def _bounded(count):
    require(count <= MAXIMUM_SEEDS,
            "Required N exceeds the cap; a campaign that large is a plan to change, not to run")
    return max(MINIMUM_SEEDS, int(count))


def endpoint_requirement(endpoint, differences, *, half_width, confidence=0.95):
    """Both estimators for one endpoint, plus the spread each reads them from."""
    require(isinstance(half_width, (int, float)) and math.isfinite(half_width) and half_width > 0,
            "The target CI half width must be a positive number")
    # Refused rather than approximated, because the scaled estimator can only scale the interval
    # `analyze` produced, and that interval is `Interval95`. Accepting 0.9 here would have scaled a
    # 95% interval while the normal estimator used a 90% z, and `RequiredN` would be the larger of
    # two numbers measured at different levels - review caught that.
    require(confidence == 0.95,
            "Only 0.95 is supported: the pilot report carries a 95% interval, and mixing levels "
            "between the two estimators would make RequiredN the max of incomparable numbers")
    count = endpoint["N"]
    require(count >= MINIMUM_SEEDS and len(differences) == count,
            "The endpoint's N must match the differences it was computed from")

    low, high = endpoint["Interval95"]
    observed = (high - low) / 2.0
    deviation = statistics.stdev(differences) if count > 1 else 0.0
    degenerate = observed <= 0.0 and deviation <= 0.0
    scaled = MINIMUM_SEEDS if degenerate else _bounded(math.ceil(count * (observed / half_width) ** 2))
    z = statistics.NormalDist().inv_cdf(0.5 + confidence / 2.0)
    normal = MINIMUM_SEEDS if degenerate else _bounded(math.ceil((z * deviation / half_width) ** 2))
    return {
        "Family": endpoint["Family"],
        "Config": endpoint["Config"],
        "PilotN": count,
        "MeanDifference": endpoint["MeanDifference"],
        "StandardDeviation": deviation,
        # #116 asks for the variance, so it is reported rather than left for a reader to square.
        "Variance": deviation ** 2,
        "ObservedHalfWidth": observed,
        "TargetHalfWidth": float(half_width),
        "ScaledRequiredN": scaled,
        "NormalRequiredN": normal,
        # The larger of the two: a campaign that takes the smaller one has chosen the estimator that
        # flattered it, and the pilot cannot say which estimator is right from six seeds.
        "RequiredN": max(scaled, normal),
        "Agreement": None if min(scaled, normal) == 0 else max(scaled, normal) / min(scaled, normal),
        "Degenerate": degenerate,
        # Already wide enough: the pilot's own interval meets the target, so the pilot's N would do.
        "PilotAlreadyMeetsTarget": not degenerate and observed <= half_width,
    }


def analyse(registration, runs, report, *, half_width, confidence=0.95):
    """Per-endpoint variance and required N, in the order `analyze` reported the endpoints."""
    families = {family["Family"]: family for family in registration["Spec"]["Families"]}
    rows = []
    for endpoint in report["Endpoints"]:
        family = families[endpoint["Family"]]
        differences = paired_differences(registration, runs, family, endpoint["Config"])
        rows.append(endpoint_requirement(endpoint, differences, half_width=half_width, confidence=confidence))
    return {
        "Schema": "evolution-power-analysis-v1",
        "RegistrationSha256": registration["RegistrationSha256"],
        "TargetHalfWidth": float(half_width),
        "Confidence": confidence,
        "Endpoints": rows,
        # The campaign is sized by its widest endpoint: every family x config is tested, and Holm
        # requires all of them to reject, so the one needing most seeds sets the budget.
        "RequiredN": max(row["RequiredN"] for row in rows) if rows else MINIMUM_SEEDS,
    }


def lines(analysis):
    """One line per endpoint, so the variance is as visible as the number derived from it."""
    out = []
    for row in analysis["Endpoints"]:
        out.append("%-22s %-12s n=%-3d var=%.4f sd=%.4f observed=%.4f target=%.4f required=%-5d "
                   "(scaled %d, normal %d)%s"
                   % (row["Family"], row["Config"], row["PilotN"], row["Variance"],
                      row["StandardDeviation"], row["ObservedHalfWidth"], row["TargetHalfWidth"],
                      row["RequiredN"], row["ScaledRequiredN"], row["NormalRequiredN"],
                      " DEGENERATE" if row["Degenerate"] else ""))
    out.append("campaign N = %d (the widest endpoint sets it)" % analysis["RequiredN"])
    return out
