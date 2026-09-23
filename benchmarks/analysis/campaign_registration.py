"""Hash-frozen campaign pre-registration and a byte-reproducible gate analysis (V1-05).

Builds on the US-04 tooling (`analyze.percentile`, `design.digest`/`write_new`): a
registration fixes, before launch, every choice a result could otherwise be tuned by —
arms, the primary endpoint per family x OpenEvolve config, budget, models, paired seeds,
sealed test-partition hashes, the failure value, bootstrap settings, Holm correction, the
fixed-N stopping rule and the decision rule. The analysis then regenerates the report from
raw evidence alone, deterministically, and stamps it with the registration hash.

Decision rule (strict superiority, ties fail): for each family x config, the paired seed
difference (ours minus OpenEvolve, oriented so positive favours ours) gets a one-sided
bootstrap p-value; Holm's step-down across all family x config endpoints must reject at
FamilywiseAlpha for EVERY endpoint. The AlphaEvolve comparison is separate (assumption A1):
match-or-beat on a strict majority of the pre-declared published problems.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import random
import secrets
import statistics

from analyze import percentile, require
from design import digest, write_new

SCHEMA = "evolution-campaign-registration-v1"
REPORT_SCHEMA = "evolution-campaign-report-v1"
CONFIGS = ("recommended", "matched")
OURS = "aidotnet"


def _hex64(value):
    return isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdef" for c in value)


def validate(spec):
    require(isinstance(spec, dict), "Registration spec must be an object.")
    keys = {"Campaign", "Families", "Budget", "Models", "SeedCount", "BootstrapSamples", "BootstrapSeed",
            "FamilywiseAlpha", "TestPartitionHashes", "Published"}
    require(set(spec) == keys, f"Registration spec fields must be exactly {sorted(keys)}.")
    require(isinstance(spec["Campaign"], str) and 0 < len(spec["Campaign"]) <= 100, "Name the campaign.")
    families = spec["Families"]
    require(isinstance(families, list) and families, "Declare at least one family.")
    seen = set()
    for family in families:
        require(isinstance(family, dict) and set(family) == {"Family", "Configs", "Endpoint"}, "Invalid family entry.")
        require(isinstance(family["Family"], str) and family["Family"] not in seen, "Families must be distinct names.")
        seen.add(family["Family"])
        require(family["Configs"] == [c for c in CONFIGS if c in family["Configs"]] and family["Configs"],
                "Configs must be a non-empty ordered subset of recommended, matched.")
        endpoint = family["Endpoint"]
        require(isinstance(endpoint, dict) and set(endpoint) == {"Metric", "Direction", "FailureValue"}
                and isinstance(endpoint["Metric"], str) and endpoint["Direction"] in ("maximize", "minimize")
                and type(endpoint["FailureValue"]) in (int, float), "Endpoint needs Metric, Direction and FailureValue.")
    budget = spec["Budget"]
    require(isinstance(budget, dict) and set(budget) == {"ModelTokens", "ModelCalls", "WallSeconds"} and
            all(type(v) is int and v > 0 for v in budget.values()), "Budget must fix tokens, calls and wall seconds.")
    require(isinstance(spec["Models"], list) and spec["Models"] and len(set(spec["Models"])) == len(spec["Models"]),
            "Declare the model set.")
    require(type(spec["SeedCount"]) is int and 2 <= spec["SeedCount"] <= 10_000, "SeedCount must be 2..10000.")
    require(type(spec["BootstrapSamples"]) is int and 1000 <= spec["BootstrapSamples"] <= 100_000
            and type(spec["BootstrapSeed"]) is int and 0 <= spec["BootstrapSeed"] < 2**32, "Invalid bootstrap settings.")
    require(type(spec["FamilywiseAlpha"]) is float and 0 < spec["FamilywiseAlpha"] <= 0.2, "FamilywiseAlpha must be in (0, 0.2].")
    require(isinstance(spec["TestPartitionHashes"], dict) and set(spec["TestPartitionHashes"]) == seen and
            all(_hex64(v) for v in spec["TestPartitionHashes"].values()), "Seal every family's test partition by SHA-256.")
    published = spec["Published"]
    require(published is None or (isinstance(published, dict) and set(published) == {"Family", "Problems"} and
            published["Family"] in seen and isinstance(published["Problems"], list) and published["Problems"] and
            len(set(published["Problems"])) == len(published["Problems"])),
            "Published must be null or name a registered family and its pre-declared problems.")


def register(spec, directory):
    """Freeze a campaign. Seeds are drawn here, never chosen by the caller."""
    validate(spec)
    seeds = []
    while len(seeds) < spec["SeedCount"]:
        candidate = secrets.randbits(32)
        if candidate not in seeds:
            seeds.append(candidate)
    registration = dict(Schema=SCHEMA, Spec=spec, Seeds=seeds, Arms=[OURS] + [f"openevolve-{c}" for c in CONFIGS],
                        StoppingRule="Fixed N = SeedCount per arm; no interim analysis, no extension, no re-run",
                        DecisionRule="Holm step-down over every family x config one-sided bootstrap p; all must reject; ties fail",
                        PublishedRule="Strict majority of pre-declared problems matched or beaten (assumption A1)")
    registration["RegistrationSha256"] = digest(registration)
    Path(directory).mkdir(parents=True, exist_ok=False)
    write_new(Path(directory) / "registration.json", registration)
    return registration


def load_registration(path):
    registration = json.loads(Path(path).read_text(encoding="utf-8"))
    claimed = registration.pop("RegistrationSha256", None)
    require(registration.get("Schema") == SCHEMA and claimed == digest(registration), "Registration was changed after freezing.")
    validate(registration["Spec"])
    registration["RegistrationSha256"] = claimed
    return registration


def holm(pvalues, alpha):
    """Holm step-down: (adjusted p, rejected) per input, in input order."""
    order = sorted(range(len(pvalues)), key=lambda i: (pvalues[i], i))
    adjusted, running = [0.0] * len(pvalues), 0.0
    for rank, index in enumerate(order):
        running = max(running, min(1.0, (len(pvalues) - rank) * pvalues[index]))
        adjusted[index] = running
    return [(value, value <= alpha) for value in adjusted]


def _bootstrap(differences, samples, seed):
    rng = random.Random(seed)
    n = len(differences)
    means = sorted(statistics.fmean(differences[rng.randrange(n)] for _ in range(n)) for _ in range(samples))
    # One-sided p for "ours better": share of resampled means at or below zero (ties fail).
    p = (sum(mean <= 0 for mean in means) + 1) / (samples + 1)
    return means, p


def analyze(registration, runs):
    """Report from raw runs: [{Family, Arm, Seed, Status, Value}], one per registered cell."""
    spec, seeds = registration["Spec"], registration["Seeds"]
    families = {f["Family"]: f for f in spec["Families"]}
    expected = {(f["Family"], arm, seed) for f in spec["Families"]
                for arm in [OURS] + [f"openevolve-{c}" for c in f["Configs"]] for seed in seeds}
    cells = {}
    for run in runs:
        key = (run.get("Family"), run.get("Arm"), run.get("Seed"))
        require(key in expected and key not in cells, f"Unregistered or duplicate run {key}.")
        require(run.get("Status") in ("completed", "failed"), f"Run {key} has no terminal status.")
        require(run["Status"] == "failed" or type(run.get("Value")) in (int, float), f"Completed run {key} lacks a value.")
        cells[key] = run
    missing = sorted(expected - set(cells))
    require(not missing, f"Registered runs are missing: {missing[:5]}")
    endpoints = []
    for family in spec["Families"]:
        endpoint, sign = family["Endpoint"], 1 if family["Endpoint"]["Direction"] == "maximize" else -1
        value = lambda run: endpoint["FailureValue"] if run["Status"] == "failed" else run["Value"]
        for config in family["Configs"]:
            index = len(endpoints)
            differences = [sign * (value(cells[(family["Family"], OURS, s)]) -
                                   value(cells[(family["Family"], f"openevolve-{config}", s)])) for s in seeds]
            means, p = _bootstrap(differences, spec["BootstrapSamples"], spec["BootstrapSeed"] + index)
            endpoints.append(dict(Family=family["Family"], Config=config, Metric=endpoint["Metric"],
                                  Direction=endpoint["Direction"], N=len(differences),
                                  MeanDifference=statistics.fmean(differences),
                                  Interval95=[percentile(means, 0.025), percentile(means, 0.975)], OneSidedP=p,
                                  Failures={arm: sum(cells[(family["Family"], arm, s)]["Status"] == "failed" for s in seeds)
                                            for arm in (OURS, f"openevolve-{config}")}))
    for endpoint, (adjusted, rejected) in zip(endpoints, holm([e["OneSidedP"] for e in endpoints], spec["FamilywiseAlpha"])):
        endpoint.update(HolmAdjustedP=adjusted, Superior=rejected)
    report = dict(Schema=REPORT_SCHEMA, Campaign=spec["Campaign"], RegistrationSha256=registration["RegistrationSha256"],
                  RawSha256=digest(sorted(runs, key=lambda r: (r["Family"], r["Arm"], r["Seed"]))),
                  FamilywiseAlpha=spec["FamilywiseAlpha"], Endpoints=endpoints,
                  OpenEvolveGate=all(e["Superior"] for e in endpoints),
                  IntervalNote="Interval95 is descriptive and unadjusted; the decision is Holm on one-sided bootstrap p")
    return report


def published_gate(registration, results):
    """AlphaEvolve rule: {problem: {"Ours": v, "Published": v, "Direction": d}} for every declared problem."""
    published = registration["Spec"]["Published"]
    require(published is not None, "This campaign registered no published comparison.")
    require(set(results) == set(published["Problems"]), "Results must cover exactly the declared problems.")
    met = sorted(p for p, r in results.items()
                 if (r["Ours"] >= r["Published"]) == (r["Direction"] == "maximize") or r["Ours"] == r["Published"])
    return dict(Problems=len(results), MatchedOrBeaten=len(met), Met=met, Gate=len(met) * 2 > len(results))


def render(report):
    """Deterministic bytes: the regeneration check compares exactly this."""
    return (json.dumps(report, indent=2, sort_keys=True, allow_nan=False) + "\n").encode("utf-8")


def check(registration_path, raw_path, report_path):
    """Regenerate from raw evidence; returns the byte difference count (0 = reproduced)."""
    registration = load_registration(registration_path)
    regenerated = render(analyze(registration, json.loads(Path(raw_path).read_text(encoding="utf-8"))))
    committed = Path(report_path).read_bytes()
    return 0 if regenerated == committed else max(len(regenerated), len(committed))


def lint(report_path, registration_path):
    """A report must carry the hash of the registration it claims."""
    report = json.loads(Path(report_path).read_text(encoding="utf-8"))
    return report.get("RegistrationSha256") == load_registration(registration_path)["RegistrationSha256"]


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    freeze = commands.add_parser("register")
    freeze.add_argument("--spec", required=True)
    freeze.add_argument("--directory", required=True)
    run = commands.add_parser("analyze")
    run.add_argument("--registration", required=True)
    run.add_argument("--raw", required=True)
    run.add_argument("--output", required=True)
    verify = commands.add_parser("check")
    verify.add_argument("--registration", required=True)
    verify.add_argument("--raw", required=True)
    verify.add_argument("--report", required=True)
    args = parser.parse_args()
    if args.command == "register":
        registration = register(json.loads(Path(args.spec).read_text(encoding="utf-8")), args.directory)
        print(registration["RegistrationSha256"])
    elif args.command == "analyze":
        report = analyze(load_registration(args.registration), json.loads(Path(args.raw).read_text(encoding="utf-8")))
        with Path(args.output).open("xb") as stream:
            stream.write(render(report))
        print(json.dumps({"OpenEvolveGate": report["OpenEvolveGate"], "RegistrationSha256": report["RegistrationSha256"]}))
    else:
        difference = check(args.registration, args.raw, args.report)
        lint_ok = lint(args.report, args.registration)
        print(json.dumps({"ReproductionDiffBytes": difference, "CarriesRegistrationHash": lint_ok}))
        raise SystemExit(0 if difference == 0 and lint_ok else 1)


if __name__ == "__main__":
    main()