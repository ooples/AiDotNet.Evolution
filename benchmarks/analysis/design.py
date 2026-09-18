"""Fixed-sample planning and one-use execution for a trusted campaign controller.

No optional stopping or release promotion. Filesystem custody remains external.
"""
import argparse
import copy
import hashlib
import json
import math
from pathlib import Path
import secrets
import statistics

from analyze import analyze, finite, integer, load_json, require, validate_plan


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()


def write_new(path, value):
    with Path(path).open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.write("\n")


def controller_hashes():
    return {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
            for name in ("design.py", "analyze.py", "presentation.py")}


def plan_design(campaign, analysis_plan, *, effect, power=0.8, alpha=0.05, maximum_runs=1000):
    """Effect is the anticipated task-balanced utility difference versus zero.

    Hoeffding applied per fixed task to [-1,1] paired differences, then union
    bounded across tasks/comparisons. Cross-task independence is NOT required.
    Independent search runs within each fixed task ARE required.
    """
    require(finite(effect) and 1e-6 <= effect <= 1, "Effect must be in [1e-6,1].")
    require(finite(power) and 0.5 < power <= 1 - 1e-9 and finite(alpha) and 1e-9 <= alpha <= 0.2, "Invalid power/alpha.")
    require(integer(maximum_runs, 2, 1000), "Run limit must be 2..1000 per task/method.")
    report = analyze(campaign, analysis_plan)
    require(analysis_plan["SeedCount"] >= 2, "At least two paired pilot search runs are needed.")
    indexed = {(r["Task"], r["Method"], r["Seed"]): r["Utility"] for r in report["Runs"]}
    primary = analysis_plan["PrimaryMethod"]
    variances = []
    for task in analysis_plan["Tasks"]:
        for comparator in analysis_plan["Comparators"]:
            differences = [indexed[task["Name"], primary, seed] - indexed[task["Name"], comparator, seed]
                           for seed in range(analysis_plan["SeedCount"])]
            variances.append(dict(Task=task["Name"], Comparator=comparator,
                                  PairedVariance=statistics.variance(differences)))
    tasks, comparisons = len(analysis_plan["Tasks"]), len(analysis_plan["Comparators"])
    # Normal approximation is pilot-dependent advice only; max task variance
    # avoids treating different tasks as extra independent seed observations.
    normal = statistics.NormalDist()
    z = normal.inv_cdf(1 - alpha / comparisons) + normal.inv_cdf(power)
    normal_n = max(2, math.ceil(z * z * max(r["PairedVariance"] for r in variances) / effect**2))
    # If each task is estimated within h, their equal-weight mean is within h.
    # Reject above h_alpha. At a true aggregate effect >= effect, a miss is
    # bounded by beta when h_alpha + h_beta <= effect.
    required = math.ceil(2 * (math.sqrt(math.log(tasks * comparisons / alpha))
                              + math.sqrt(math.log(tasks / (1 - power))))**2 / effect**2)
    n = max(2, required)
    fixed = copy.deepcopy(analysis_plan)
    fixed.update(SeedCount=n, ConfidenceLevel=1 - alpha, ResampleTasks=False)
    reason = None
    try:
        require(n <= maximum_runs, "Required fixed sample exceeds the declared run cap; do not truncate it or claim sufficient power.")
        validate_plan(fixed)
    except ValueError as error:
        reason = str(error)
    return dict(Schema="evolution-fixed-design-v1", Feasible=reason is None, InfeasibleReason=reason,
                PilotCampaignSha256=digest(campaign), PilotPlanSha256=digest(analysis_plan),
                PilotSeeds=list(range(analysis_plan["SeedCount"])), PilotVariance=variances,
                PilotFailedOrMissing=sum(r["Status"] != "completed" for r in report["Runs"]),
                AnticipatedEffect=effect, FamilywiseAlpha=alpha, TargetPower=power,
                NormalApproximationRuns=normal_n, RequiredRunsPerTaskMethod=n, MaximumRuns=maximum_runs,
                SelectedDesign="fixed-task Hoeffding union bound", StoppingRule="one fixed sample; no extension after outcomes",
                FixedAnalysisPlan=fixed, ConfirmatoryEligible=False,
                Assumptions=["Fresh independent paired search runs within each predeclared fixed task; timing repetitions are not runs.",
                             "Utilities are bounded in [0,1], failures/missing runs have zero utility, and scales stay fixed.",
                             "Task suite is fixed; this bound does not infer generalization to unseen task families.",
                             "Pilot normal approximation can fail for small/degenerate pilots; it is never used as the selected guarantee.",
                             "The bound concerns detection versus zero at the anticipated true effect, not proof of a minimum practical gain.",
                             "Private holdout custody and actual evaluator correctness/isolation require independent verification."])


def freeze(directory, design, runtime_contract):
    require(isinstance(design, dict) and design.get("Schema") == "evolution-fixed-design-v1" and design.get("Feasible") is True,
            "Only a feasible design may be frozen.")
    # Recompute critical planning quantities instead of trusting caller-edited n.
    fixed = design["FixedAnalysisPlan"]
    validate_plan(fixed)
    tasks, comparisons = len(fixed["Tasks"]), len(fixed["Comparators"])
    alpha, power, effect = design["FamilywiseAlpha"], design["TargetPower"], design["AnticipatedEffect"]
    require(finite(alpha) and 1e-9 <= alpha <= .2 and finite(power) and .5 < power <= 1 - 1e-9
            and finite(effect) and 1e-6 <= effect <= 1, "Invalid design parameters.")
    n = max(2, math.ceil(2 * (math.sqrt(math.log(tasks * comparisons / alpha))
                              + math.sqrt(math.log(tasks / (1 - power))))**2 / effect**2))
    require(fixed["SeedCount"] == design["RequiredRunsPerTaskMethod"] == n <= design["MaximumRuns"]
            and fixed["ConfidenceLevel"] == 1 - alpha and fixed["ResampleTasks"] is False, "Design was changed after planning.")
    require(isinstance(runtime_contract, dict) and runtime_contract and
            all(isinstance(k, str) and isinstance(v, str) and len(v) == 64
                and all(c in "0123456789abcdef" for c in v) for k, v in runtime_contract.items()),
            "Runtime contract requires named SHA256 artifact identities.")
    directory = Path(directory)
    directory.mkdir(parents=False, exist_ok=False)
    excluded = set(design["PilotSeeds"])
    seeds = []
    while len(seeds) < n:
        candidate = secrets.randbits(32)
        if candidate not in excluded:
            excluded.add(candidate)
            seeds.append(candidate)
    registration = dict(Schema="evolution-analysis-registration-v1", Design=design,
                        RuntimeContract=runtime_contract, SearchSeeds=seeds, ControllerHashes=controller_hashes())
    registration["RegistrationSha256"] = digest(registration)
    write_new(directory / "registration.json", registration)
    return registration


def execute(directory, expected_registration_hash, runtime_contract, runner):
    """runner(schedule) must return the numeric campaign with actual SearchSeeds.

    Dispatch is claimed before runner entry. Crashes/failures cannot be retried
    under this registration. Missing rows retain zero utility and unknown work.
    Caller must keep expected_registration_hash outside the writable directory.
    """
    directory = Path(directory)
    registration, _ = load_json(directory / "registration.json", 1024 * 1024)
    identity = registration.pop("RegistrationSha256")
    require(identity == expected_registration_hash == digest(registration), "Registration changed or external identity differs.")
    require(runtime_contract == registration["RuntimeContract"] and
            registration["ControllerHashes"] == controller_hashes(), "Runtime/controller changed after freeze.")
    design = registration["Design"]
    plan = design["FixedAnalysisPlan"]
    seeds = registration["SearchSeeds"]
    require(len(seeds) == plan["SeedCount"] and len(set(seeds)) == len(seeds)
            and not set(seeds).intersection(design["PilotSeeds"]), "Fixed seed schedule differs or overlaps the pilot.")
    write_new(directory / "started.json", {"RegistrationSha256": identity, "Status": "claimed-before-dispatch"})
    schedule = dict(RegistrationSha256=identity, AnalysisPlan=copy.deepcopy(plan), SearchSeeds=list(seeds))
    raw = None
    error = None
    try:
        raw = runner(schedule)
        write_new(directory / "raw-campaign.json", raw)
        require(raw.get("RegistrationSha256") == identity, "Runner did not bind the frozen registration.")
        normalized = copy.deepcopy(raw)
        mapping = {seed: index for index, seed in enumerate(seeds)}
        for row in normalized["Runs"]:
            require(type(row["Seed"]) is int and row["Seed"] in mapping, "Run used an unplanned or pilot seed.")
            row["Seed"] = mapping[row["Seed"]]
        report = analyze(normalized, plan)
        width = math.sqrt(2 * math.log(len(plan["Tasks"]) * len(plan["Comparators"]) / design["FamilywiseAlpha"]) / len(seeds))
        report["FixedDesign"] = dict(RegistrationSha256=identity, SearchSeeds=seeds, LowerBoundRadius=width,
                                    NoExtension=True, ConfirmatoryEligible=False,
                                    Comparisons=[dict(Comparator=r["Comparator"], LowerBound=max(-1, r["MeanPairedUtilityDifference"] - width),
                                                      AboveZero=r["MeanPairedUtilityDifference"] > width) for r in report["Comparisons"]])
        report["Purpose"] = "prospective-fixed-development"
        report["Caveats"][0] = "Schedule frozen before this dispatch; development tasks are not sealed holdouts and no release promotion follows."
        write_new(directory / "report.json", report)
        from analyze import markdown
        from presentation import html_report
        (directory / "report.md").write_text(markdown(report), encoding="utf-8")
        (directory / "report.html").write_text(html_report(report), encoding="utf-8")
        return report
    except Exception as exception:
        error = type(exception).__name__ + ": " + str(exception)[:500]
        write_new(directory / "failed-schedule.json", dict(Error=error, UnknownWork=True,
                  Runs=[dict(Task=task["Name"], Method=method, Seed=seed, Status="unverified-after-controller-failure", Utility=0, Work=None)
                        for task in plan["Tasks"] for method in plan["Methods"] for seed in seeds]))
        raise
    finally:
        write_new(directory / "terminal.json", dict(RegistrationSha256=identity, Error=error,
                  Status="failed-no-retry" if error else "reported", UnknownWork=error is not None,
                  ScheduledRuns=len(plan["Tasks"]) * len(plan["Methods"]) * len(seeds)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pilot", required=True, type=Path)
    parser.add_argument("--analysis-plan", required=True, type=Path)
    parser.add_argument("--effect", required=True, type=float)
    parser.add_argument("--power", type=float, default=.8)
    parser.add_argument("--alpha", type=float, default=.05)
    parser.add_argument("--maximum-runs", type=int, default=1000)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    campaign, _ = load_json(args.pilot, 256 * 1024 * 1024)
    plan, _ = load_json(args.analysis_plan, 1024 * 1024)
    result = plan_design(campaign, plan, effect=args.effect, power=args.power, alpha=args.alpha, maximum_runs=args.maximum_runs)
    write_new(args.output, result)
    print(json.dumps({"feasible": result["Feasible"], "required_runs": result["RequiredRunsPerTaskMethod"]}))
    return 0 if result["Feasible"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
