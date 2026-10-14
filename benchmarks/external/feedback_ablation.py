"""V1-54 (#167) criterion 4: does LLM feedback move the primary endpoint on a dev partition?

Design, in one paragraph. Each seed is run twice against the same task, the same budget and the same
engine seed, once with `llmFeedback` absent and once with it enabled at a stated weight. The arms
differ in exactly that key. The endpoint is NOT the evaluator's score: the evaluator shows six
visible examples and the endpoint is accuracy on held-out inputs it never sees, scored afterwards by
feedback_ablation_task/score_heldout.py. That is deliberate. Both arms can reach a perfect visible
score, either by recovering the rule or by memorising the six pairs, and only the held-out set
tells those apart - which is the property #167's story asks feedback to supply ("suspicion of test
gaming"). A driver that reported the evaluator's own score would be unable to measure the thing.

Pairing is what makes 16 seeds enough to say anything: the two arms share a seed, so the difference
is taken within a pair and the between-seed variance drops out. The interval is a seeded percentile
bootstrap over the per-seed differences. It is still 16 pairs, and the endpoint is close to bimodal
in practice (a candidate usually either generalises or does not), so a small true effect will not be
resolved; the JSON records the discordant-pair counts alongside the interval so a reader can see how
much of the interval is carried by how few pairs.

Real model calls. Nothing here uses a stub, because a stub answers both arms identically and the
measurement would be of the stub. Cost is near-constant per call (~31.6k cache-creation tokens even
for a two-token prompt), so the bill is set by the call count, not the prompt size:
  calls = seeds * 2 * (maxEvaluations - 1)        proposals
        + seeds * maxEvaluations                  judgements, feedback arm only
"""
import argparse
import json
import pathlib
import random
import shutil
import statistics
import subprocess
import sys
import time

TASK = pathlib.Path(__file__).parent / "feedback_ablation_task"


def repository_root():
    for parent in pathlib.Path(__file__).resolve().parents:
        if (parent / "docs" / "parity").is_dir():
            return parent
    raise SystemExit("repository root not found above " + str(pathlib.Path(__file__).resolve()))


def run_file(output, seed, feedback_weight, evaluations, model, executable, budget_usd, time_limit, arm=None):
    """The two arms differ in `llmFeedback` and nothing else; every other key is shared."""
    run = {
        "schema": "aidotnet-evolve-run-v1",
        "runId": "feedback-ablation-s%d-%s" % (seed, arm or ("on" if feedback_weight is not None else "off")),
        "initialProgram": str(TASK / "initial_program.py"),
        "evaluator": str(TASK / "evaluator.py"),
        "output": str(output),
        "language": "Python",
        "evaluatorLanguage": "Python",
        "direction": "Maximize",
        "runtimeVersion": "feedback-ablation-v1",
        "mode": "FullRewrite",
        "taskDescription": (
            "Implement solve(x) for integers x. The evaluator shows only a handful of example pairs; "
            "it will be used on inputs well outside the range you are shown."
        ),
        "model": {"provider": "ClaudeCode", "name": model, "claudeExecutable": executable},
        # Both arms, identically. The evaluator returns the visible pairs and the candidate's answers as
        # artifacts; without them the proposer sees only a scalar and cannot induce the rule, and a dry
        # run confirmed that - every proposal stayed at the seed's own 1 of 6 in both arms.
        "prompt": {"includeArtifacts": True},
        # retainArtifactText is what makes includeArtifacts mean anything: with it off the prompt carries
        # "artifact: [artifact content withheld]" and the proposer sees no examples. Measured by recording
        # the prompt with a stub executable rather than inferred from the option names.
        "search": {"retainArtifactText": True},
        "budget": {
            "maxEvaluations": evaluations,
            "seed": seed,
            "parallelism": 1,
            "evaluationTimeLimitSeconds": time_limit,
        },
    }
    if budget_usd is not None:
        run["model"]["maxBudgetUsd"] = budget_usd
    if feedback_weight is not None:
        run["llmFeedback"] = {"weight": feedback_weight}
    return run


def evolve(cli, run_path, results_directory, timeout):
    """Runs one arm and returns the CLI's result JSON, or a reason it produced none."""
    started = time.time()
    try:
        finished = subprocess.run(
            [str(cli), "run", str(run_path)], capture_output=True, text=True, timeout=timeout, check=False)
    except subprocess.TimeoutExpired:
        # Returned, not raised: an escaping exception would lose every row collected so far, and a failed
        # arm is supposed to keep its seed in the denominator rather than remove the whole campaign.
        return None, time.time() - started, "exceeded the %d s run timeout" % timeout
    elapsed = time.time() - started
    if finished.returncode != 0:
        return None, elapsed, "exit %d: %s" % (finished.returncode, finished.stderr.strip()[-400:])
    try:
        return json.loads(finished.stdout), elapsed, None
    except json.JSONDecodeError as error:
        return None, elapsed, "unparsable result: %s; stdout=%s" % (error, finished.stdout[:300])


def best_program(results_directory):
    """The run writes best.<ext> beside its checkpoints; that file is the winner's exact source."""
    candidates = sorted(pathlib.Path(results_directory).glob("best.*"))
    return candidates[0] if candidates else None


def endpoint(program_path, python, timeout=120):
    """Accuracy on the held-out inputs, scored in a separate process that the search never ran.

    The candidate is code a model wrote, run on inputs the search never tried, so it may loop forever or
    print. Both are handled here rather than allowed to end the campaign: an unbounded call would hang the
    driver, and stray output would make json.loads raise after every row had already been paid for.
    """
    if program_path is None:
        return {"heldout_accuracy": 0.0, "compiled": False, "detail": "the run produced no best program"}
    try:
        finished = subprocess.run(
            [python, str(TASK / "score_heldout.py"), str(program_path)],
            capture_output=True, text=True, timeout=timeout, check=False)
    except subprocess.TimeoutExpired:
        return {"heldout_accuracy": 0.0, "compiled": True,
                "detail": "the candidate did not finish the held-out inputs within %d s" % timeout}
    if finished.returncode != 0:
        return {"heldout_accuracy": 0.0, "compiled": False, "detail": "scorer exit %d: %s" % (
            finished.returncode, finished.stderr.strip()[-300:])}
    lines = [line for line in finished.stdout.splitlines() if line.strip()]
    try:
        # The last line: the scorer prints exactly one JSON object last, and a candidate that printed
        # during import or inside solve would otherwise make this unparsable.
        return json.loads(lines[-1]) if lines else {}
    except (json.JSONDecodeError, IndexError):
        return {"heldout_accuracy": 0.0, "compiled": True,
                "detail": "the scorer's output was not parsable: " + finished.stdout.strip()[:200]}


def bootstrap_interval(differences, draws, rng):
    """Percentile interval over the PER-PAIR differences, which is where the pairing pays off."""
    if not differences:
        return [float("nan"), float("nan")]
    medians = []
    for _ in range(draws):
        sample = [differences[rng.randrange(len(differences))] for _ in differences]
        medians.append(statistics.median(sample))
    medians.sort()
    low = medians[int(0.025 * (draws - 1))]
    high = medians[int(0.975 * (draws - 1))]
    return [low, high]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--cli", required=True, help="the built aidotnet-evolve executable")
    parser.add_argument("--output", required=True, help="where the result JSON is written")
    parser.add_argument("--work", required=True, help="scratch directory for the per-arm run outputs")
    parser.add_argument("--seeds", type=int, default=16)
    parser.add_argument("--evaluations", type=int, default=6, help="per arm, the seed included")
    parser.add_argument("--weights", default="0.3", help="comma-separated llmFeedback.weight values; one arm each")
    parser.add_argument("--model", default="sonnet")
    parser.add_argument("--claude", default="claude")
    parser.add_argument("--max-budget-usd", type=float, default=None)
    parser.add_argument("--time-limit", type=int, default=60, help="sandbox seconds per evaluation")
    parser.add_argument("--run-timeout", type=int, default=1800, help="seconds for one arm")
    parser.add_argument("--python", default=sys.executable, help="interpreter for the held-out scorer")
    parser.add_argument("--draws", type=int, default=4000)
    parser.add_argument("--bootstrap-seed", type=int, default=7)
    arguments = parser.parse_args(argv)

    work = pathlib.Path(arguments.work)
    work.mkdir(parents=True, exist_ok=True)
    # One control arm and one arm per weight. A single weight cannot answer the question the measurement
    # raised: the blend is linear, so whether feedback can change which candidate wins depends on whether
    # the weight is above the point where the judge's gap overcomes the visible-score gap.
    weights = [float(value) for value in str(arguments.weights).split(",") if value.strip()]
    arms = [("off", None)] + [("on-w%g" % weight, weight) for weight in weights]
    rows = []
    for seed in range(arguments.seeds):
        for arm, weight in arms:
            results = work / ("s%d-%s" % (seed, arm))
            run_path = work / ("s%d-%s.run.json" % (seed, arm))
            # Cleared, not reused: a re-run arm that fails before writing best.* would otherwise have the
            # PREVIOUS invocation's winner scored as this arm's endpoint - a fabricated measurement that
            # looks like a result. The run also refuses an existing checkpoint, so a stale directory is
            # not reusable anyway.
            if results.exists():
                shutil.rmtree(results)
            run_path.write_text(json.dumps(run_file(
                results, seed, weight, arguments.evaluations, arguments.model, arguments.claude,
                arguments.max_budget_usd, arguments.time_limit, arm), indent=2), encoding="utf-8")
            result, elapsed, failure = evolve(arguments.cli, run_path, results, arguments.run_timeout)
            # A failed arm takes a zero endpoint by contract rather than by scoring whatever is on disk.
            scored = ({"heldout_accuracy": 0.0, "detail": "the arm produced no result: " + failure}
                      if failure is not None else endpoint(best_program(results), arguments.python))
            rows.append({
                "seed": seed,
                "arm": arm,
                "weight": weight,
                "seconds": round(elapsed, 2),
                "failure": failure,
                "visible_quality": None if result is None else result.get("BestQuality"),
                "completed_evaluations": None if result is None else result.get("CompletedEvaluations"),
                "model_calls": None if result is None else (result.get("ModelUsage") or {}).get("Proposals"),
                "heldout_accuracy": scored["heldout_accuracy"],
                "endpoint_detail": scored.get("detail"),
            })
            print(json.dumps(rows[-1]), flush=True)
    return report(rows, arguments)


def report(rows, arguments):
    by_pair = {}
    for row in rows:
        by_pair.setdefault(row["seed"], {})[row["arm"]] = row
    treated = sorted({row["arm"] for row in rows if row["arm"] != "off"})

    rng = random.Random(arguments.bootstrap_seed)
    summaries, all_pairs = {}, {}
    for arm in treated:
        paired, differences = [], []
        for seed in sorted(by_pair):
            pair = by_pair[seed]
            if "off" not in pair or arm not in pair:
                continue
            # A failed arm keeps its seed in the denominator with a zero endpoint rather than being
            # dropped: dropping it would let a crash in one arm improve that arm's figure.
            difference = pair[arm]["heldout_accuracy"] - pair["off"]["heldout_accuracy"]
            paired.append({
                "seed": seed,
                "off": pair["off"]["heldout_accuracy"],
                "on": pair[arm]["heldout_accuracy"],
                "difference": difference,
                "off_failure": pair["off"]["failure"],
                "on_failure": pair[arm]["failure"],
            })
            differences.append(difference)

        wins = sum(1 for value in differences if value > 0)
        losses = sum(1 for value in differences if value < 0)
        summaries[arm] = {
            "pairs": len(differences),
            "endpoint": "heldout_accuracy",
            "weight": next((row["weight"] for row in rows if row["arm"] == arm), None),
            "off_median": statistics.median([p["off"] for p in paired]) if paired else None,
            "on_median": statistics.median([p["on"] for p in paired]) if paired else None,
            "median_difference": statistics.median(differences) if differences else None,
            "mean_difference": statistics.fmean(differences) if differences else None,
            "difference_ci95": bootstrap_interval(differences, arguments.draws, rng),
            # The interval over a near-bimodal endpoint is carried by the discordant pairs; stated so a
            # reader can see whether a conclusion rests on three pairs or on thirteen.
            "pairs_favouring_feedback": wins,
            "pairs_favouring_no_feedback": losses,
            "tied_pairs": len(differences) - wins - losses,
        }
        all_pairs[arm] = paired

    summary = {
        "arms": summaries,
        "arms_that_produced_no_result": sum(1 for row in rows if row["failure"] is not None),
    }

    document = {
        "story": "V1-54 #167 criterion 4: LLM feedback as a weighted fitness signal, dev-partition ablation",
        "design": (
            "paired by engine seed; the arms differ only in the run file's llmFeedback key "
            "(absent vs weight in %s). Endpoint is accuracy on held-out inputs the evaluator never sees, "
            "scored after the run by feedback_ablation_task/score_heldout.py, because both arms can "
            "reach a perfect visible score either by recovering the rule or by memorising the six "
            "visible pairs and only the held-out set separates those." % arguments.weights
        ),
        "interval": ("median of per-pair differences with a %d-draw seeded percentile bootstrap "
                     "(seed %d). %d pairs, and a near-bimodal endpoint, so a small true effect is not "
                     "resolvable here; the discordant-pair counts are reported beside the interval."
                     % (arguments.draws, arguments.bootstrap_seed, arguments.seeds)),
        "model": {"provider": "ClaudeCode", "name": arguments.model, "executable": arguments.claude},
        "budget": {"seeds": arguments.seeds, "evaluations_per_arm": arguments.evaluations,
                   "weights": arguments.weights},
        "summary": summary,
        "pairs": all_pairs,
        "rows": rows,
    }
    pathlib.Path(arguments.output).write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
