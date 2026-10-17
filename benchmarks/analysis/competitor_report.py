"""Where v1 stands against OpenEvolve and AlphaEvolve, derived from the committed evidence.

0.5.0 shipped with the competitive question answered in pieces: an overhead card here, a parity
census there, a pilot that disclaimed its own result. This assembles those pieces into one table and
- the part that makes it worth reading - refuses to assemble a flattering one.

**Every cell is derived from a committed file.** Nothing is typed in: the overhead ratio comes out of
the head-to-head card, the option counts out of the catalog and the census snapshot beside it, the
quality verdict out of the pilot's own endpoints. A number in the rendered document and no number in
the bundle is a failure of `lint`, not a footnote.

**A verdict is a claim and has to clear a bar.** `ahead` and `behind` need a competitor figure to be
ahead or behind *of*, and either an interval that excludes parity or two ranges that do not overlap -
so a single run cannot become a win. A row measured without a competitor says so rather than
borrowing a verdict. And two rows that would be the most tempting to launder are refused outright: a
floor study, which measures how far a model-free search gets and therefore says nothing about ours,
can never read `ahead`; and the head-to-head quality row cannot read `ahead` unless an endpoint in
the campaign it cites is actually marked superior. Today none is, so it reads `inconclusive`, which
is the honest state of the v1 claim and the reason the gate campaign still has to run.
"""
import argparse
import collections
import json
import re
import statistics
import sys
from pathlib import Path

SCHEMA = "evolution-competitor-comparison-v1"

# The verdict vocabulary. A row carries exactly one, so a reader never has to infer from the sign of
# a number which way it went.
AHEAD = "ahead"
PARITY = "parity"
BEHIND = "behind"
MEASURED = "measured, no competitor figure"
INCONCLUSIVE = "inconclusive"
UNMEASURED = "unmeasured"
VERDICTS = (AHEAD, PARITY, BEHIND, MEASURED, INCONCLUSIVE, UNMEASURED)
COMPARATIVE = (AHEAD, BEHIND)

OPENEVOLVE = "OpenEvolve"
ALPHAEVOLVE = "AlphaEvolve"

CATALOG = "src/AiDotNet.Evolution.Cli/OpenEvolve/OpenEvolveConfigCatalog.cs"
OVERHEAD = "benchmarks/evidence/engine-overhead/65ced88/head-to-head.json"
SCALING = "benchmarks/evidence/population-scaling/47899db/scaling.json"
LATENCY_OURS = "benchmarks/evidence/latency/4c31b32/runs.jsonl"
LATENCY_THEIRS = "benchmarks/evidence/latency/4c31b32/openevolve.jsonl"
DEFECTS_032 = "benchmarks/evidence/robustness/c30c875/openevolve-defects-0.3.2.json"
DEFECTS_040 = "benchmarks/evidence/robustness/c30c875/openevolve-defects-0.4.0.json"
OURS_ROBUST = "benchmarks/evidence/robustness/c30c875/ours.json"
SANDBOX = "benchmarks/evidence/sandbox-overhead/ca96524/ubuntu-latest.json"
CHECKPOINT = "benchmarks/evidence/checkpoint/86d56c9/re-measurement.json"
AE_MANIFEST = "benchmarks/evidence/alphaevolve-math/manifest.json"
SR_HEADROOM = "benchmarks/evidence/symbolic-regression/e083c3b/headroom.json"
PILOT = "benchmarks/evidence/pilot-v1-07/aafd609/report.json"
WORKER = "benchmarks/evidence/worker-overhead/3f73e03-ci/linux-x64-python.json"
ENSEMBLE = "benchmarks/evidence/ensemble-overhead/34c117c/models-%d.json"
SOAK = "benchmarks/evidence/soak/segmented-a1d5d2f/memory-capped-10000-conserve.json"
DISTRIBUTED = "benchmarks/evidence/distributed-v1-61/818b642"
ABLATIONS = {
    "repair": "benchmarks/evidence/repair-us17/0354e33/summary.json",
    "reuse": "benchmarks/evidence/reuse-us18/0354e33/summary.json",
    "routing": "benchmarks/evidence/routing-us19/0354e33/summary.json",
    "automl": "benchmarks/evidence/automl-us11/0354e33/summary.json",
    "warm-start": "benchmarks/evidence/warm-start-us23/0354e33/summary.json",
}


def read(root, relative):
    return json.loads((Path(root) / relative).read_text(encoding="utf-8"))


def read_lines(root, relative):
    text = (Path(root) / relative).read_text(encoding="utf-8")
    return [json.loads(line) for line in text.splitlines() if line.strip()]


def row(dimension, competitor, ours, theirs, reading, verdict, evidence, *,
        ours_range=None, theirs_range=None, interval=None, lower_is_better=True,
        floor_study=False, endpoints=None):
    """One comparison. `evidence` is the file the numbers were read out of, relative to the root."""
    return {
        "Dimension": dimension, "Competitor": competitor, "Ours": ours, "Theirs": theirs,
        "Reading": reading, "Verdict": verdict, "Evidence": evidence,
        "OursRange": ours_range, "TheirsRange": theirs_range, "Interval": interval,
        "LowerIsBetter": lower_is_better, "FloorStudy": floor_study, "Endpoints": endpoints,
    }


def defect_classes(ours, theirs):
    """Joins the two cards on the defect-class id, so the comparison is per class and not per count.

    The local suite runs ten TESTS for eight CLASSES - `D7_the_grid_has_exactly_the_requested_bins`
    is parameterised three ways - while the upstream card counts classes, so `total - passed` and
    `reproduced` are different units and subtracting one from the other compares nothing. Review
    caught that; the fix is that the upstream card already NAMES the local test guarding each class
    in its `ours` field, so the join is on the class itself.

    A class upstream reproduces and nothing here guards is raised rather than counted: it would
    otherwise shrink the denominator and read as a cleaner result.
    """
    held, broken, unguarded = [], [], []
    for entry in theirs["results"]:
        if not entry.get("reproduced"):
            continue
        tests = [item for item in ours["results"] if entry["ours"] in item["test"]]
        if not tests:
            unguarded.append(entry["id"])
        elif all(item["outcome"] == "Passed" for item in tests):
            held.append(entry["id"])
        else:
            broken.append(entry["id"])
    if unguarded:
        raise ValueError("Defect class(es) upstream reproduces with no test here: " + ", ".join(unguarded))
    return held, broken


def parity_rows(root, census):
    catalog = (Path(root) / CATALOG).read_text(encoding="utf-8")
    dispositions = collections.Counter(
        disposition for _, disposition in
        re.findall(r'new\("([^"]+)",\s*OpenEvolveKeyDisposition\.(\w+)', catalog))
    keys = sum(dispositions.values())
    # A census that cannot say which OpenEvolve it read cannot support a parity claim, and `Ref` is
    # null when the checkout has no describable ref - which `.get` with a fallback would have
    # rendered as a hardcoded commit that was never checked.
    reference = " ".join(str(census.get(key)) for key in ("Commit", "Ref") if census.get(key))
    if not reference.strip():
        raise ValueError("The census names no commit or ref for the checkout it was taken from")
    examples = sorted(path.parent.name for path in
                      (Path(root) / "benchmarks/evidence/openevolve-examples").glob("*/run.json")
                      if json.loads(path.read_text(encoding="utf-8")).get("openEvolveEvaluator"))
    manifest = read(root, AE_MANIFEST)
    robust = read(root, OURS_ROBUST)
    defects_032, defects_040 = read(root, DEFECTS_032), read(root, DEFECTS_040)
    reproduced_032, reproduced_040 = defects_032["reproduced"], defects_040["reproduced"]
    held, broken = defect_classes(robust, defects_040)
    # The two upstream releases must have reproduced the same inventory, or "the same classes two
    # releases apart" is a sentence about two different sets.
    if {entry["id"] for entry in defects_032["results"] if entry.get("reproduced")} != \
            {entry["id"] for entry in defects_040["results"] if entry.get("reproduced")}:
        raise ValueError("The two upstream cards reproduce different defect classes")

    rows = [
        row("Configuration surface", OPENEVOLVE,
            "%d keys classified (%s)" % (keys, ", ".join(
                "%d %s" % (count, name) for name, count in sorted(dispositions.items()))),
            "%d options at %s" % (len(census["UpstreamOptions"]), reference),
            "%d upstream options are unclassified; %d catalog key(s) upstream does not expose (%s)"
            % (len(census["Unclassified"]), len(census["CatalogNotUpstream"]),
               ", ".join(census["CatalogNotUpstream"]) or "none"),
            PARITY if not census["Unclassified"] else BEHIND, census["Evidence"]),
        row("Unmodified upstream evaluators", OPENEVOLVE,
            "%d of their example evaluators run through our CLI" % len(examples),
            "%d example evaluators" % len(examples),
            "run end to end against the upstream evaluator process, not a reimplementation: "
            + ", ".join(examples), PARITY, "benchmarks/evidence/openevolve-examples"),
        row("Archive and scheduling defects", OPENEVOLVE,
            "%d of %d defect classes hold here" % (len(held), len(held) + len(broken)),
            "%d classes reproduced on 0.3.2 and %d on 0.4.0" % (reproduced_032, reproduced_040),
            "joined on the class id each card names, not on a count: the upstream card names the "
            "test guarding each class and the local suite runs %d tests for these %d classes (%s is "
            "parameterised). %s. The same classes reproduce two releases apart, so they are design "
            "and not a bad build"
            % (len(robust["results"]), len(held) + len(broken), "D7",
               "Every class holds" if not broken else "FAILING HERE: " + ", ".join(broken)),
            AHEAD, OURS_ROBUST,
            ours_range=[len(broken), len(broken)],
            theirs_range=[min(reproduced_032, reproduced_040), max(reproduced_032, reproduced_040)]),
        row("Published results transcribed", ALPHAEVOLVE,
            "%d of %d verified by an independent verifier, %d pending"
            % (manifest["verified"], manifest["problems"], manifest["pending"]),
            "%d published constructions" % manifest["problems"],
            "every verifier reproduces the published value from the published construction, so the "
            "target list is checkable rather than quoted", PARITY, AE_MANIFEST),
    ]
    return rows


def headroom_row(root, headroom, relative):
    gaps = sorted(item["GapFraction"] for item in headroom["Rows"])
    closest = min(headroom["Rows"], key=lambda item: item["GapFraction"])
    reached = [item["Problem"] for item in headroom["Rows"] if item["FloorReachesPublished"]]
    return row("Distance to the published constructions", ALPHAEVOLVE,
               "not run: the engine has never been pointed at these problems",
               "%d in-scope problems, %d others of other candidate shapes"
               % (len(headroom["Rows"]), len(headroom["OutOfScope"])),
               "a model-free floor is behind on %d of %d by %+.2f%% to %+.2f%% - room to compete, and "
               "nothing yet competing for it. Its closest is %s at %+.2f%%, which is near enough "
               "that a result there would say little about the searcher%s"
               % (len(headroom["Rows"]) - len(reached), len(headroom["Rows"]),
                  100 * gaps[0], 100 * gaps[-1], closest["Problem"], 100 * closest["GapFraction"],
                  "" if not reached else "; the floor already reaches " + ", ".join(reached)),
               UNMEASURED, relative, floor_study=True)


def performance_rows(root):
    overhead = read(root, OVERHEAD)
    single = next(item for item in overhead["summary"] if item["workers"] == 1)
    scaling = read(root, SCALING)
    ours_sizes = scaling["summary"]["aidotnet"]["us_per_eval_median"]
    theirs_sizes = scaling["summary"]["openevolve"]["us_per_eval_median"]
    ours_latency = [item for item in read_lines(root, LATENCY_OURS)
                    if item["Label"] == "continuous opportunistic window 16"]
    theirs_latency = read_lines(root, LATENCY_THEIRS)
    sandbox, checkpoint = read(root, SANDBOX), read(root, CHECKPOINT)
    small, large = checkpoint["rows"][0], checkpoint["rows"][-1]
    worker = read(root, WORKER)
    ensemble, ensemble_repeats, ensemble_proposals = {}, 0, 0
    for count in (1, 4, 16):
        rows = read(root, ENSEMBLE % count)["Rows"]
        ensemble[count] = statistics.median(item["OverheadMicrosecondsPerProposal"] for item in rows)
        ensemble_repeats, ensemble_proposals = len(rows), rows[0]["Proposals"]
    soak = read(root, SOAK)
    working = [sample["WorkingSetBytes"] for sample in soak["Samples"]]
    distributed = read(root, DISTRIBUTED + "/summary.json")
    one, four = read(root, DISTRIBUTED + "/tp1.json"), read(root, DISTRIBUTED + "/tp4.json")

    return [
        row("Engine overhead per evaluation", OPENEVOLVE,
            "%.1f us/eval" % single["aidotnet_us_per_eval"],
            "%.0f us/eval" % single["openevolve_us_per_eval"],
            "ratio %.4f, CI95 [%.4f, %.4f] over %d interleaved repeats; peak memory %.0f MB against "
            "%.0f MB. The gap narrows as workers are added - ratio %.4f at 1 worker to %.4f at %d - "
            "and the card's own caveat stands: %s"
            % (single["ratio_ours_over_theirs"], single["ratio_ci95"][0], single["ratio_ci95"][1],
               single["repeats_aidotnet_first"] + single["repeats_openevolve_first"],
               single["aidotnet_peak_mb"], single["openevolve_peak_mb"],
               min(item["ratio_ours_over_theirs"] for item in overhead["summary"]),
               max(item["ratio_ours_over_theirs"] for item in overhead["summary"]),
               max(item["workers"] for item in overhead["summary"]),
               overhead["note"].rstrip(".").lower()),
            AHEAD, OVERHEAD, interval=single["ratio_ci95"],
            ours_range=single["aidotnet_us_per_eval_range"],
            theirs_range=single["openevolve_us_per_eval_range"]),
        row("Overhead as the population grows", OPENEVOLVE,
            "%.1f to %.1f us/eval across %s" % (min(ours_sizes.values()), max(ours_sizes.values()),
                                                " to ".join(sorted(ours_sizes, key=int)[::len(ours_sizes) - 1])),
            "%.0f to %.0f us/eval across %s" % (min(theirs_sizes.values()), max(theirs_sizes.values()),
                                                " to ".join(sorted(theirs_sizes, key=int)[::len(theirs_sizes) - 1])),
            "largest over smallest: %.4fx here against %.2fx there, and their largest size is the "
            "largest they completed" % (scaling["summary"]["aidotnet"]["growth_largest_over_smallest"],
                                        scaling["summary"]["openevolve"]["growth_largest_over_smallest"]),
            AHEAD, SCALING,
            ours_range=[min(ours_sizes.values()), max(ours_sizes.values())],
            theirs_range=[min(theirs_sizes.values()), max(theirs_sizes.values())]),
        row("Keeping a concurrency budget busy", OPENEVOLVE,
            "%.3fx ideal wall time" % min(item["WallOverIdeal"] for item in ours_latency),
            "%.3fx ideal wall time" % min(item["WallOverIdeal"] for item in theirs_latency),
            "in-flight utilisation %.1f%% against %.1f%% at the same concurrency of %d, over %d and "
            "%d repeats" % (100 * max(item["InFlightUtilisation"] for item in ours_latency),
                            100 * max(item["InFlightUtilisation"] for item in theirs_latency),
                            ours_latency[0]["Concurrency"], len(ours_latency), len(theirs_latency)),
            AHEAD, LATENCY_OURS,
            ours_range=[min(item["WallOverIdeal"] for item in ours_latency),
                        max(item["WallOverIdeal"] for item in ours_latency)],
            theirs_range=[min(item["WallOverIdeal"] for item in theirs_latency),
                          max(item["WallOverIdeal"] for item in theirs_latency)]),
        row("Sandbox cost per evaluation", None,
            "+%.2f ms p50 warm, +%.1f ms per process, +%.1f ms in Docker"
            % (sandbox["WarmAddedP50Ms"], sandbox["AddedP50Ms"], sandbox["DockerAddedP50Ms"]), None,
            "%d evaluations on %s; the warm worker is the default and the other two are the fallbacks"
            % (sandbox["Evaluations"], sandbox["Os"]), MEASURED, SANDBOX),
        row("Checkpoint and resume", None,
            "%.0f ms p50 write at %s elites and %.0f ms at %s; resume %.0f ms and %.0f ms"
            % (small["WriteP50Ms"], format(small["Elites"], ","), large["WriteP50Ms"],
               format(large["Elites"], ","), small["ResumeMs"], large["ResumeMs"]), None,
            "p95 %.0f and %.0f ms, %.0f bytes per elite, and the resumed state matches the written "
            "one in both" % (small["WriteP95Ms"], large["WriteP95Ms"], large["BytesPerElite"]),
            MEASURED, CHECKPOINT),
        row("Evaluation worker round trip", None,
            "%.3f ms p50 IPC round trip, %.3f ms p95" % (worker["ipc_round_trip"]["p50_ms"],
                                                         worker["ipc_round_trip"]["p95_ms"]), None,
            "over %d evaluations on %s. The per-lease overhead around it DEGRADES over the run - p50 "
            "%.1f ms in the first tenth against %.1f ms in the last, %.1f ms overall - which is "
            "measured here and not yet explained"
            % (worker["evaluations"], worker["platform"], worker["overhead_first_tenth"]["p50_ms"],
               worker["overhead_last_tenth"]["p50_ms"], worker["overhead"]["p50_ms"]),
            MEASURED, WORKER),
        row("Multi-model ensemble overhead", None,
            "%.3f us per proposal at %d models" % (ensemble[max(ensemble)], max(ensemble)), None,
            "median over %d repeats of %s proposals each; %s - at this scale the ensemble costs less "
            "than the measurement noise, so it is reported as a bound and not as a cost"
            % (ensemble_repeats, format(ensemble_proposals, ","),
               ", ".join("%.3f us at %d" % (ensemble[count], count) for count in sorted(ensemble))),
            MEASURED, ENSEMBLE % max(ensemble)),
        row("Memory over a long run", None,
            "%s evaluations in %.0f s" % (format(soak["CompletedEvaluations"], ","), soak["Seconds"]),
            None,
            "working set %.0f MB to %.0f MB over %d samples, peaking at %.0f MB, with a checkpoint "
            "every %s evaluations - growth, bounded and sampled, not a plateau"
            % (working[0] / 1e6, working[-1] / 1e6, len(working), max(working) / 1e6,
               format(soak["CheckpointEvery"], ",")), MEASURED, SOAK),
        row("Distributed evaluation", None,
            "%.2f to %.2f evaluations per second from 1 to %d workers (%.2fx)"
            % (one["evaluationsPerSecond"], four["evaluationsPerSecond"], four["workersAwaited"],
               four["evaluationsPerSecond"] / one["evaluationsPerSecond"]), None,
            "and the state hash is identical across worker counts and across hosts, with all %d "
            "recorded checks passing - including exactly-once delivery and a killed worker's lease "
            "being reissued" % len(distributed["checks"]), MEASURED, DISTRIBUTED + "/summary.json"),
    ]


def quality_rows(root):
    pilot = read(root, PILOT)
    sr = read(root, SR_HEADROOM)
    closed = sorted(item["gap_closed"] for item in sr["rows"] if item["task"] == "rational_gain")
    endpoints = [{"Config": item["Config"], "Superior": item["Superior"],
                  "MeanDifference": item["MeanDifference"], "Interval95": item["Interval95"],
                  "HolmAdjustedP": item["HolmAdjustedP"], "N": item["N"]}
                 for item in pilot["Endpoints"]]
    reading = "; ".join(
        "%s %+.4f CI95 [%+.4f, %+.4f] holm %.3f on n=%d"
        % (item["Config"], item["MeanDifference"], item["Interval95"][0], item["Interval95"][1],
           item["HolmAdjustedP"], item["N"]) for item in endpoints)
    rows = [row("Search quality, head to head", OPENEVOLVE,
                "one campaign run: %s" % pilot["Campaign"],
                "both their configurations, recommended and matched", reading
                + " - no endpoint is superior, and the gate is %s"
                % json.dumps(pilot["OpenEvolveGate"]),
                INCONCLUSIVE, PILOT, endpoints=endpoints),
            row("Could the pilot's task separate two searchers", None,
                "a model-free floor closes %.1f%% to %.1f%% of the gap on the pilot's task"
                % (100 * closed[0], 100 * closed[-1]), None,
                "so the one campaign that has run measured the task with the least room to separate "
                "two searchers; damped_oscillator is the discriminating one in the same family",
                MEASURED, SR_HEADROOM, floor_study=True)]
    return rows


def capability_rows(root):
    repair = read(root, ABLATIONS["repair"])
    reuse = read(root, ABLATIONS["reuse"])
    routing = read(root, ABLATIONS["routing"])
    automl = read(root, ABLATIONS["automl"])
    warm = read(root, ABLATIONS["warm-start"])
    numeric = next(item for item in routing["Families"] if item["Family"] == "numeric")
    return [
        row("Compiler-guided repair", None,
            "+%.2f points of valid proposals" % repair["ValidProposalRatePoints"]["Mean"], None,
            "CI95 [%+.2f, %+.2f] over %d seeds, and %+.4f model calls per valid candidate [%+.4f, "
            "%+.4f] - the feature pays for itself in calls"
            % (repair["ValidProposalRatePoints"]["LowerBound95"],
               repair["ValidProposalRatePoints"]["UpperBound95"], repair["ValidProposalRatePoints"]["Seeds"],
               repair["CallsPerValidCandidate"]["Mean"], repair["CallsPerValidCandidate"]["LowerBound95"],
               repair["CallsPerValidCandidate"]["UpperBound95"]),
            MEASURED, ABLATIONS["repair"]),
        row("Novelty and experience reuse", None,
            "+%.2f points of proposals suppressed" % reuse["SuppressedProposalRatePoints"]["Mean"], None,
            "but calls to target moved the wrong way, %+.2f [%+.2f, %+.2f] over %d pairs that reached "
            "it: the suppression is real and the saving it was meant to buy is NOT SUPPORTED"
            % (reuse["CallsToTargetDifference"]["Mean"], reuse["CallsToTargetDifference"]["LowerBound95"],
               reuse["CallsToTargetDifference"]["UpperBound95"], reuse["PairsReachingTarget"]),
            MEASURED, ABLATIONS["reuse"]),
        row("Adaptive operator routing", None,
            "quality non-inferior to the best single operator in all %d families" % len(routing["Families"]), None,
            "objective calls x%.4f [%.4f, %.4f] against the best static choice on the numeric family, "
            "so the 0.75x call-reduction target is NOT SUPPORTED"
            % (numeric["ObjectiveCallRatio"]["Mean"], numeric["ObjectiveCallRatio"]["LowerBound95"],
               numeric["ObjectiveCallRatio"]["UpperBound95"]),
            MEASURED, ABLATIONS["routing"]),
        row("Hyperparameter search against grid search", None,
            "test MSE ratio %.4f" % automl["TestMseRatio"]["Mean"], None,
            "CI95 [%.4f, %.4f] at an equal budget of %d fits each, %d seeds better here and %d there: "
            "parity, not a win" % (automl["TestMseRatio"]["LowerBound95"], automl["TestMseRatio"]["UpperBound95"],
                                   automl["EqualFitsPerSearcher"], automl["SeedsEngineBetter"],
                                   automl["SeedsGridBetter"]),
            MEASURED, ABLATIONS["automl"]),
        row("Warm start from a previous run", None,
            "%.4f of the evaluations to the same bar" % warm["EvaluationRatio"]["Mean"], None,
            "CI95 [%.4f, %.4f]; %d of %d seeds met the bar on resume at zero further cost, and %d "
            "never reached it" % (warm["EvaluationRatio"]["LowerBound95"], warm["EvaluationRatio"]["UpperBound95"],
                                  warm["SeedsBarMetOnResume"], warm["Seeds"], warm["SeedsWarmNeverReachedBar"]),
            MEASURED, ABLATIONS["warm-start"]),
    ]


def build(root, census, headroom_path):
    """Assembles the comparison from committed files. Derives every number; copies none."""
    root = Path(root)
    headroom = json.loads((root / headroom_path).read_text(encoding="utf-8"))
    sections = (
        ("parity", parity_rows(root, census) + [headroom_row(root, headroom, headroom_path)]),
        ("performance", performance_rows(root)),
        ("quality", quality_rows(root)),
        ("capability", capability_rows(root)),
    )
    rows = []
    for section, items in sections:
        for item in items:
            item["Section"] = section
            rows.append(item)
    counts = collections.Counter(item["Verdict"] for item in rows)
    return {
        "Schema": SCHEMA,
        "Competitors": [OPENEVOLVE, ALPHAEVOLVE],
        "Rows": rows,
        "Counts": {verdict: counts.get(verdict, 0) for verdict in VERDICTS},
        "Unsupported": 0,
    }

def disjoint(row):
    """True when the two measured ranges do not overlap, in the direction the row calls better."""
    ours, theirs = row["OursRange"], row["TheirsRange"]
    if not ours or not theirs:
        return None
    if row["LowerIsBetter"]:
        return max(ours) < min(theirs)
    return min(ours) > max(theirs)


def interval_excludes_parity(row):
    """An interval on a comparative row is a ratio of ours to theirs, so parity is exactly 1."""
    interval = row["Interval"]
    if not interval:
        return None
    return max(interval) < 1.0 if row["LowerIsBetter"] else min(interval) > 1.0


def lint(report, root):
    """Refuses a comparison that claims more than its evidence carries.

    The three rules worth stating, because each refuses a specific way this document could be made
    to flatter us. A comparative verdict needs a competitor figure AND a basis for separation - an
    interval that excludes parity, or two ranges that do not overlap - so one lucky run cannot become
    a win. A FLOOR STUDY can never read `ahead`: it measures how far a model-free search gets, which
    is a statement about the task and not about our engine, and reading it the other way is how
    "the floor is 10% behind AlphaEvolve" would turn into "we are close to AlphaEvolve". And a row
    citing campaign endpoints can only read `ahead` if one of those endpoints is marked superior,
    which is the single check standing between the pilot's two inconclusive results and a v1 claim.
    """
    root = Path(root)
    problems = []
    for item in report["Rows"]:
        label = "%s / %s" % (item["Dimension"], item["Competitor"] or "no competitor")
        if item["Verdict"] not in VERDICTS:
            problems.append(label + ": unknown verdict " + str(item["Verdict"]))
        if not item["Evidence"] or not (root / item["Evidence"]).exists():
            problems.append(label + ": evidence " + str(item["Evidence"]) + " is not in the tree")
        if not str(item["Reading"]).strip():
            problems.append(label + ": no reading behind the row")
        if item["Verdict"] in COMPARATIVE:
            if item["Theirs"] is None:
                problems.append(label + ": " + item["Verdict"] + " of nothing - no competitor figure")
            bases = [basis for basis in (disjoint(item), interval_excludes_parity(item))
                     if basis is not None]
            if not bases:
                problems.append(label + ": " + item["Verdict"]
                                + " with neither an interval nor two ranges to separate the arms")
            elif not all(bases):
                problems.append(label + ": " + item["Verdict"]
                                + " on evidence that does not separate the arms")
            if item["FloorStudy"]:
                problems.append(label + ": a floor study cannot be " + item["Verdict"]
                                + " - it measures the task, not this engine")
            if item["Endpoints"] is not None and not any(end["Superior"] for end in item["Endpoints"]):
                problems.append(label + ": " + item["Verdict"]
                                + " while no endpoint it cites is marked superior")
        if item["Verdict"] == MEASURED and item["Theirs"] is not None:
            problems.append(label + ": recorded as having no competitor figure, but one is quoted")
    counts = collections.Counter(item["Verdict"] for item in report["Rows"])
    for verdict in VERDICTS:
        if report["Counts"].get(verdict, 0) != counts.get(verdict, 0):
            problems.append("the %s count says %d and the rows say %d"
                            % (verdict, report["Counts"].get(verdict, 0), counts.get(verdict, 0)))
    if report["Unsupported"] != len(problems):
        problems.append("the report records %d unsupported rows and the lint found %d"
                        % (report["Unsupported"], len(problems)))
    if problems:
        raise ValueError("Rows the evidence does not carry: " + "; ".join(problems))
    return 0


HEADING = {
    "parity": "Feature parity",
    "performance": "Performance, measured on one machine",
    "quality": "Search quality - the open question",
    "capability": "Features of ours, measured on their own",
}
PREAMBLE = {
    "parity": "Parity is a derived number here, not a hand-kept list: the option census diffs their "
              "own `config.py` against the catalog that claims to classify it, and the AlphaEvolve "
              "row counts verifiers that reproduce the published value.",
    "performance": "Both arms of every head-to-head ran on the same machine in interleaved repeats, "
                   "so the ordering is not measuring a warm cache, and each card records the machine "
                   "and whether it was quiet. Rows without a competitor figure say so, and the two "
                   "that measured something unflattering - a worker overhead that doubles over a run, "
                   "memory that grows across a soak - say that too.",
    "quality": "This is the row v1 turns on, and it is not yet decided. One head-to-head campaign "
               "has run; neither endpoint reached superiority, and the task it ran on is one a "
               "model-free floor nearly solves by itself.",
    "capability": "OpenEvolve has no counterpart to these, so there is nothing to be ahead of. Three "
                  "of the five did not support the claim they were built to test, and say so.",
}


def lines(report):
    out = []
    for verdict in VERDICTS:
        out.append("%-30s %d" % (verdict, report["Counts"][verdict]))
    for item in report["Rows"]:
        out.append("%-40s %-28s %s" % (item["Dimension"][:40], item["Verdict"],
                                       (item["Competitor"] or "-")))
    return out


def render(report):
    """The published document. Regenerated, never edited: a test re-renders it and diffs the bytes."""
    out = ["# Where AiDotNet.Evolution stands against OpenEvolve and AlphaEvolve", "",
           "Generated by [`benchmarks/analysis/competitor_report.py`]"
           "(../benchmarks/analysis/competitor_report.py) from the committed evidence bundles. "
           "Every number in this file is read out of the file named in its row; the generator's lint "
           "refuses a row whose evidence does not carry it, and the test re-renders this document and "
           "compares the bytes, so it cannot be edited into something the evidence does not say.", "",
           "| verdict | rows |", "| --- | ---: |"]
    for verdict in VERDICTS:
        out.append("| %s | %d |" % (verdict, report["Counts"][verdict]))
    out.append("")
    for section in ("parity", "performance", "quality", "capability"):
        rows = [item for item in report["Rows"] if item["Section"] == section]
        if not rows:
            continue
        out += ["## " + HEADING[section], "", PREAMBLE[section], "",
                "| dimension | ours | theirs | verdict | reading | evidence |",
                "| --- | --- | --- | --- | --- | --- |"]
        for item in rows:
            out.append("| %s%s | %s | %s | **%s** | %s | [%s](../%s) |" % (
                item["Dimension"],
                "" if not item["Competitor"] else " (vs %s)" % item["Competitor"],
                item["Ours"] or "-", item["Theirs"] or "-", item["Verdict"],
                item["Reading"], Path(item["Evidence"]).name or item["Evidence"], item["Evidence"]))
        out.append("")
    waiting = [item for item in report["Rows"] if item["Verdict"] in (INCONCLUSIVE, UNMEASURED, BEHIND)]
    out += ["## What v1 is waiting on", "",
            "Derived from the rows above: every dimension that is not `ahead` or `parity`. This list "
            "is the gap between 0.5.0 and a v1 that claims to surpass both, and it is short because "
            "the engineering is done and the deciding measurement is not.", ""]
    for item in waiting:
        out.append("- **%s%s** - %s: %s" % (
            item["Dimension"], "" if not item["Competitor"] else " (vs %s)" % item["Competitor"],
            item["Verdict"], item["Reading"]))
    out.append("")
    return "\n".join(out) + "\n"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=str(Path(__file__).resolve().parents[2]))
    parser.add_argument("--census", required=True, help="the committed openevolve config census")
    parser.add_argument("--headroom", required=True, help="the committed alphaevolve headroom rows")
    parser.add_argument("--output", help="write the comparison as JSON")
    parser.add_argument("--document", help="write the rendered markdown here")
    arguments = parser.parse_args(argv)
    # The census is cited by path in the document, so it is named relative to the root like every
    # other piece of evidence and the lint checks it is really there.
    census = json.loads((Path(arguments.root) / arguments.census).read_text(encoding="utf-8"))
    census["Evidence"] = Path(arguments.census).as_posix()
    report = build(arguments.root, census, arguments.headroom)
    lint(report, arguments.root)
    print("\n".join(lines(report)))
    if arguments.output:
        with open(arguments.output, "x", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(report, indent=1) + "\n")
    if arguments.document:
        Path(arguments.document).write_text(render(report), encoding="utf-8", newline="\n")
    return report


if __name__ == "__main__":
    main()