"""One-use, partitioned subscription-only head-to-head with retained failure grid.

Prepare a plan, publish its SHA256 before execution, then execute that exact plan.
This driver never declares superiority; report inference is a separate operation.
"""
import argparse
import hashlib
import json
from pathlib import Path
import random
import subprocess
import time

from codex_transport import CodexTransport
from docker_sandbox import encode
from program_controls import candidate_hash
from program_promotion import promote
from run_program_comparison import run_campaign
from warm_evaluator import WarmEvaluator
from warm_panel import DEFINITIONS, prepare_task, description
from warm_sandbox import WarmDockerSandbox
from warm_study_design import call_cap, choose_profiles, digest, grid, power_requirement
from warm_budget import CampaignBudget, requirements, integer, validate_limits, validate_accounting

ROOT = Path(__file__).resolve().parents[2]
OE_REVISION = "411fb59c886c18704caaffb611e17cf9e7d824d2"
ALGO_REVISION = "dff9914c10800c7a031c9e8c3d4d1c8cd1b38906"


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def checkout(path, expected):
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=path, text=True).strip()
    dirty = subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], cwd=path, text=True).strip()
    if revision != expected or dirty:
        raise ValueError("Pinned upstream checkout changed")
    return revision


def artifacts(dll, codex):
    folder = Path(__file__).parent
    files = [*folder.glob("*.py"), *folder.joinpath("sandbox").glob("*.py"),
             folder / "sandbox" / "Dockerfile", ROOT / "benchmarks/suite/catalog-v1.json",
             *Path(dll).parent.glob("*.dll"), *Path(dll).parent.glob("*.json"), Path(codex)]
    return {str(p.resolve()): sha(p) for p in files}


def verify(plan):
    if any(sha(path) != expected for path, expected in plan["artifacts_sha256"].items()):
        raise ValueError("Registered runtime artifact changed")
    checkout(plan["upstream"], ALGO_REVISION)
    checkout(plan["openevolve"], OE_REVISION)
    if plan["predecessor"] and sha(plan["predecessor"]) != plan["predecessor_sha256"]:
        raise ValueError("Predecessor evidence changed")


def predecessor(path, phase):
    value = json.loads(Path(path).read_bytes())
    if value["plan"].get("schema") != "warm-head-to-head-v3":
        raise ValueError("New budget protocol requires a new development registration")
    validate_accounting(value)
    expected = "development" if phase == "selection" else "selection"
    if value["phase"] != expected or value["status"] != "completed" or value["unknown_work"]:
        raise ValueError("Predecessor must be complete, reconciled and from the preceding partition")
    if len(value["rows"]) != len(value["plan"]["grid"]) or any(r["status"] != "completed" for r in value["rows"]):
        raise ValueError("Incomplete predecessor grid")
    if value["plan_sha256"] != digest(value["plan"]):
        raise ValueError("Predecessor plan binding failed")
    for row, cell in zip(value["rows"], value["plan"]["grid"]):
        if any(row.get(k) != v for k,v in cell.items()):
            raise ValueError("Predecessor planned cell was replaced")
        observed = [(p["mode"],p["method"]) for p in row["pairs"]]
        if len(observed) != len(set(observed)) or set(observed) != {tuple(t) for t in cell["tracks"]}:
            raise ValueError("Missing/duplicated predecessor tracks")
    return value


def prepare(output, upstream, openevolve, dll, codex, image, *, phase="development", previous=None, call_limit=412,
            budget_authorization=None, container_limit=None):
    if phase not in ("development", "selection", "final") or type(call_limit) is not int or call_limit < 1:
        raise ValueError("Invalid phase or total study call ceiling")
    if bool(previous) != (phase != "development"):
        raise ValueError("Non-development phases require a completed predecessor")
    profiles, consumed, requirement = dict(aidotnet="uniform", openevolve="default"), 0, None
    prior = predecessor(previous, phase) if previous else None
    if prior:
        consumed = prior["cumulative_model_calls"]
        # A new approval may enlarge the cap BEFORE opening the next partition,
        # never midway through a registered phase or by rerunning completed data.
        if call_limit != prior["plan"]["call_limit"] and not (isinstance(budget_authorization,str) and budget_authorization.strip()):
            raise ValueError("Changed cumulative cap requires an explicit budget approval reference")
        profiles = choose_profiles(prior["rows"]) if phase == "selection" else prior["plan"]["profiles"]
    if phase == "final":
        requirement = power_requirement(prior["rows"])
    count = 2 if phase == "development" else 4 if phase == "selection" else requirement["searches_per_task"]
    offset = {"development": 31000, "selection": 41000, "final": 51000}[phase]
    schedule = grid(phase, list(range(offset, offset + count)), profiles)
    planned_calls = call_cap(schedule)
    if consumed + planned_calls > call_limit:
        raise ValueError(f"Do not open {phase} data: {planned_calls} more calls required; {call_limit-consumed} available")
    limits = requirements(schedule, 4, 3)
    containers_before = prior["cumulative_container_attempts"] if prior else 0
    if not integer(container_limit):
        raise ValueError("Declare an explicit cumulative container-attempt limit")
    if prior and container_limit != prior["plan"]["container_limit"] and not (isinstance(budget_authorization, str) and budget_authorization.strip()):
        raise ValueError("Changed container cap requires an explicit budget approval reference")
    if containers_before + limits["search_containers"] + limits["confirmation_containers"] > container_limit:
        raise ValueError("Insufficient container budget including reserved final audits")
    root = Path(output).resolve()
    root.mkdir(parents=True, exist_ok=False)
    plan = dict(schema="warm-head-to-head-v3", phase=phase, profiles=profiles, grid=schedule,
                resource_limits=limits, container_limit=container_limit, cumulative_containers_before=containers_before,
                accounting_policy="warm-budget-v1", setup_allocation="shared-unallocated",
                correctness_contract="strict-upstream-v1", final_audit_policy="both-executables-v1",
                image=image, upstream=str(Path(upstream).resolve()), openevolve=str(Path(openevolve).resolve()),
                dll=str(Path(dll).resolve()), codex=str(Path(codex).resolve()), model="gpt-6-astra",
                resolved_model="unreported; model alias may change across time blocks", iterations=4, samples=3,
                # Input-size policy is fixed without viewing selection/final performance.
                scale_multiplier=16 if phase == "development" else 4,
                search_instance_seeds=[offset+1000, offset+1001],
                diagnostic_instance_seeds=[offset+2000, offset+2001],
                audit_instance_seeds=list(range(offset+3000, offset+3008)),
                model_calls_maximum=planned_calls, cumulative_calls_before=consumed, call_limit=call_limit,
                budget_authorization=budget_authorization,
                predecessor=str(Path(previous).resolve()) if previous else None,
                predecessor_sha256=sha(previous) if previous else None,
                artifacts_sha256=artifacts(dll, codex), power_requirement=requirement,
                source_revision=subprocess.check_output(["git", "rev-parse", "HEAD"],cwd=ROOT,text=True).strip(),
                primary_endpoint="Warm input-to-response INCLUDING transport/copying/serialization, normalized to each paired original",
                primary_gate="Each final task x four contrasts: Bonferroni 95% upper paired-log normalized-latency-ratio CI < 1",
                practical_target="20% lower warm latency; separately report whether upper CI < 0.8",
                resources="Kernel cgroup CPU/peak through response, including startup/probe overhead; NOT total host cost",
                correctness="Task-specific schema/host checks plus pinned upstream compatibility; both executables audited on eight fresh instances; NOT exhaustive proof",
                failure_policy="Failed search/invalid selection deploy original; retain failures; unknown work stops all dispatch",
                tuning="Two native profiles per method, equal task/seed/call caps; choose by mean log fresh-instance speedup",
                cost_policy="Equal declared caps, report actual calls/tokens/work/wall; NOT equal realized costs",
                claim="none")
    if prior and (prior["plan"]["artifacts_sha256"] != plan["artifacts_sha256"] or prior["plan"]["image"] != image):
        raise ValueError("Frozen implementation/image changed between partitions; start a new development registration")
    verify(plan)
    (root / "plan.json").write_bytes(encode(plan))
    return dict(plan=str(root / "plan.json"), sha256=digest(plan), calls=planned_calls, searches_per_task=count)


def execute(plan_path, registered_sha256):
    plan_path = Path(plan_path).resolve()
    plan = json.loads(plan_path.read_bytes())
    if digest(plan) != registered_sha256:
        raise ValueError("Registration hash mismatch")
    if plan.get("schema") != "warm-head-to-head-v3" or plan.get("correctness_contract") != "strict-upstream-v1" or plan.get("final_audit_policy") != "both-executables-v1":
        raise ValueError("Require a new registration for strengthened correctness and fallback audits")
    validate_limits(plan["resource_limits"])
    if (plan["resource_limits"] != requirements(plan["grid"], plan["iterations"], plan["samples"])
            or not all(integer(plan[k]) for k in ("call_limit", "cumulative_calls_before", "container_limit", "cumulative_containers_before"))
            or plan["resource_limits"]["model_calls"] + plan["cumulative_calls_before"] > plan["call_limit"]
            or sum(plan["resource_limits"][k] for k in ("search_containers", "confirmation_containers")) + plan["cumulative_containers_before"] > plan["container_limit"]):
        raise ValueError("Registered resource limits cannot cover complete planned work")
    verify(plan)
    root = plan_path.parent
    # Exclusive marker forbids resumes/retries/optional sample extensions.
    with (root / "execution-started.json").open("xb") as stream:
        stream.write(encode(dict(plan_sha256=registered_sha256, started_unix=time.time())))
    report = dict(plan=plan, plan_sha256=registered_sha256, phase=plan["phase"], status="running", claim="none",
                  rows=[dict(**cell, status="not-run", pairs=[]) for cell in plan["grid"]],
                  cumulative_model_calls=plan["cumulative_calls_before"], model_calls=0, unknown_work=0)
    report_path = root / "report.json"
    def save():
        report_path.write_bytes(encode(report))
    save()
    sandbox, transport = None, None
    budget = CampaignBudget(root / "resource-journal.jsonl", plan["resource_limits"], registered_sha256)
    started = time.monotonic()
    try:
        sandbox = budget.run("sandbox-setup", lambda: WarmDockerSandbox(plan["image"], root / "sandbox"))
        for index, row in enumerate(report["rows"]):
            budget.run("verification", lambda: verify(plan))
            row["status"] = "running"
            save()
            owner = f"cell-{index:04d}"
            task = budget.run("search-setup", lambda: prepare_task(plan["upstream"], row["task"], partition=plan["phase"],
                                seeds=plan["search_instance_seeds"], scale=DEFINITIONS[row["task"]]["scale"]*plan["scale_multiplier"],contract=plan["correctness_contract"]), owner=owner)
            row["task_metadata"] = task["metadata"]
            cell = root / f"cell-{index:04d}"
            cap = call_cap([row], plan["iterations"])
            transport = budget.run("transport-setup", lambda: CodexTransport(plan["codex"], plan["model"], cell / "model", cap), owner=owner)
            evaluator = WarmEvaluator(sandbox, task["metadata"]["class"], task["cases"], task["validate"],
                                      identity=digest(task["metadata"]), samples=plan["samples"], budget=budget)
            result = run_campaign(cell / "search", plan["dll"], plan["openevolve"], task["initial"], description(task["metadata"]),
                                  plan["model"], transport.generate_metered, evaluator, iterations=plan["iterations"], seed=row["seed"],
                                  evolution_profile=row["evolution_profile"], openevolve_profile=row["openevolve_profile"],
                                  tracks=row["tracks"], evidence_class="development-experiment", evaluator_manifest=evaluator.manifest,
                                  accounting=budget, accounting_owner=owner)
            row["search_status"] = result["status"]
            # Freeze all choices before generating any fresh validation/timing inputs.
            (cell / "selections.json").write_bytes(encode([{k:r[k] for k in ("mode","method","selected_hash")} for r in result["runs"]]))
            row["model_calls"] = transport.calls
            report["model_calls"] += transport.calls
            report["cumulative_model_calls"] += transport.calls
            row["search_runs"] = result["runs"]
            save()
            if transport.failed or any(any(r["independent_counters"]["unknown"].values()) for r in result["runs"]):
                raise RuntimeError("Unknown/failed provider work; stop the study without retries")
            fresh = budget.run("diagnostic-setup", lambda: prepare_task(plan["upstream"], row["task"], partition=plan["phase"],
                                 seeds=plan["diagnostic_instance_seeds"], scale=task["metadata"]["scale"],contract=plan["correctness_contract"]), owner=owner)
            confirmation = WarmEvaluator(sandbox, fresh["metadata"]["class"], fresh["cases"], fresh["validate"],
                                         identity=digest(fresh["metadata"]), samples=plan["samples"], phase="confirmation", budget=budget, stage="diagnostic")
            audits = [budget.run("audit-setup", lambda: prepare_task(plan["upstream"], row["task"], partition=plan["phase"], seeds=plan["audit_instance_seeds"][i:i+2],
                                   scale=task["metadata"]["scale"],contract=plan["correctness_contract"]), owner=owner) for i in range(0,8,2)]
            for track_index, track in enumerate(result["runs"]):
                order = ["original", "selected"]
                random.Random(row["seed"]*31+track_index).shuffle(order)
                track_owner = f"{owner}/{track['mode']}/{track['method']}"
                values = {role:confirmation(task["initial"] if role == "original" else track["selected_code"], owner=track_owner) for role in order}
                if values["original"]["status"] != "valid":
                    raise RuntimeError("Original failed fresh validation; retain the failed planned grid")
                audit_rows, original_audits, audit_bindings = [], [], []
                for audit in audits:
                    check = WarmEvaluator(sandbox, audit["metadata"]["class"], audit["cases"], audit["validate"],
                                          identity=digest(audit["metadata"]), samples=1, phase="confirmation", budget=budget, stage="audit", owner=track_owner)
                    original_audits.append(check(task["initial"]))
                    audit_rows.append(check(track["selected_code"]))
                    audit_bindings.append(dict(input_sha256=check.manifest["input_sha256"],evaluator_sha256=digest(check.manifest)))
                # Persist even when the baseline audit fails and promotion aborts.
                (cell / f"acceptance-{track_index}.json").write_bytes(encode(dict(diagnostics=values,original_audits=original_audits,selected_audits=audit_rows)))
                expected = dict(diagnostic=dict(input_sha256=confirmation.manifest["input_sha256"],evaluator_sha256=digest(confirmation.manifest)),audits=audit_bindings)
                decision = promote(task["initial"],track["selected_code"],values,audit_rows,original_audits,expected=expected,search_failed=bool(track.get("fallback")))
                fallback, deployed = decision["fallback"],decision["deployed"]
                row["pairs"].append(dict(mode=track["mode"], method=track["method"], order=order, **values, audits=audit_rows,original_audits=original_audits,
                                         deployed_hash=decision["deployed_hash"],
                                         fallback=fallback, deployed_seconds=deployed["duration_seconds"],
                                         deployed=deployed, speedup=1.0 if fallback else values["original"]["duration_seconds"]/deployed["duration_seconds"],
                                         model_tokens=track["actual_model_tokens"], model_calls=track["independent_counters"]["attempted"]["model"],
                                         search_wall_seconds=track["search_wall_seconds"],
                                         search_evaluation_seconds=track["independent_counters"]["evaluation_seconds"]))
                save()
            budget.run("verification", lambda: verify(plan))
            row["status"] = "completed"
            save()
            print(json.dumps(dict(cell=index, task=row["task"], seed=row["seed"], calls=report["model_calls"])), flush=True)
            transport = None
        report["status"] = "completed"
    except Exception as error:
        report.update(status="failed", error=type(error).__name__ + ": " + str(error)[:500])
        for row in report["rows"]:
            if row["status"] == "running":
                row["status"] = "failed"
                if transport and "model_calls" not in row:
                    row["model_calls"] = transport.calls
                    report["model_calls"] += transport.calls
                    report["cumulative_model_calls"] += transport.calls
    finally:
        report["elapsed_seconds"] = time.monotonic() - started
        report["evaluator_attempts"] = len(sandbox.rows) if sandbox else 0
        report["cumulative_container_attempts"] = plan["cumulative_containers_before"] + report["evaluator_attempts"]
        report["accounting"] = budget.snapshot()
        report["unknown_work"] = sum(r["unknown_work"] for r in sandbox.rows) if sandbox else 0
        report["unknown_work"] += sum(sum(r["independent_counters"]["unknown"].values()) for cell in report["rows"] for r in cell.get("search_runs", []))
        if transport and transport.failed:
            report["unknown_work"] += 1
        report["unknown_work"] += sum(r["status"] != "completed" for r in report["accounting"]["rows"])
        if report["status"] == "completed":
            try:
                validate_accounting(report)
            except Exception as error:
                report.update(status="failed", error="Accounting: " + str(error)[:500])
        save()
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare_parser = commands.add_parser("prepare")
    for name in ("output", "upstream", "openevolve", "dll", "codex", "image"):
        prepare_parser.add_argument("--"+name, required=True)
    prepare_parser.add_argument("--phase", choices=("development", "selection", "final"), default="development")
    prepare_parser.add_argument("--previous")
    prepare_parser.add_argument("--call-limit", type=int, default=412)
    prepare_parser.add_argument("--container-limit", type=int, required=True, help="Cumulative container attempts including repetitions and both-executable final audits")
    prepare_parser.add_argument("--budget-authorization", help="Reference to user approval for a changed cumulative cap before the next partition")
    execute_parser = commands.add_parser("execute")
    execute_parser.add_argument("--plan-path", required=True)
    execute_parser.add_argument("--registered-sha256", required=True)
    args = vars(parser.parse_args())
    action = args.pop("command")
    result = prepare(**args) if action == "prepare" else execute(**args)
    print(json.dumps(result if action == "prepare" else {k:result[k] for k in ("status","model_calls","unknown_work","claim")}))
    raise SystemExit(1 if result.get("status") == "failed" else 0)
