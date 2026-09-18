"""Public sorting screening-on/off ablation. No model, sealed data or competitor run."""
import argparse
import random
import time
from pathlib import Path

from docker_sandbox import encode
from program_controls import candidate_hash
from warm_budget import CampaignBudget
from warm_evaluator import WarmEvaluator
from warm_sandbox import WarmDockerSandbox
from warm_screening import ScreenedEvaluator, policy
from warm_study_design import digest


def program(repetitions):
    return ("class Solver:\n def solve(self,p):\n"
            f"  for _ in range({repetitions}):\n   result=sorted(p['values'])\n  return result\n")


def retained_fraction(full_valid, after):
    retained_ids = {r["candidate_hash"] for r in after if r["status"] == "valid"}
    retained = [r for r in full_valid if r["candidate_hash"] in retained_ids]
    if not retained:
        raise ValueError("No valid full-fidelity candidate retained")
    return min(r["duration_seconds"] for r in full_valid)/min(r["duration_seconds"] for r in retained)


def run(output, image):
    setup_started = time.monotonic()
    root = Path(output)
    root.mkdir(parents=True,exist_ok=False)
    rng = random.Random(61023)
    small = [dict(values=[rng.randrange(100000) for _ in range(1024)])]
    full = [dict(values=[rng.randrange(100000) for _ in range(1024)]) for _ in range(2)]
    initial = program("30")
    fast = [program("1")+f"# fast-{i}\n" for i in range(4)]
    slow = [program("4000")+f"# slow-{i}\n" for i in range(12)]
    crossover = program("4000 if len(p['values']) < 512 else 1")
    panels = dict(reject_heavy=slow+fast,all_advance=fast,size_crossover=[crossover])
    frozen = dict(schema="public-screening-ablation-v2",screening_policy=policy(True),seed=917,
                  initial=initial,panels=panels,screen_cases=small,full_cases=full,samples=3,
                  intent="Measure total overhead and useful rejections, not optimize a preset from these results")
    (root/"registration.json").write_bytes(encode(frozen))
    shared_setup_seconds = time.monotonic()-setup_started
    reports = []
    for panel,codes in panels.items():
        arms = [False,True]
        rng.shuffle(arms)
        paired = {}
        # Both arms receive identical candidate order and the same worst-case caps.
        limits = dict(model_calls=0,search_containers=4*(len(codes)+1)+2,confirmation_containers=12)
        for enabled in arms:
            started = time.monotonic()
            directory = root/f"{panel}-{'on' if enabled else 'off'}"
            directory.mkdir()
            budget = CampaignBudget(directory/"journal.jsonl",limits,digest(frozen))
            sandbox = budget.run("sandbox-setup",lambda:WarmDockerSandbox(image,directory/"sandbox"))
            def evaluator(cases,samples,phase,stage):
                expected = [sorted(case["values"]) for case in cases]
                return WarmEvaluator(sandbox,"Solver",cases,lambda value:value==expected,identity=digest(cases),
                                     samples=samples,phase=phase,stage=stage,budget=budget)
            full_evaluator = evaluator(full,3,"search","search")
            cascade = (ScreenedEvaluator(initial,evaluator(small,1,"search","screen"),full_evaluator,
                evaluator(full,1,"confirmation","rejection-audit"),policy(True),tracks=1,seed=917) if enabled else None)
            evaluate = cascade or full_evaluator
            observations = [evaluate(code,owner="public/sorting") for code in [initial,*codes]]
            audits = cascade.finish() if cascade else {}
            accounting = budget.snapshot()
            if accounting["closed"] or any(accounting["pending"].values()) or any(r["unknown_work"] for r in observations):
                raise ValueError("Public pilot has unknown work")
            result = dict(enabled=enabled,observations=observations,audits=audits,accounting=accounting,
                          elapsed_seconds=time.monotonic()-started,containers=len(sandbox.rows),
                          cpu_seconds=sum(r["observations"].get("cpu_seconds_through_response",0) for r in accounting["rows"]))
            if result["containers"] != sum(accounting["spent"].values()):
                raise ValueError("Pilot budget does not reconcile physical attempts")
            (directory/"result.json").write_bytes(encode(result))
            paired[enabled] = result
        before,after = paired[False],paired[True]
        rejected = {r["candidate_hash"] for r in after["observations"] if r.get("screening",{}).get("full",True) is None}
        baseline = before["observations"][0]["duration_seconds"]
        full_valid = [r for r in before["observations"] if r["status"] == "valid"]
        if len(full_valid) != len(codes)+1:
            raise ValueError("Every public sorting candidate must be correct at full scale")
        useful = {r["candidate_hash"] for r in full_valid[1:] if r["duration_seconds"] < baseline*.8}
        reports.append(dict(panel=panel,candidates=len(codes),before_containers=before["containers"],after_containers=after["containers"],
            before_wall_seconds=before["elapsed_seconds"],after_wall_seconds=after["elapsed_seconds"],
            before_cpu_seconds=before["cpu_seconds"],after_cpu_seconds=after["cpu_seconds"],
            rejected=len(rejected),descriptive_useful_candidates=len(useful),descriptive_useful_rejected=len(useful & rejected),
            retained_best_fraction_of_full_run=retained_fraction(full_valid,after["observations"]),
            audited=after["audits"]["public/sorting"]["summary"],
            interpretation="Useful means >20% faster median in the separate full arm; descriptive, not a significance claim"))
    result = dict(schema=frozen["schema"],registration_sha256=digest(frozen),panels=reports,model_calls=0,
                  shared_setup_wall_seconds=shared_setup_seconds,default_enabled=False,claim="none")
    (root/"summary.json").write_bytes(encode(result))
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output",required=True)
    parser.add_argument("--image",required=True)
    args = parser.parse_args()
    print(encode(run(args.output,args.image)).decode())
