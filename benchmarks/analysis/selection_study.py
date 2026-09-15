"""One-use development ablation of parent selection; never a final superiority test."""
import argparse
import json
from pathlib import Path
import random
import subprocess

from analyze import require
from design import digest, write_new
from program_report import TRACKS, read, report
from program_study import EXTERNAL, SCALES, hashes, resource_summary, trajectory, verify_artifacts
from program_tasks import TASKS, initial_program
from run_program_pilot import run as pilot
from run_program_audit import run as audit


def schedule():
    blocks = []
    for seed, profiles in ((201, ("uniform", "best")), (203, ("best", "uniform"))):
        tasks = list(TASKS)
        random.Random(seed).shuffle(tasks)
        for profile in profiles:
            blocks.append(dict(id=f"{profile}-{seed}", seed=seed, profile=profile, tasks=tasks,
                               search_instance_seed=seed*10+1, diagnostic_instance_seed=seed*10+2,
                               audit_instance_seed=seed*10+3))
    return blocks


def validate(plan):
    require(plan.get("schema") == "selection-development-v1" and plan.get("claim") == "none", "Invalid development plan.")
    require(plan["schedule"] == schedule() and plan["iterations"] == 4 and plan["timing_samples"] == 3 and
            plan["model_calls_maximum"] == (252 if plan["codex"] else 0), "Changed fixed ablation budget/schedule.")
    require(set(plan["tasks"]) == set(TASKS), "Changed development task panel.")


def prepare(root, *, image, upstream, openevolve, dll, codex=None):
    paths = [*EXTERNAL.glob("*.py"), *Path(__file__).parent.glob("*.py"),
             EXTERNAL / "sandbox/worker.py", EXTERNAL / "sandbox/Dockerfile", *Path(dll).resolve().parent.glob("*.dll")]
    if codex:
        paths.append(Path(codex).resolve(strict=True))
    plan = dict(schema="selection-development-v1", claim="none", purpose="development-only policy ablation",
                tasks={t: initial_program(upstream,t)[1] for t in TASKS}, schedule=schedule(), iterations=4, timing_samples=3,
                image=image, upstream=str(Path(upstream).resolve()), openevolve=str(Path(openevolve).resolve()),
                dll=str(Path(dll).resolve(strict=True)), codex=str(Path(codex).resolve(strict=True)) if codex else None,
                model="gpt-6-astra", model_calls_maximum=252 if codex else 0, artifacts=hashes(paths),
                source_revision=subprocess.check_output(["git","rev-parse","HEAD"],cwd=EXTERNAL,text=True).strip(),
                intervention="controlled: parent policy only; native: parent and built-in inspiration policy",
                accounting="equal declared caps, NOT equal actual tokens/time; all tuning calls charged",
                stop="fixed four blocks; unknown work stops dispatch; no retries or optional extensions",
                limitation="Inspected development families, two paired seeds; not powered confirmation or exact provider snapshot")
    validate(plan)
    root = Path(root)
    root.mkdir(parents=True,exist_ok=False)
    write_new(root / "registration.json",plan)
    return digest(plan)


def execute(root, expected_hash):
    root = Path(root)
    plan = read(root / "registration.json")
    validate(plan)
    require(digest(plan) == expected_hash, "Registration hash mismatch.")
    verify_artifacts(plan)
    write_new(root / "consumed.json",dict(registration_sha256=expected_hash))
    observed, failure = [], None
    for block in plan["schedule"]:
        try:
            verify_artifacts(plan)
            path = root / block["id"]
            result = pilot(path, plan["image"], plan["upstream"], plan["openevolve"], plan["dll"],
                           codex=plan["codex"], model=plan["model"], tasks=block["tasks"],
                           seed=block["seed"], search_instance_seed=block["search_instance_seed"],
                           diagnostic_instance_seed=block["diagnostic_instance_seed"],
                           iterations=plan["iterations"], evolution_profile=block["profile"], samples=plan["timing_samples"])
            audit_path = root / (block["id"] + "-audit")
            audit(path,audit_path,plan["image"],seed=block["audit_instance_seed"])
            rows = report(path,audit_path)["rows"]
            progress = []
            for task in block["tasks"]:
                for run in read(path / task / "comparison.json")["runs"]:
                    progress.append(dict(task=task,mode=run["mode"],method=run["method"],trajectory=trajectory(run,SCALES[task])))
            value = dict(id=block["id"],profile=block["profile"],seed=block["seed"],rows=rows,progress=progress)
            observed.append(value)
            write_new(root / (block["id"] + "-analysis.json"),value)
            require(not result.get("not_run") and result["unknown_evaluator_attempts"] == 0 and
                    all(p["trajectory"].get("counters") and not any(p["trajectory"]["counters"]["unknown"].values()) for p in progress),
                    "Unknown work stops ablation dispatch.")
            print(json.dumps(dict(block=block["id"],status=result["status"],calls=result["model_calls"])),flush=True)
        except Exception as error:
            failure = dict(block=block["id"],error=type(error).__name__+": "+str(error)[:400])
            break
    by_id = {b["id"]:b for b in observed}
    rows = []
    for block in plan["schedule"]:
        lookup = {(r["task"],r["mode"],r["method"]):r for r in by_id.get(block["id"],{}).get("rows",[])}
        for task in block["tasks"]:
            for mode,method in TRACKS:
                row = lookup.get((task,mode,method),dict(task=task,mode=mode,method=method,status="missing",deployed_seconds=None,model_tokens=None))
                rows.append(dict(block=block["id"],profile=block["profile"],seed=block["seed"],**row))
    resources = resource_summary(root,plan)
    require(resources["model_attempts"] <= plan["model_calls_maximum"], "Model-call cap exceeded.")
    result = dict(schema="selection-development-results-v1",claim="none",status="failed" if failure else "completed",
                  failure=failure,registration_sha256=expected_hash,blocks=observed,rows=rows,resources=resources,
                  superiority_established=False,holdout_status="development only; future protocol must be frozen separately")
    write_new(root / "study.json",result)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command",required=True)
    prep = commands.add_parser("prepare")
    for name in ("root","image","upstream","openevolve","dll"):
        prep.add_argument("--"+name,required=True)
    prep.add_argument("--codex")
    run = commands.add_parser("execute")
    run.add_argument("--root",required=True)
    run.add_argument("--registration-sha256",required=True)
    args = vars(parser.parse_args())
    if args.pop("command") == "prepare":
        print(prepare(**args))
    else:
        value = execute(args["root"],args["registration_sha256"])
        print(json.dumps(dict(status=value["status"],claim=value["claim"])))
        raise SystemExit(0 if value["status"] == "completed" else 1)
